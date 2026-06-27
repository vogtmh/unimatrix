#if CRYPTO
using System;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Security.Cryptography;

namespace UniMatrix.Services
{
    /// <summary>Thrown when a libolm call returns its error sentinel.</summary>
    internal sealed class OlmException : Exception
    {
        public OlmException(string message) : base(message) { }
    }

    /// <summary>
    /// Small helpers shared by the libolm wrappers: size_t conversion, random-byte generation
    /// (libolm never sources its own entropy — the caller must), and the recurring "ask for the
    /// length, allocate, fill" buffer dance.
    /// </summary>
    internal static class OlmHelp
    {
        internal static readonly UIntPtr Error = OlmNative.olm_error();

        internal static UIntPtr Sz(long v) { return new UIntPtr((ulong)v); }
        internal static long Len(UIntPtr v) { return (long)v.ToUInt64(); }

        internal static bool IsError(UIntPtr v) { return v.ToUInt64() == Error.ToUInt64(); }

        /// <summary>Cryptographically secure random bytes from the UWP CNG provider.</summary>
        internal static byte[] Random(long count)
        {
            if (count <= 0) return new byte[0];
            var buf = CryptographicBuffer.GenerateRandom((uint)count);
            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(buf, out bytes);
            return bytes;
        }

        internal static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s ?? string.Empty); }
        internal static string FromUtf8(byte[] b, long len) { return Encoding.UTF8.GetString(b, 0, (int)len); }
    }

    /// <summary>Base class owning the unmanaged memory block that backs a libolm object.</summary>
    internal abstract class OlmObject : IDisposable
    {
        protected IntPtr Ptr;        // the OlmAccount*/OlmSession*/... handle
        private IntPtr _memory;      // the AllocHGlobal block (libolm writes into this)
        private bool _disposed;

        protected OlmObject(long size)
        {
            _memory = Marshal.AllocHGlobal((int)size);
            Ptr = Create(_memory);
        }

        /// <summary>Calls the matching olm_*(memory) constructor and returns the object pointer.</summary>
        protected abstract IntPtr Create(IntPtr memory);

        /// <summary>Calls the matching olm_clear_*(ptr) before the memory is freed.</summary>
        protected abstract void Clear();

        /// <summary>Reads the last error string for this object.</summary>
        protected abstract string LastError();

        protected void Check(UIntPtr result)
        {
            if (OlmHelp.IsError(result)) throw new OlmException(LastError());
        }

        protected static string PtrToStr(IntPtr p)
        {
            return p == IntPtr.Zero ? "OLM_ERROR" : (Marshal.PtrToStringAnsi(p) ?? "OLM_ERROR");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (Ptr != IntPtr.Zero) Clear(); } catch { /* best effort */ }
            if (_memory != IntPtr.Zero) { Marshal.FreeHGlobal(_memory); _memory = IntPtr.Zero; }
            Ptr = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }

        ~OlmObject() { Dispose(); }
    }

    /// <summary>An Olm account: long-term Curve25519 identity + Ed25519 signing key + one-time keys.</summary>
    internal sealed class OlmAccount : OlmObject
    {
        private OlmAccount() : base(OlmHelp.Len(OlmNative.olm_account_size())) { }
        protected override IntPtr Create(IntPtr m) { return OlmNative.olm_account(m); }
        protected override void Clear() { OlmNative.olm_clear_account(Ptr); }
        protected override string LastError() { return PtrToStr(OlmNative.olm_account_last_error(Ptr)); }

        internal IntPtr Handle { get { return Ptr; } }

        /// <summary>Creates a brand-new account with fresh keys.</summary>
        internal static OlmAccount Create()
        {
            var a = new OlmAccount();
            var rnd = OlmHelp.Random(OlmHelp.Len(OlmNative.olm_create_account_random_length(a.Ptr)));
            a.Check(OlmNative.olm_create_account(a.Ptr, rnd, OlmHelp.Sz(rnd.Length)));
            return a;
        }

        /// <summary>Restores an account from a previously pickled blob.</summary>
        internal static OlmAccount Unpickle(string pickle, byte[] key)
        {
            var a = new OlmAccount();
            var blob = OlmHelp.Utf8(pickle);
            a.Check(OlmNative.olm_unpickle_account(a.Ptr, key, OlmHelp.Sz(key.Length), blob, OlmHelp.Sz(blob.Length)));
            return a;
        }

        internal string Pickle(byte[] key)
        {
            long len = OlmHelp.Len(OlmNative.olm_pickle_account_length(Ptr));
            var outBuf = new byte[len];
            long w = OlmHelp.Len(CheckLen(OlmNative.olm_pickle_account(Ptr, key, OlmHelp.Sz(key.Length), outBuf, OlmHelp.Sz(len))));
            return OlmHelp.FromUtf8(outBuf, w);
        }

        /// <summary>Returns the JSON {"curve25519":..,"ed25519":..} identity keys.</summary>
        internal string IdentityKeysJson()
        {
            long len = OlmHelp.Len(OlmNative.olm_account_identity_keys_length(Ptr));
            var buf = new byte[len];
            long w = OlmHelp.Len(CheckLen(OlmNative.olm_account_identity_keys(Ptr, buf, OlmHelp.Sz(len))));
            return OlmHelp.FromUtf8(buf, w);
        }

        /// <summary>Signs a message with the account's Ed25519 key (base64 signature).</summary>
        internal string Sign(string message)
        {
            var msg = OlmHelp.Utf8(message);
            long len = OlmHelp.Len(OlmNative.olm_account_signature_length(Ptr));
            var buf = new byte[len];
            long w = OlmHelp.Len(CheckLen(OlmNative.olm_account_sign(Ptr, msg, OlmHelp.Sz(msg.Length), buf, OlmHelp.Sz(len))));
            return OlmHelp.FromUtf8(buf, w);
        }

        /// <summary>Returns the JSON of the unpublished one-time keys ({"curve25519":{id:key,...}}).</summary>
        internal string OneTimeKeysJson()
        {
            long len = OlmHelp.Len(OlmNative.olm_account_one_time_keys_length(Ptr));
            var buf = new byte[len];
            long w = OlmHelp.Len(CheckLen(OlmNative.olm_account_one_time_keys(Ptr, buf, OlmHelp.Sz(len))));
            return OlmHelp.FromUtf8(buf, w);
        }

        internal int MaxOneTimeKeys()
        {
            return (int)OlmHelp.Len(OlmNative.olm_account_max_number_of_one_time_keys(Ptr));
        }

        internal void GenerateOneTimeKeys(int count)
        {
            long rl = OlmHelp.Len(OlmNative.olm_account_generate_one_time_keys_random_length(Ptr, OlmHelp.Sz(count)));
            var rnd = OlmHelp.Random(rl);
            Check(OlmNative.olm_account_generate_one_time_keys(Ptr, OlmHelp.Sz(count), rnd, OlmHelp.Sz(rnd.Length)));
        }

        internal void MarkKeysAsPublished() { Check(OlmNative.olm_account_mark_keys_as_published(Ptr)); }

        private UIntPtr CheckLen(UIntPtr r) { Check(r); return r; }
    }

    /// <summary>A 1:1 Olm session (Double Ratchet) with one other device.</summary>
    internal sealed class OlmSession : OlmObject
    {
        private OlmSession() : base(OlmHelp.Len(OlmNative.olm_session_size())) { }
        protected override IntPtr Create(IntPtr m) { return OlmNative.olm_session(m); }
        protected override void Clear() { OlmNative.olm_clear_session(Ptr); }
        protected override string LastError() { return PtrToStr(OlmNative.olm_session_last_error(Ptr)); }

        internal IntPtr Handle { get { return Ptr; } }

        internal static OlmSession CreateOutbound(OlmAccount account, string theirIdentityKey, string theirOneTimeKey)
        {
            var s = new OlmSession();
            var idk = OlmHelp.Utf8(theirIdentityKey);
            var otk = OlmHelp.Utf8(theirOneTimeKey);
            var rnd = OlmHelp.Random(OlmHelp.Len(OlmNative.olm_create_outbound_session_random_length(s.Ptr)));
            s.Check(OlmNative.olm_create_outbound_session(s.Ptr, account.Handle, idk, OlmHelp.Sz(idk.Length), otk, OlmHelp.Sz(otk.Length), rnd, OlmHelp.Sz(rnd.Length)));
            return s;
        }

        /// <summary>Creates an inbound session from a received prekey message (a copy is consumed).</summary>
        internal static OlmSession CreateInbound(OlmAccount account, string theirIdentityKey, string prekeyMessage)
        {
            var s = new OlmSession();
            var idk = OlmHelp.Utf8(theirIdentityKey);
            var msg = OlmHelp.Utf8(prekeyMessage);
            s.Check(OlmNative.olm_create_inbound_session_from(s.Ptr, account.Handle, idk, OlmHelp.Sz(idk.Length), msg, OlmHelp.Sz(msg.Length)));
            return s;
        }

        internal static OlmSession Unpickle(string pickle, byte[] key)
        {
            var s = new OlmSession();
            var blob = OlmHelp.Utf8(pickle);
            s.Check(OlmNative.olm_unpickle_session(s.Ptr, key, OlmHelp.Sz(key.Length), blob, OlmHelp.Sz(blob.Length)));
            return s;
        }

        internal string Pickle(byte[] key)
        {
            long len = OlmHelp.Len(OlmNative.olm_pickle_session_length(Ptr));
            var buf = new byte[len];
            Check(OlmNative.olm_pickle_session(Ptr, key, OlmHelp.Sz(key.Length), buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }

        internal string SessionId()
        {
            long len = OlmHelp.Len(OlmNative.olm_session_id_length(Ptr));
            var buf = new byte[len];
            Check(OlmNative.olm_session_id(Ptr, buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }

        /// <summary>True if the prekey message matches this inbound session (message is copied).</summary>
        internal bool MatchesInbound(string theirIdentityKey, string prekeyMessage)
        {
            var idk = OlmHelp.Utf8(theirIdentityKey);
            var msg = OlmHelp.Utf8(prekeyMessage);
            var r = OlmNative.olm_matches_inbound_session_from(Ptr, idk, OlmHelp.Sz(idk.Length), msg, OlmHelp.Sz(msg.Length));
            if (OlmHelp.IsError(r)) return false;
            return OlmHelp.Len(r) == 1;
        }

        /// <summary>0 = pre-key message, 1 = normal message.</summary>
        internal int EncryptMessageType() { return (int)OlmHelp.Len(OlmNative.olm_encrypt_message_type(Ptr)); }

        internal string Encrypt(string plaintext)
        {
            var pt = OlmHelp.Utf8(plaintext);
            var rnd = OlmHelp.Random(OlmHelp.Len(OlmNative.olm_encrypt_random_length(Ptr)));
            long len = OlmHelp.Len(OlmNative.olm_encrypt_message_length(Ptr, OlmHelp.Sz(pt.Length)));
            var buf = new byte[len];
            Check(OlmNative.olm_encrypt(Ptr, pt, OlmHelp.Sz(pt.Length), rnd, OlmHelp.Sz(rnd.Length), buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }

        internal string Decrypt(int messageType, string message)
        {
            // olm_decrypt mutates its message buffer, and the max-length probe also consumes a
            // copy, so pass a fresh array to each call.
            var probe = OlmHelp.Utf8(message);
            long max = OlmHelp.Len(CheckLen(OlmNative.olm_decrypt_max_plaintext_length(Ptr, OlmHelp.Sz(messageType), probe, OlmHelp.Sz(probe.Length))));
            var msg = OlmHelp.Utf8(message);
            var outBuf = new byte[max];
            long w = OlmHelp.Len(CheckLen(OlmNative.olm_decrypt(Ptr, OlmHelp.Sz(messageType), msg, OlmHelp.Sz(msg.Length), outBuf, OlmHelp.Sz(max))));
            return OlmHelp.FromUtf8(outBuf, w);
        }

        private UIntPtr CheckLen(UIntPtr r) { Check(r); return r; }
    }

    /// <summary>An inbound Megolm group session — decrypts room messages for one sender chain.</summary>
    internal sealed class OlmInboundGroupSession : OlmObject
    {
        private OlmInboundGroupSession() : base(OlmHelp.Len(OlmNative.olm_inbound_group_session_size())) { }
        protected override IntPtr Create(IntPtr m) { return OlmNative.olm_inbound_group_session(m); }
        protected override void Clear() { OlmNative.olm_clear_inbound_group_session(Ptr); }
        protected override string LastError() { return PtrToStr(OlmNative.olm_inbound_group_session_last_error(Ptr)); }

        /// <summary>Creates a session from a freshly shared session key (m.room_key).</summary>
        internal static OlmInboundGroupSession Create(string sessionKey)
        {
            var s = new OlmInboundGroupSession();
            var key = OlmHelp.Utf8(sessionKey);
            s.Check(OlmNative.olm_init_inbound_group_session(s.Ptr, key, OlmHelp.Sz(key.Length)));
            return s;
        }

        /// <summary>Creates a session from an exported/forwarded key (m.forwarded_room_key / backup).</summary>
        internal static OlmInboundGroupSession Import(string exportedKey)
        {
            var s = new OlmInboundGroupSession();
            var key = OlmHelp.Utf8(exportedKey);
            s.Check(OlmNative.olm_import_inbound_group_session(s.Ptr, key, OlmHelp.Sz(key.Length)));
            return s;
        }

        internal static OlmInboundGroupSession Unpickle(string pickle, byte[] key)
        {
            var s = new OlmInboundGroupSession();
            var blob = OlmHelp.Utf8(pickle);
            s.Check(OlmNative.olm_unpickle_inbound_group_session(s.Ptr, key, OlmHelp.Sz(key.Length), blob, OlmHelp.Sz(blob.Length)));
            return s;
        }

        internal string Pickle(byte[] key)
        {
            long len = OlmHelp.Len(OlmNative.olm_pickle_inbound_group_session_length(Ptr));
            var buf = new byte[len];
            Check(OlmNative.olm_pickle_inbound_group_session(Ptr, key, OlmHelp.Sz(key.Length), buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }

        internal string SessionId()
        {
            long len = OlmHelp.Len(OlmNative.olm_inbound_group_session_id_length(Ptr));
            var buf = new byte[len];
            Check(OlmNative.olm_inbound_group_session_id(Ptr, buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }

        internal uint FirstKnownIndex() { return OlmNative.olm_inbound_group_session_first_known_index(Ptr); }

        /// <summary>Exports the session key at the given index (for key backup / forwarding).</summary>
        internal string Export(uint messageIndex)
        {
            long len = OlmHelp.Len(OlmNative.olm_export_inbound_group_session_length(Ptr));
            var buf = new byte[len];
            Check(OlmNative.olm_export_inbound_group_session(Ptr, buf, OlmHelp.Sz(len), messageIndex));
            return OlmHelp.FromUtf8(buf, len);
        }

        /// <summary>Decrypts a Megolm ciphertext; outputs the ratchet message index used.</summary>
        internal string Decrypt(string message, out uint messageIndex)
        {
            var probe = OlmHelp.Utf8(message);
            long max = OlmHelp.Len(CheckLen(OlmNative.olm_group_decrypt_max_plaintext_length(Ptr, probe, OlmHelp.Sz(probe.Length))));
            var msg = OlmHelp.Utf8(message);
            var outBuf = new byte[max];
            long w = OlmHelp.Len(CheckLen(OlmNative.olm_group_decrypt(Ptr, msg, OlmHelp.Sz(msg.Length), outBuf, OlmHelp.Sz(max), out messageIndex)));
            return OlmHelp.FromUtf8(outBuf, w);
        }

        private UIntPtr CheckLen(UIntPtr r) { Check(r); return r; }
    }

    /// <summary>An outbound Megolm group session — encrypts room messages we send.</summary>
    internal sealed class OlmOutboundGroupSession : OlmObject
    {
        private OlmOutboundGroupSession() : base(OlmHelp.Len(OlmNative.olm_outbound_group_session_size())) { }
        protected override IntPtr Create(IntPtr m) { return OlmNative.olm_outbound_group_session(m); }
        protected override void Clear() { OlmNative.olm_clear_outbound_group_session(Ptr); }
        protected override string LastError() { return PtrToStr(OlmNative.olm_outbound_group_session_last_error(Ptr)); }

        internal static OlmOutboundGroupSession Create()
        {
            var s = new OlmOutboundGroupSession();
            var rnd = OlmHelp.Random(OlmHelp.Len(OlmNative.olm_init_outbound_group_session_random_length(s.Ptr)));
            s.Check(OlmNative.olm_init_outbound_group_session(s.Ptr, rnd, OlmHelp.Sz(rnd.Length)));
            return s;
        }

        internal static OlmOutboundGroupSession Unpickle(string pickle, byte[] key)
        {
            var s = new OlmOutboundGroupSession();
            var blob = OlmHelp.Utf8(pickle);
            s.Check(OlmNative.olm_unpickle_outbound_group_session(s.Ptr, key, OlmHelp.Sz(key.Length), blob, OlmHelp.Sz(blob.Length)));
            return s;
        }

        internal string Pickle(byte[] key)
        {
            long len = OlmHelp.Len(OlmNative.olm_pickle_outbound_group_session_length(Ptr));
            var buf = new byte[len];
            Check(OlmNative.olm_pickle_outbound_group_session(Ptr, key, OlmHelp.Sz(key.Length), buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }

        internal string SessionId()
        {
            long len = OlmHelp.Len(OlmNative.olm_outbound_group_session_id_length(Ptr));
            var buf = new byte[len];
            Check(OlmNative.olm_outbound_group_session_id(Ptr, buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }

        /// <summary>The current session key — shared with recipients via m.room_key.</summary>
        internal string SessionKey()
        {
            long len = OlmHelp.Len(OlmNative.olm_outbound_group_session_key_length(Ptr));
            var buf = new byte[len];
            Check(OlmNative.olm_outbound_group_session_key(Ptr, buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }

        internal uint MessageIndex() { return OlmNative.olm_outbound_group_session_message_index(Ptr); }

        internal string Encrypt(string plaintext)
        {
            var pt = OlmHelp.Utf8(plaintext);
            long len = OlmHelp.Len(OlmNative.olm_group_encrypt_message_length(Ptr, OlmHelp.Sz(pt.Length)));
            var buf = new byte[len];
            Check(OlmNative.olm_group_encrypt(Ptr, pt, OlmHelp.Sz(pt.Length), buf, OlmHelp.Sz(len)));
            return OlmHelp.FromUtf8(buf, len);
        }
    }

    /// <summary>SHA-256 and Ed25519 signature verification.</summary>
    internal sealed class OlmUtility : OlmObject
    {
        internal OlmUtility() : base(OlmHelp.Len(OlmNative.olm_utility_size())) { }
        protected override IntPtr Create(IntPtr m) { return OlmNative.olm_utility(m); }
        protected override void Clear() { OlmNative.olm_clear_utility(Ptr); }
        protected override string LastError() { return PtrToStr(OlmNative.olm_utility_last_error(Ptr)); }

        /// <summary>Verifies an Ed25519 signature; returns false on mismatch (does not throw).</summary>
        internal bool Verify(string ed25519Key, string message, string signature)
        {
            var key = OlmHelp.Utf8(ed25519Key);
            var msg = OlmHelp.Utf8(message);
            var sig = OlmHelp.Utf8(signature);
            var r = OlmNative.olm_ed25519_verify(Ptr, key, OlmHelp.Sz(key.Length), msg, OlmHelp.Sz(msg.Length), sig, OlmHelp.Sz(sig.Length));
            return !OlmHelp.IsError(r);
        }
    }

    /// <summary>Curve25519 public-key encryption — encrypts Megolm sessions to the key backup.</summary>
    internal sealed class OlmPkEncryption : OlmObject
    {
        internal OlmPkEncryption() : base(OlmHelp.Len(OlmNative.olm_pk_encryption_size())) { }
        protected override IntPtr Create(IntPtr m) { return OlmNative.olm_pk_encryption(m); }
        protected override void Clear() { OlmNative.olm_clear_pk_encryption(Ptr); }
        protected override string LastError() { return PtrToStr(OlmNative.olm_pk_encryption_last_error(Ptr)); }

        internal void SetRecipientKey(string publicKey)
        {
            var key = OlmHelp.Utf8(publicKey);
            Check(OlmNative.olm_pk_encryption_set_recipient_key(Ptr, key, OlmHelp.Sz(key.Length)));
        }

        /// <summary>Encrypts plaintext; outputs base64 mac + ephemeral key alongside the ciphertext.</summary>
        internal string Encrypt(string plaintext, out string mac, out string ephemeralKey)
        {
            var pt = OlmHelp.Utf8(plaintext);
            long ctLen = OlmHelp.Len(OlmNative.olm_pk_ciphertext_length(Ptr, OlmHelp.Sz(pt.Length)));
            long macLen = OlmHelp.Len(OlmNative.olm_pk_mac_length(Ptr));
            long ekLen = OlmHelp.Len(OlmNative.olm_pk_key_length());
            var ct = new byte[ctLen];
            var macBuf = new byte[macLen];
            var ekBuf = new byte[ekLen];
            var rnd = OlmHelp.Random(OlmHelp.Len(OlmNative.olm_pk_encrypt_random_length(Ptr)));
            Check(OlmNative.olm_pk_encrypt(Ptr, pt, OlmHelp.Sz(pt.Length), ct, OlmHelp.Sz(ctLen), macBuf, OlmHelp.Sz(macLen), ekBuf, OlmHelp.Sz(ekLen), rnd, OlmHelp.Sz(rnd.Length)));
            mac = OlmHelp.FromUtf8(macBuf, macLen);
            ephemeralKey = OlmHelp.FromUtf8(ekBuf, ekLen);
            return OlmHelp.FromUtf8(ct, ctLen);
        }
    }

    /// <summary>Curve25519 private-key holder — decrypts the key backup and exposes the public key.</summary>
    internal sealed class OlmPkDecryption : OlmObject
    {
        private OlmPkDecryption() : base(OlmHelp.Len(OlmNative.olm_pk_decryption_size())) { }
        protected override IntPtr Create(IntPtr m) { return OlmNative.olm_pk_decryption(m); }
        protected override void Clear() { OlmNative.olm_clear_pk_decryption(Ptr); }
        protected override string LastError() { return PtrToStr(OlmNative.olm_pk_decryption_last_error(Ptr)); }

        /// <summary>The matching public key (base64), produced by FromPrivateKey.</summary>
        internal string PublicKey { get; private set; }

        /// <summary>Builds a decryptor from a 32-byte Curve25519 private key (the recovery key).</summary>
        internal static OlmPkDecryption FromPrivateKey(byte[] privateKey)
        {
            var d = new OlmPkDecryption();
            long pubLen = OlmHelp.Len(OlmNative.olm_pk_key_length());
            var pub = new byte[pubLen];
            d.Check(OlmNative.olm_pk_key_from_private(d.Ptr, pub, OlmHelp.Sz(pubLen), privateKey, OlmHelp.Sz(privateKey.Length)));
            d.PublicKey = OlmHelp.FromUtf8(pub, pubLen);
            return d;
        }

        internal string Decrypt(string ephemeralKey, string mac, string ciphertext)
        {
            var ek = OlmHelp.Utf8(ephemeralKey);
            var macB = OlmHelp.Utf8(mac);
            var ct = OlmHelp.Utf8(ciphertext);
            long max = OlmHelp.Len(CheckLen(OlmNative.olm_pk_max_plaintext_length(Ptr, OlmHelp.Sz(ct.Length))));
            var outBuf = new byte[max];
            long w = OlmHelp.Len(CheckLen(OlmNative.olm_pk_decrypt(Ptr, ek, OlmHelp.Sz(ek.Length), macB, OlmHelp.Sz(macB.Length), ct, OlmHelp.Sz(ct.Length), outBuf, OlmHelp.Sz(max))));
            return OlmHelp.FromUtf8(outBuf, w);
        }

        private UIntPtr CheckLen(UIntPtr r) { Check(r); return r; }
    }
}
#endif
