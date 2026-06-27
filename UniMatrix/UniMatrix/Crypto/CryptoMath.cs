using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;
// Windows.Storage.Streams also defines a 'Buffer' type, which collides with System.Buffer
// (used here for BlockCopy). Alias it so the unqualified name resolves to the BCL helper.
using Buffer = System.Buffer;

namespace UniMatrix.Crypto
{
    /// <summary>
    /// Symmetric crypto primitives used by SSSS (secret storage) and encrypted attachments,
    /// built on the UWP CNG providers (Windows.Security.Cryptography.Core). These are
    /// platform-independent (no libolm) so they compile on every architecture.
    ///
    /// Notable: AES-CTR is not a named UWP algorithm, so it is implemented here as AES-ECB over
    /// successive counter blocks. To stay byte-compatible with matrix-js-sdk (Element), the
    /// counter occupies only the low 64 bits of the 16-byte IV (the high 64 bits are a fixed
    /// nonce), matching SubtleCrypto's {name:"AES-CTR", length:64}.
    /// </summary>
    internal static class CryptoMath
    {
        private static readonly char[] Base58Alphabet =
            "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".ToCharArray();

        // ---- Hashing / HMAC --------------------------------------------------------------
        internal static byte[] Sha256(byte[] data)
        {
            var prov = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var hash = prov.HashData(CryptographicBuffer.CreateFromByteArray(data ?? new byte[0]));
            byte[] outBytes;
            CryptographicBuffer.CopyToByteArray(hash, out outBytes);
            return outBytes;
        }

        internal static byte[] HmacSha256(byte[] key, byte[] data)
        {
            var prov = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256);
            var hkey = prov.CreateKey(CryptographicBuffer.CreateFromByteArray(key ?? new byte[0]));
            var mac = CryptographicEngine.Sign(hkey, CryptographicBuffer.CreateFromByteArray(data ?? new byte[0]));
            byte[] outBytes;
            CryptographicBuffer.CopyToByteArray(mac, out outBytes);
            return outBytes;
        }

        /// <summary>HKDF (RFC 5869) over HMAC-SHA-256.</summary>
        internal static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length)
        {
            if (salt == null || salt.Length == 0) salt = new byte[32];
            byte[] prk = HmacSha256(salt, ikm ?? new byte[0]);

            var result = new byte[length];
            byte[] t = new byte[0];
            int pos = 0;
            byte counter = 1;
            while (pos < length)
            {
                var input = new byte[t.Length + (info?.Length ?? 0) + 1];
                Buffer.BlockCopy(t, 0, input, 0, t.Length);
                if (info != null && info.Length > 0) Buffer.BlockCopy(info, 0, input, t.Length, info.Length);
                input[input.Length - 1] = counter;
                t = HmacSha256(prk, input);
                int take = Math.Min(t.Length, length - pos);
                Buffer.BlockCopy(t, 0, result, pos, take);
                pos += take;
                counter++;
            }
            return result;
        }

        /// <summary>PBKDF2-HMAC-SHA-512 (used to derive an SSSS key from a passphrase).</summary>
        internal static byte[] Pbkdf2Sha512(string password, byte[] salt, int iterations, int dkLenBytes)
        {
            var prov = KeyDerivationAlgorithmProvider.OpenAlgorithm(KeyDerivationAlgorithmNames.Pbkdf2Sha512);
            IBuffer pwBuf = CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(password ?? string.Empty));
            var key = prov.CreateKey(pwBuf);
            var pbkdf2Params = KeyDerivationParameters.BuildForPbkdf2(
                CryptographicBuffer.CreateFromByteArray(salt ?? new byte[0]), (uint)iterations);
            IBuffer derived = CryptographicEngine.DeriveKeyMaterial(key, pbkdf2Params, (uint)dkLenBytes);
            byte[] outBytes;
            CryptographicBuffer.CopyToByteArray(derived, out outBytes);
            return outBytes;
        }

        // ---- AES-CTR (manual, 64-bit counter to match matrix-js-sdk) ----------------------
        internal static byte[] AesCtr(byte[] key, byte[] iv16, byte[] data)
        {
            if (iv16 == null || iv16.Length != 16) throw new ArgumentException("iv must be 16 bytes");
            var prov = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesEcb);
            var aesKey = prov.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(key));

            var counter = (byte[])iv16.Clone();
            var output = new byte[data.Length];
            var keystreamBlock = new byte[16];

            for (int offset = 0; offset < data.Length; offset += 16)
            {
                IBuffer encrypted = CryptographicEngine.Encrypt(
                    aesKey, CryptographicBuffer.CreateFromByteArray(counter), null);
                CryptographicBuffer.CopyToByteArray(encrypted, out keystreamBlock);

                int blockLen = Math.Min(16, data.Length - offset);
                for (int i = 0; i < blockLen; i++)
                    output[offset + i] = (byte)(data[offset + i] ^ keystreamBlock[i]);

                // Increment only the low 64 bits (bytes 8..15), big-endian — matches length:64.
                for (int i = 15; i >= 8; i--) { if (++counter[i] != 0) break; }
            }
            return output;
        }

        // ---- Base64 helpers ---------------------------------------------------------------
        internal static string ToBase64(byte[] data) { return Convert.ToBase64String(data ?? new byte[0]); }
        internal static byte[] FromBase64(string s) { return Convert.FromBase64String(s); }

        /// <summary>Unpadded base64 ("base64url"-style padding stripped) as used in Matrix JSON.</summary>
        internal static string ToUnpaddedBase64(byte[] data)
        {
            return ToBase64(data).TrimEnd('=');
        }

        internal static byte[] FromUnpaddedBase64(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];
            int pad = (4 - (s.Length % 4)) % 4;
            return Convert.FromBase64String(s + new string('=', pad));
        }

        /// <summary>Unpadded base64url (RFC 4648 §5, '-'/'_' alphabet) used for JWK key fields.</summary>
        internal static string ToBase64Url(byte[] data)
        {
            return ToBase64(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        internal static byte[] FromBase64Url(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];
            string t = s.Replace('-', '+').Replace('_', '/');
            int pad = (4 - (t.Length % 4)) % 4;
            return Convert.FromBase64String(t + new string('=', pad));
        }

        // ---- Base58 -----------------------------------------------------------------------
        internal static string Base58Encode(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;

            int zeros = 0;
            while (zeros < data.Length && data[zeros] == 0) zeros++;

            // Convert base-256 to base-58 via repeated division.
            var input = (byte[])data.Clone();
            var sb = new StringBuilder();
            int start = zeros;
            while (start < input.Length)
            {
                int remainder = 0;
                for (int i = start; i < input.Length; i++)
                {
                    int acc = (remainder << 8) + input[i];
                    input[i] = (byte)(acc / 58);
                    remainder = acc % 58;
                }
                sb.Append(Base58Alphabet[remainder]);
                if (input[start] == 0) start++;
            }
            for (int i = 0; i < zeros; i++) sb.Append(Base58Alphabet[0]);

            char[] arr = sb.ToString().ToCharArray();
            Array.Reverse(arr);
            return new string(arr);
        }

        internal static byte[] Base58Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];

            // Map characters to values.
            var indexes = new int[128];
            for (int i = 0; i < indexes.Length; i++) indexes[i] = -1;
            for (int i = 0; i < Base58Alphabet.Length; i++) indexes[Base58Alphabet[i]] = i;

            var input58 = new byte[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                int digit = c < 128 ? indexes[c] : -1;
                if (digit < 0) throw new FormatException("Invalid base58 character: " + c);
                input58[i] = (byte)digit;
            }

            int zeros = 0;
            while (zeros < input58.Length && input58[zeros] == 0) zeros++;

            var decoded = new byte[s.Length];
            int outPos = decoded.Length;
            int start = zeros;
            while (start < input58.Length)
            {
                int remainder = 0;
                for (int i = start; i < input58.Length; i++)
                {
                    int acc = (remainder * 58) + input58[i];
                    input58[i] = (byte)(acc / 256);
                    remainder = acc % 256;
                }
                decoded[--outPos] = (byte)remainder;
                if (input58[start] == 0) start++;
            }

            // Restore leading zeros.
            while (outPos < decoded.Length && decoded[outPos] == 0) outPos++;
            outPos -= zeros;

            int len = decoded.Length - outPos;
            var result = new byte[len];
            Buffer.BlockCopy(decoded, outPos, result, 0, len);
            return result;
        }

        // ---- Misc -------------------------------------------------------------------------
        internal static byte[] RandomBytes(int count)
        {
            var buf = CryptographicBuffer.GenerateRandom((uint)count);
            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(buf, out bytes);
            return bytes;
        }

        /// <summary>Constant-time byte-array comparison.</summary>
        internal static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
