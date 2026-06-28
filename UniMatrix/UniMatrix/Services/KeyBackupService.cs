using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using UniMatrix.Crypto;
using UniMatrix.Data;

namespace UniMatrix.Services
{
    /// <summary>
    /// Server-side Megolm key backup (algorithm m.megolm_backup.v1.curve25519-aes-sha2). The backup
    /// is a Curve25519 public key on the server; inbound Megolm session keys are encrypted to it (via
    /// libolm PkEncryption) and uploaded, so they can be restored after a fresh login. The matching
    /// private key is the SSSS secret "m.megolm_backup.v1" (see SsssService); on its own this service
    /// produces/consumes a base58 "recovery key" the user can write down.
    ///
    /// Crypto-dependent work is gated behind CRYPTO (libolm); on other platforms the service is an
    /// inert no-op so the rest of the app still compiles.
    /// </summary>
    internal class KeyBackupService
    {
        public const string Algorithm = "m.megolm_backup.v1.curve25519-aes-sha2";
        private const string MetaVersion = "backup_version";
        private const string MetaPubKey = "backup_pubkey";
        private const string MetaPrivKey = "backup_private_key"; // base64 of the 32-byte Curve25519 private key

        private readonly MatrixDatabase _db;
        private readonly MatrixClient _client;
        private readonly CryptoService _crypto;

        /// <summary>The active backup version, or null when no usable backup is configured.</summary>
        public string Version { get; private set; }

        /// <summary>The backup's Curve25519 public key (base64), or null.</summary>
        public string PublicKey { get; private set; }

        /// <summary>True when a backup version exists AND we hold its private key (so we can upload).</summary>
        public bool Enabled { get { return !string.IsNullOrEmpty(Version) && _hasPrivateKey; } }

        /// <summary>True when the server has a backup version we have not unlocked yet (needs recovery).</summary>
        public bool ExistsButLocked { get { return !string.IsNullOrEmpty(Version) && !_hasPrivateKey; } }

        private bool _hasPrivateKey;

        public KeyBackupService(MatrixDatabase db, MatrixClient client, CryptoService crypto)
        {
            _db = db;
            _client = client;
            _crypto = crypto;
        }

        // ---- Recovery-key codec (shared with SSSS) ----
        // A recovery key is base58(0x8B 0x01 || 32-byte key || parity), parity = XOR of all prior bytes.

        /// <summary>Encodes a 32-byte secret as a grouped base58 recovery key for the user to record.</summary>
        public static string EncodeRecoveryKey(byte[] key)
        {
            if (key == null || key.Length != 32) return null;
            var buf = new byte[35];
            buf[0] = 0x8B;
            buf[1] = 0x01;
            Array.Copy(key, 0, buf, 2, 32);
            byte parity = 0;
            for (int i = 0; i < 34; i++) parity ^= buf[i];
            buf[34] = parity;

            string raw = CryptoMath.Base58Encode(buf);
            // Group into space-separated blocks of 4 for readability (Element style).
            var sb = new System.Text.StringBuilder(raw.Length + raw.Length / 4);
            for (int i = 0; i < raw.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append(' ');
                sb.Append(raw[i]);
            }
            return sb.ToString();
        }

        /// <summary>Decodes a base58 recovery key (spaces ignored) to its 32-byte secret, validating
        /// the prefix and parity. Returns false on any malformed input.</summary>
        public static bool TryDecodeRecoveryKey(string recoveryKey, out byte[] key)
        {
            key = null;
            if (string.IsNullOrEmpty(recoveryKey)) return false;
            string compact = recoveryKey.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
            byte[] buf;
            try { buf = CryptoMath.Base58Decode(compact); }
            catch { return false; }
            if (buf == null || buf.Length != 35) return false;
            if (buf[0] != 0x8B || buf[1] != 0x01) return false;
            byte parity = 0;
            for (int i = 0; i < 34; i++) parity ^= buf[i];
            if (parity != buf[34]) return false;
            var result = new byte[32];
            Array.Copy(buf, 2, result, 0, 32);
            key = result;
            return true;
        }

#if CRYPTO
        /// <summary>Reads any existing server backup and our locally-stored key material so Enabled /
        /// ExistsButLocked reflect reality. Safe to call repeatedly.</summary>
        public async Task<bool> LoadAsync()
        {
            try
            {
                var info = await _client.GetBackupVersionAsync();
                if (info == null) { Version = null; PublicKey = null; _hasPrivateKey = false; return false; }

                string alg = MatrixClient.GetString(info, "algorithm");
                if (alg != Algorithm) { Version = null; PublicKey = null; _hasPrivateKey = false; return false; }

                Version = MatrixClient.GetString(info, "version");
                var authData = CryptoService.GetObj(info, "auth_data");
                PublicKey = authData != null ? MatrixClient.GetString(authData, "public_key") : null;

                // Do we already hold the matching private key locally?
                string storedVersion = _db.GetMeta(MetaVersion);
                string storedPriv = _db.GetMeta(MetaPrivKey);
                _hasPrivateKey = !string.IsNullOrEmpty(storedPriv) && storedVersion == Version && PrivateKeyMatches(storedPriv, PublicKey);
                return true;
            }
            catch (Exception ex) { App.Log("CRYPTO: backup LoadAsync failed: " + ex.Message); return false; }
        }

        /// <summary>Creates a brand-new server backup, persists its private key locally, and returns a
        /// base58 recovery key for the user to write down. Returns null on failure.</summary>
        public async Task<string> CreateBackupAsync()
        {
            try
            {
                byte[] priv = CryptoMath.RandomBytes(32);
                string pub;
                using (var dec = OlmPkDecryption.FromPrivateKey(priv)) { pub = dec.PublicKey; }

                var authData = new JsonObject { ["public_key"] = JsonValue.CreateStringValue(pub) };
                _crypto.SignObject(authData);

                string version = await _client.CreateBackupVersionAsync(Algorithm, authData);
                if (string.IsNullOrEmpty(version)) return null;

                Version = version;
                PublicKey = pub;
                _hasPrivateKey = true;
                _db.SetMeta(MetaVersion, version);
                _db.SetMeta(MetaPubKey, pub);
                _db.SetMeta(MetaPrivKey, CryptoMath.ToBase64(priv));

                // Seed the backup with every session we already hold.
                await BackupAllAsync();
                return EncodeRecoveryKey(priv);
            }
            catch (Exception ex) { App.Log("CRYPTO: CreateBackupAsync failed: " + ex.Message); return null; }
        }

        /// <summary>Uploads a single inbound Megolm session into the active backup. No-op when the
        /// backup isn't enabled.</summary>
        public async Task BackupSessionAsync(StoredInboundGroupSession s)
        {
            if (!Enabled || s == null || string.IsNullOrEmpty(PublicKey)) return;
            try
            {
                uint firstIndex;
                string exported = _crypto.ExportInboundSession(s, out firstIndex);
                if (string.IsNullOrEmpty(exported)) return;

                var plain = new JsonObject
                {
                    ["algorithm"] = JsonValue.CreateStringValue("m.megolm.v1.aes-sha2"),
                    ["sender_key"] = JsonValue.CreateStringValue(s.SenderKey ?? ""),
                    ["sender_claimed_keys"] = new JsonObject { ["ed25519"] = JsonValue.CreateStringValue(s.Ed25519 ?? "") },
                    ["forwarding_curve25519_key_chain"] = new JsonArray(),
                    ["session_key"] = JsonValue.CreateStringValue(exported)
                };

                string mac, ephemeral, ciphertext;
                using (var enc = new OlmPkEncryption())
                {
                    enc.SetRecipientKey(PublicKey);
                    ciphertext = enc.Encrypt(plain.Stringify(), out mac, out ephemeral);
                }

                var keyData = new JsonObject
                {
                    ["first_message_index"] = JsonValue.CreateNumberValue(firstIndex),
                    ["forwarded_count"] = JsonValue.CreateNumberValue(0),
                    ["is_verified"] = JsonValue.CreateBooleanValue(false),
                    ["session_data"] = new JsonObject
                    {
                        ["ciphertext"] = JsonValue.CreateStringValue(ciphertext),
                        ["mac"] = JsonValue.CreateStringValue(mac),
                        ["ephemeral"] = JsonValue.CreateStringValue(ephemeral)
                    }
                };

                await _client.BackupKeyPutAsync(Version, s.RoomId, s.SessionId, keyData);
            }
            catch (Exception ex) { App.Log("CRYPTO: BackupSessionAsync failed: " + ex.Message); }
        }

        /// <summary>Uploads every locally-held inbound Megolm session into the backup.</summary>
        public async Task BackupAllAsync()
        {
            if (!Enabled) return;
            try
            {
                var all = _db.GetAllInboundGroupSessions();
                if (all == null) return;
                foreach (var s in all) await BackupSessionAsync(s);
                App.Log("CRYPTO: backed up " + all.Count + " megolm session(s)");
            }
            catch (Exception ex) { App.Log("CRYPTO: BackupAllAsync failed: " + ex.Message); }
        }

        /// <summary>Restores all sessions from the server backup using a recovery private key. Returns
        /// the number of new sessions imported, or -1 on error / key mismatch.</summary>
        public async Task<int> RestoreAsync(byte[] privateKey)
        {
            if (privateKey == null || privateKey.Length != 32) return -1;
            try
            {
                if (string.IsNullOrEmpty(Version)) await LoadAsync();
                if (string.IsNullOrEmpty(Version)) { App.Log("CRYPTO: no server key backup version (or unsupported algorithm) -> cannot restore"); return -1; }

                using (var dec = OlmPkDecryption.FromPrivateKey(privateKey))
                {
                    if (!string.IsNullOrEmpty(PublicKey) && dec.PublicKey != PublicKey)
                    {
                        App.Log("CRYPTO: recovery key does not match backup public key (derivedPub=" + dec.PublicKey + " backupPub=" + PublicKey + ")");
                        return -1;
                    }

                    var all = await _client.BackupKeysGetAsync(Version);
                    var rooms = CryptoService.GetObj(all, "rooms");
                    if (rooms == null) return 0;

                    int imported = 0;
                    foreach (var roomId in rooms.Keys)
                    {
                        var roomObj = rooms.GetNamedObject(roomId, null);
                        var sessions = roomObj != null ? CryptoService.GetObj(roomObj, "sessions") : null;
                        if (sessions == null) continue;

                        foreach (var sessionId in sessions.Keys)
                        {
                            var entry = sessions.GetNamedObject(sessionId, null);
                            var sd = entry != null ? CryptoService.GetObj(entry, "session_data") : null;
                            if (sd == null) continue;

                            string ephemeral = MatrixClient.GetString(sd, "ephemeral");
                            string mac = MatrixClient.GetString(sd, "mac");
                            string ciphertext = MatrixClient.GetString(sd, "ciphertext");
                            if (string.IsNullOrEmpty(ciphertext)) continue;

                            string plainJson;
                            try { plainJson = dec.Decrypt(ephemeral, mac, ciphertext); }
                            catch (Exception ex) { App.Log("CRYPTO: backup decrypt failed for " + sessionId + ": " + ex.Message); continue; }

                            JsonObject plain;
                            if (!JsonObject.TryParse(plainJson, out plain)) continue;

                            string sessionKey = MatrixClient.GetString(plain, "session_key");
                            string senderKey = MatrixClient.GetString(plain, "sender_key");
                            var claimed = CryptoService.GetObj(plain, "sender_claimed_keys");
                            string ed25519 = claimed != null ? MatrixClient.GetString(claimed, "ed25519") : null;
                            if (string.IsNullOrEmpty(sessionKey)) continue;

                            if (_crypto.ImportInboundSession(roomId, sessionKey, senderKey, ed25519, true)) imported++;
                        }
                    }

                    // We now own the private key — persist it so the backup stays enabled.
                    PublicKey = dec.PublicKey;
                    _hasPrivateKey = true;
                    _db.SetMeta(MetaVersion, Version);
                    _db.SetMeta(MetaPubKey, dec.PublicKey);
                    _db.SetMeta(MetaPrivKey, CryptoMath.ToBase64(privateKey));

                    App.Log("CRYPTO: restored " + imported + " megolm session(s) from backup");
                    return imported;
                }
            }
            catch (Exception ex) { App.Log("CRYPTO: RestoreAsync failed: " + ex.Message); return -1; }
        }

        /// <summary>Restores using a base58 recovery key string. Returns imported count or -1.</summary>
        public async Task<int> RestoreWithRecoveryKeyAsync(string recoveryKey)
        {
            byte[] priv;
            if (!TryDecodeRecoveryKey(recoveryKey, out priv)) return -1;
            return await RestoreAsync(priv);
        }

        /// <summary>The locally-held backup private key (32 bytes) or null. Used by SSSS to stash it.</summary>
        public byte[] GetPrivateKey()
        {
            string b64 = _db.GetMeta(MetaPrivKey);
            if (string.IsNullOrEmpty(b64)) return null;
            try { return CryptoMath.FromBase64(b64); }
            catch { return null; }
        }

        private static bool PrivateKeyMatches(string privBase64, string publicKey)
        {
            if (string.IsNullOrEmpty(privBase64) || string.IsNullOrEmpty(publicKey)) return false;
            try
            {
                byte[] priv = CryptoMath.FromBase64(privBase64);
                using (var dec = OlmPkDecryption.FromPrivateKey(priv)) { return dec.PublicKey == publicKey; }
            }
            catch { return false; }
        }
#else
        // Non-ARM builds: key backup is unavailable; everything is a safe no-op.
        public Task<bool> LoadAsync() { return Task.FromResult(false); }
        public Task<string> CreateBackupAsync() { return Task.FromResult<string>(null); }
        public Task BackupSessionAsync(StoredInboundGroupSession s) { return Task.FromResult(0); }
        public Task BackupAllAsync() { return Task.FromResult(0); }
        public Task<int> RestoreAsync(byte[] privateKey) { return Task.FromResult(-1); }
        public Task<int> RestoreWithRecoveryKeyAsync(string recoveryKey) { return Task.FromResult(-1); }
        public byte[] GetPrivateKey() { return null; }
#endif
    }
}
