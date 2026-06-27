using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using UniMatrix.Crypto;

namespace UniMatrix.Services
{
    /// <summary>
    /// Secure Secret Storage and Sharing (SSSS, m.secret_storage.v1.aes-hmac-sha2). Stores the
    /// Megolm key-backup private key as an account-data secret encrypted under a key derived from a
    /// recovery key (base58) or a passphrase (PBKDF2-SHA512). On a fresh login this lets the user
    /// unlock the server key backup and restore their message history.
    ///
    /// All crypto here is the platform-independent UWP CNG primitives in <see cref="CryptoMath"/>
    /// (AES-CTR, HMAC-SHA-256, HKDF, PBKDF2), so this type compiles on every architecture; the only
    /// crypto-gated step is handing the recovered private key to <see cref="KeyBackupService"/>.
    /// </summary>
    internal class SsssService
    {
        public const string AlgAesHmacSha2 = "m.secret_storage.v1.aes-hmac-sha2";
        public const string AlgPbkdf2 = "m.pbkdf2";
        private const string DefaultKeyType = "m.secret_storage.default_key";
        private const string KeyTypePrefix = "m.secret_storage.key.";
        private const string BackupSecretName = "m.megolm_backup.v1";
        private const int DefaultPbkdf2Iterations = 500000; // matches Element; recovery shows progress

        private readonly MatrixClient _client;
        private readonly KeyBackupService _backup;

        // After a successful Recover/SetUp, the unlocked SSSS key + its id are cached for this
        // session so other secrets (cross-signing master/self-signing/user-signing private keys)
        // can be fetched without re-prompting the user for the recovery key.
        private byte[] _unlockedKey;
        private string _unlockedKeyId;

        public SsssService(MatrixClient client, KeyBackupService backup)
        {
            _client = client;
            _backup = backup;
        }

        /// <summary>True once the SSSS key has been unlocked this session (Recover/SetUp succeeded).</summary>
        public bool IsUnlocked { get { return _unlockedKey != null; } }

        /// <summary>True when the account already has a default SSSS key configured.</summary>
        public async Task<bool> HasDefaultKeyAsync()
        {
            try
            {
                var def = await _client.AccountDataGetAsync(DefaultKeyType);
                return def != null && !string.IsNullOrEmpty(MatrixClient.GetString(def, "key"));
            }
            catch { return false; }
        }

        /// <summary>
        /// Sets up secret storage: derives/generates the SSSS key (from a passphrase if supplied,
        /// else random), publishes the key description + default-key pointer, and stores the current
        /// backup private key as the m.megolm_backup.v1 secret. Returns a base58 recovery key for the
        /// user to write down, or null on failure.
        /// </summary>
        public async Task<string> SetUpRecoveryAsync(string passphrase)
        {
            try
            {
                byte[] ssssKey;
                JsonObject passphraseInfo = null;

                if (!string.IsNullOrEmpty(passphrase))
                {
                    byte[] salt = CryptoMath.RandomBytes(32);
                    ssssKey = CryptoMath.Pbkdf2Sha512(passphrase, salt, DefaultPbkdf2Iterations, 32);
                    passphraseInfo = new JsonObject
                    {
                        ["algorithm"] = JsonValue.CreateStringValue(AlgPbkdf2),
                        ["salt"] = JsonValue.CreateStringValue(CryptoMath.ToUnpaddedBase64(salt)),
                        ["iterations"] = JsonValue.CreateNumberValue(DefaultPbkdf2Iterations),
                        ["bits"] = JsonValue.CreateNumberValue(256)
                    };
                }
                else
                {
                    ssssKey = CryptoMath.RandomBytes(32);
                }

                // Key description with a self-check (iv+mac over 32 zero bytes, empty name).
                byte[] checkIv = MakeIv();
                string checkMac = ComputeMac(ssssKey, "", checkIv, new byte[32]);
                var keyDesc = new JsonObject
                {
                    ["algorithm"] = JsonValue.CreateStringValue(AlgAesHmacSha2),
                    ["iv"] = JsonValue.CreateStringValue(CryptoMath.ToUnpaddedBase64(checkIv)),
                    ["mac"] = JsonValue.CreateStringValue(checkMac)
                };
                if (passphraseInfo != null) keyDesc["passphrase"] = passphraseInfo;

                string keyId = NewKeyId();
                await _client.AccountDataPutAsync(KeyTypePrefix + keyId, keyDesc);
                await _client.AccountDataPutAsync(DefaultKeyType, new JsonObject
                {
                    ["key"] = JsonValue.CreateStringValue(keyId)
                });

                // Remember the unlocked key so cross-signing secrets can be stored/fetched.
                _unlockedKey = ssssKey;
                _unlockedKeyId = keyId;

                // Stash the backup private key (if we have one) as a secret.
                byte[] backupPriv = _backup != null ? _backup.GetPrivateKey() : null;
                if (backupPriv != null)
                {
                    var secret = EncryptData(ssssKey, BackupSecretName, Encoding.UTF8.GetBytes(CryptoMath.ToBase64(backupPriv)));
                    await _client.AccountDataPutAsync(BackupSecretName, new JsonObject
                    {
                        ["encrypted"] = new JsonObject { [keyId] = secret }
                    });
                }

                return KeyBackupService.EncodeRecoveryKey(ssssKey);
            }
            catch (Exception ex) { App.Log("CRYPTO: SetUpRecoveryAsync failed: " + ex.Message); return null; }
        }

        /// <summary>
        /// Unlocks secret storage with a recovery key or passphrase, decrypts the backup private key,
        /// and restores message history from the server backup. Returns the number of restored Megolm
        /// sessions, or -1 on failure (wrong key / no backup / no secret).
        /// </summary>
        public async Task<int> RecoverAsync(string recoveryKeyOrPassphrase)
        {
            try
            {
                var def = await _client.AccountDataGetAsync(DefaultKeyType);
                string keyId = def != null ? MatrixClient.GetString(def, "key") : null;
                if (string.IsNullOrEmpty(keyId)) { App.Log("CRYPTO: no SSSS default key"); return -1; }

                var keyDesc = await _client.AccountDataGetAsync(KeyTypePrefix + keyId);
                if (keyDesc == null) { App.Log("CRYPTO: SSSS key description missing"); return -1; }

                byte[] ssssKey = DeriveKey(recoveryKeyOrPassphrase, keyDesc);
                if (ssssKey == null) { App.Log("CRYPTO: could not derive SSSS key"); return -1; }
                if (!VerifyKey(ssssKey, keyDesc)) { App.Log("CRYPTO: SSSS key check failed"); return -1; }

                // Cache the verified key + id so cross-signing secrets unlock without re-prompting.
                _unlockedKey = ssssKey;
                _unlockedKeyId = keyId;

                var secretData = await _client.AccountDataGetAsync(BackupSecretName);
                var encrypted = secretData != null ? CryptoService.GetObj(secretData, "encrypted") : null;
                var entry = encrypted != null ? CryptoService.GetObj(encrypted, keyId) : null;
                if (entry == null) { App.Log("CRYPTO: backup secret not stored in SSSS"); return -1; }

                byte[] plain = DecryptData(ssssKey, BackupSecretName, entry);
                if (plain == null) { App.Log("CRYPTO: backup secret decrypt/mac failed"); return -1; }

                byte[] backupPriv;
                try { backupPriv = CryptoMath.FromBase64(Encoding.UTF8.GetString(plain)); }
                catch { App.Log("CRYPTO: backup secret not valid base64"); return -1; }

                if (_backup == null) return -1;
                return await _backup.RestoreAsync(backupPriv);
            }
            catch (Exception ex) { App.Log("CRYPTO: RecoverAsync failed: " + ex.Message); return -1; }
        }

        /// <summary>
        /// Fetches and decrypts an arbitrary SSSS secret (e.g. m.cross_signing.master) using the
        /// key unlocked this session. Returns the secret's plaintext string (typically unpadded
        /// base64 of a 32-byte seed), or null if locked / not stored / decrypt fails.
        /// </summary>
        public async Task<string> GetSecretAsync(string secretName)
        {
            try
            {
                if (_unlockedKey == null || string.IsNullOrEmpty(_unlockedKeyId)) return null;
                if (string.IsNullOrEmpty(secretName)) return null;

                var secretData = await _client.AccountDataGetAsync(secretName);
                var encrypted = secretData != null ? CryptoService.GetObj(secretData, "encrypted") : null;
                var entry = encrypted != null ? CryptoService.GetObj(encrypted, _unlockedKeyId) : null;
                if (entry == null) { App.Log("CRYPTO: secret " + secretName + " not in SSSS"); return null; }

                byte[] plain = DecryptData(_unlockedKey, secretName, entry);
                if (plain == null) { App.Log("CRYPTO: secret " + secretName + " decrypt/mac failed"); return null; }
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) { App.Log("CRYPTO: GetSecretAsync(" + secretName + ") failed: " + ex.Message); return null; }
        }

        /// <summary>Forgets the unlocked SSSS key (call on logout).</summary>
        public void Lock()
        {
            if (_unlockedKey != null) Array.Clear(_unlockedKey, 0, _unlockedKey.Length);
            _unlockedKey = null;
            _unlockedKeyId = null;
        }

        // ---- key derivation ----

        private static byte[] DeriveKey(string input, JsonObject keyDesc)
        {
            // A recovery key wins if it parses; otherwise treat the input as a passphrase.
            byte[] key;
            if (KeyBackupService.TryDecodeRecoveryKey(input, out key)) return key;

            var passphrase = CryptoService.GetObj(keyDesc, "passphrase");
            if (passphrase == null) return null;

            string salt64 = MatrixClient.GetString(passphrase, "salt");
            int iterations = (int)MatrixClient.GetNumber(passphrase, "iterations");
            if (string.IsNullOrEmpty(salt64) || iterations <= 0) return null;

            byte[] salt = Decode64(salt64);
            return CryptoMath.Pbkdf2Sha512(input, salt, iterations, 32);
        }

        private static bool VerifyKey(byte[] ssssKey, JsonObject keyDesc)
        {
            string iv64 = MatrixClient.GetString(keyDesc, "iv");
            string mac64 = MatrixClient.GetString(keyDesc, "mac");
            if (string.IsNullOrEmpty(mac64)) return false;
            byte[] iv = string.IsNullOrEmpty(iv64) ? new byte[16] : Decode64(iv64);
            string mac = ComputeMac(ssssKey, "", iv, new byte[32]);
            return CryptoMath.ConstantTimeEquals(Decode64(mac), Decode64(mac64));
        }

        // ---- AES-HMAC-SHA2 secret encryption ----

        private static JsonObject EncryptData(byte[] ssssKey, string name, byte[] plaintext)
        {
            byte[] iv = MakeIv();
            byte[] aesKey, hmacKey;
            DeriveAesHmac(ssssKey, name, out aesKey, out hmacKey);
            byte[] ciphertext = CryptoMath.AesCtr(aesKey, iv, plaintext);
            byte[] mac = CryptoMath.HmacSha256(hmacKey, ciphertext);
            return new JsonObject
            {
                ["iv"] = JsonValue.CreateStringValue(CryptoMath.ToUnpaddedBase64(iv)),
                ["ciphertext"] = JsonValue.CreateStringValue(CryptoMath.ToUnpaddedBase64(ciphertext)),
                ["mac"] = JsonValue.CreateStringValue(CryptoMath.ToUnpaddedBase64(mac))
            };
        }

        private static byte[] DecryptData(byte[] ssssKey, string name, JsonObject data)
        {
            string iv64 = MatrixClient.GetString(data, "iv");
            string ct64 = MatrixClient.GetString(data, "ciphertext");
            string mac64 = MatrixClient.GetString(data, "mac");
            if (string.IsNullOrEmpty(ct64)) return null;

            byte[] iv = Decode64(iv64);
            byte[] ciphertext = Decode64(ct64);
            byte[] aesKey, hmacKey;
            DeriveAesHmac(ssssKey, name, out aesKey, out hmacKey);

            byte[] mac = CryptoMath.HmacSha256(hmacKey, ciphertext);
            if (!CryptoMath.ConstantTimeEquals(mac, Decode64(mac64))) return null;

            return CryptoMath.AesCtr(aesKey, iv, ciphertext);
        }

        /// <summary>MAC produced when encrypting <paramref name="plaintext"/> under the given iv/name —
        /// used both for the key self-check and message authentication.</summary>
        private static string ComputeMac(byte[] ssssKey, string name, byte[] iv, byte[] plaintext)
        {
            byte[] aesKey, hmacKey;
            DeriveAesHmac(ssssKey, name, out aesKey, out hmacKey);
            byte[] ciphertext = CryptoMath.AesCtr(aesKey, iv, plaintext);
            byte[] mac = CryptoMath.HmacSha256(hmacKey, ciphertext);
            return CryptoMath.ToUnpaddedBase64(mac);
        }

        private static void DeriveAesHmac(byte[] ssssKey, string name, out byte[] aesKey, out byte[] hmacKey)
        {
            // HKDF-SHA256, zero salt, info = secret name (empty for the key self-check), 64 bytes.
            byte[] okm = CryptoMath.Hkdf(ssssKey, new byte[32], Encoding.UTF8.GetBytes(name ?? string.Empty), 64);
            aesKey = new byte[32];
            hmacKey = new byte[32];
            Buffer.BlockCopy(okm, 0, aesKey, 0, 32);
            Buffer.BlockCopy(okm, 32, hmacKey, 0, 32);
        }

        // ---- helpers ----

        private static byte[] MakeIv()
        {
            byte[] iv = CryptoMath.RandomBytes(16);
            // Clear the top bit so the 64-bit AES-CTR counter never overflows mid-message (matches
            // matrix-js-sdk).
            iv[8] &= 0x7f;
            return iv;
        }

        private static byte[] Decode64(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];
            // Accept padded or unpadded base64.
            return CryptoMath.FromUnpaddedBase64(s.TrimEnd('='));
        }

        private static string NewKeyId()
        {
            byte[] b = CryptoMath.RandomBytes(16);
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
