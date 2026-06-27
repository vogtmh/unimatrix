using System;
using Windows.Data.Json;
using UniMatrix.Crypto;

namespace UniMatrix.Services
{
    /// <summary>
    /// Encrypts and decrypts media attachments for encrypted rooms, following the Matrix
    /// "m.encrypted" attachment scheme (spec v1.11, "End-to-end encryption" / encrypted
    /// attachments): AES-CTR-256 with a 64-bit counter, a SHA-256 integrity hash over the
    /// ciphertext, and a JWK ("oct"/A256CTR) key transported inside the event's <c>file</c> block.
    /// The crypto itself is CNG-based (see <see cref="CryptoMath"/>) so this works on every
    /// architecture; only the surrounding Megolm event encryption needs libolm.
    /// </summary>
    internal static class AttachmentCrypto
    {
        /// <summary>The result of encrypting an attachment: the bytes to upload and the partial
        /// <c>file</c> block (the mxc <c>url</c> is filled in by the caller after upload).</summary>
        internal class EncryptedAttachment
        {
            public byte[] Ciphertext;
            public JsonObject FileInfo; // file block without "url"
        }

        /// <summary>Encrypts plaintext bytes; returns the ciphertext plus the JWK/IV/hash metadata.</summary>
        public static EncryptedAttachment Encrypt(byte[] plaintext)
        {
            if (plaintext == null) plaintext = new byte[0];

            byte[] key = CryptoMath.RandomBytes(32);
            // IV: 8 random bytes followed by 8 zero counter bytes (matches matrix-js-sdk).
            byte[] iv = new byte[16];
            byte[] rnd = CryptoMath.RandomBytes(8);
            Buffer.BlockCopy(rnd, 0, iv, 0, 8);

            byte[] ciphertext = CryptoMath.AesCtr(key, iv, plaintext);
            byte[] sha = CryptoMath.Sha256(ciphertext);

            var jwk = new JsonObject
            {
                ["kty"] = JsonValue.CreateStringValue("oct"),
                ["alg"] = JsonValue.CreateStringValue("A256CTR"),
                ["ext"] = JsonValue.CreateBooleanValue(true),
                ["k"] = JsonValue.CreateStringValue(CryptoMath.ToBase64Url(key))
            };
            var keyOps = new JsonArray();
            keyOps.Add(JsonValue.CreateStringValue("encrypt"));
            keyOps.Add(JsonValue.CreateStringValue("decrypt"));
            jwk["key_ops"] = keyOps;

            var hashes = new JsonObject { ["sha256"] = JsonValue.CreateStringValue(CryptoMath.ToUnpaddedBase64(sha)) };

            var fileInfo = new JsonObject
            {
                ["v"] = JsonValue.CreateStringValue("v2"),
                ["key"] = jwk,
                ["iv"] = JsonValue.CreateStringValue(CryptoMath.ToUnpaddedBase64(iv)),
                ["hashes"] = hashes
            };

            return new EncryptedAttachment { Ciphertext = ciphertext, FileInfo = fileInfo };
        }

        /// <summary>
        /// Decrypts downloaded ciphertext using the event's <c>file</c> block. Verifies the SHA-256
        /// hash before decrypting and returns null if the block is malformed or the hash mismatches
        /// (a tampered or truncated download).
        /// </summary>
        public static byte[] Decrypt(byte[] ciphertext, JsonObject fileInfo)
        {
            if (ciphertext == null || fileInfo == null) return null;
            try
            {
                JsonObject jwk = fileInfo.ContainsKey("key") && fileInfo["key"].ValueType == JsonValueType.Object
                    ? fileInfo.GetNamedObject("key") : null;
                if (jwk == null) return null;

                string k = MatrixClient.GetString(jwk, "k");
                string ivB64 = MatrixClient.GetString(fileInfo, "iv");
                if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(ivB64)) return null;

                byte[] key = CryptoMath.FromBase64Url(k);
                byte[] iv = CryptoMath.FromUnpaddedBase64(ivB64);
                if (iv.Length != 16)
                {
                    // Some encoders pad the IV differently; normalise to 16 bytes.
                    var fixedIv = new byte[16];
                    Buffer.BlockCopy(iv, 0, fixedIv, 0, Math.Min(iv.Length, 16));
                    iv = fixedIv;
                }

                // Integrity check before decrypting.
                JsonObject hashes = fileInfo.ContainsKey("hashes") && fileInfo["hashes"].ValueType == JsonValueType.Object
                    ? fileInfo.GetNamedObject("hashes") : null;
                string expected = hashes != null ? MatrixClient.GetString(hashes, "sha256") : null;
                if (!string.IsNullOrEmpty(expected))
                {
                    byte[] actual = CryptoMath.Sha256(ciphertext);
                    if (!CryptoMath.ConstantTimeEquals(actual, CryptoMath.FromUnpaddedBase64(expected)))
                    {
                        App.Log("ATTACH: sha256 mismatch; refusing to decrypt");
                        return null;
                    }
                }

                return CryptoMath.AesCtr(key, iv, ciphertext);
            }
            catch (Exception ex)
            {
                App.Log("ATTACH: decrypt failed: " + ex.Message);
                return null;
            }
        }
    }
}
