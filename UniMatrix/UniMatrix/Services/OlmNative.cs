#if CRYPTO
using System;
using System.Runtime.InteropServices;

namespace UniMatrix.Services
{
    /// <summary>
    /// Raw P/Invoke surface for libolm (olm.dll), the Matrix end-to-end-encryption C library.
    /// These map 1:1 to the C functions in olm.h / inbound_group_session.h /
    /// outbound_group_session.h / pk.h. Higher-level, memory-safe wrappers live in
    /// OlmWrappers.cs — application code should use those, not these externs.
    ///
    /// Conventions used by libolm and reflected here:
    ///   * Opaque objects (OlmAccount*, OlmSession*, ...) are returned/consumed as IntPtr.
    ///     libolm never allocates them itself; the caller hands it a block of memory of
    ///     olm_*_size() bytes. We allocate that block with Marshal.AllocHGlobal so the pointer
    ///     stays fixed for the object's lifetime (a managed byte[] would be unsafe — the GC
    ///     could move it between calls, especially under .NET Native on ARM).
    ///   * Transient input/output buffers are passed as byte[]; the marshaller pins them for
    ///     the duration of a single call, which is sufficient.
    ///   * Almost every function returns a size_t. A return value equal to olm_error()
    ///     (SIZE_MAX) signals failure; the matching olm_*_last_error() then returns a string.
    ///     size_t marshals as UIntPtr here so it is correct on both 32-bit (Lumia) and 64-bit.
    /// </summary>
    internal static class OlmNative
    {
        private const string Dll = "olm.dll";
        private const CallingConvention Cdecl = CallingConvention.Cdecl;

        // ---- Diagnostics -----------------------------------------------------------------
        // LoadPackagedLibrary is the only loader allowed inside an appcontainer. We use it to
        // probe whether olm.dll (and its dependency chain, e.g. the VC++ appcontainer runtime)
        // can actually be loaded, so a load failure produces a real Win32 error code instead of
        // the opaque .NET Native "Unresolved P/Invoke" message.
        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadPackagedLibrary(string libFileName, uint reserved);

        // Sentinel returned by libolm calls on error (== (size_t)-1).
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_error();

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_get_library_version(out byte major, out byte minor, out byte patch);

        // ---- Account ---------------------------------------------------------------------
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_account(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_account_last_error(IntPtr account);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_account(IntPtr account);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_create_account_random_length(IntPtr account);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_create_account(IntPtr account, byte[] random, UIntPtr random_length);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_identity_keys_length(IntPtr account);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_identity_keys(IntPtr account, byte[] identity_keys, UIntPtr identity_key_length);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_signature_length(IntPtr account);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_sign(IntPtr account, byte[] message, UIntPtr message_length, byte[] signature, UIntPtr signature_length);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_one_time_keys_length(IntPtr account);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_one_time_keys(IntPtr account, byte[] one_time_keys, UIntPtr one_time_keys_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_mark_keys_as_published(IntPtr account);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_max_number_of_one_time_keys(IntPtr account);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_generate_one_time_keys_random_length(IntPtr account, UIntPtr number_of_keys);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_account_generate_one_time_keys(IntPtr account, UIntPtr number_of_keys, byte[] random, UIntPtr random_length);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pickle_account_length(IntPtr account);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pickle_account(IntPtr account, byte[] key, UIntPtr key_length, byte[] pickled, UIntPtr pickled_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_unpickle_account(IntPtr account, byte[] key, UIntPtr key_length, byte[] pickled, UIntPtr pickled_length);

        // ---- 1:1 (Olm) session -----------------------------------------------------------
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_session_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_session(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_session_last_error(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_session(IntPtr session);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_create_outbound_session_random_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_create_outbound_session(IntPtr session, IntPtr account, byte[] their_identity_key, UIntPtr their_identity_key_length, byte[] their_one_time_key, UIntPtr their_one_time_key_length, byte[] random, UIntPtr random_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_create_inbound_session(IntPtr session, IntPtr account, byte[] one_time_key_message, UIntPtr message_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_create_inbound_session_from(IntPtr session, IntPtr account, byte[] their_identity_key, UIntPtr their_identity_key_length, byte[] one_time_key_message, UIntPtr message_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_matches_inbound_session(IntPtr session, byte[] one_time_key_message, UIntPtr message_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_matches_inbound_session_from(IntPtr session, byte[] their_identity_key, UIntPtr their_identity_key_length, byte[] one_time_key_message, UIntPtr message_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_remove_one_time_keys(IntPtr account, IntPtr session);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_session_id_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_session_id(IntPtr session, byte[] id, UIntPtr id_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int olm_session_has_received_message(IntPtr session);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_encrypt_message_type(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_encrypt_random_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_encrypt_message_length(IntPtr session, UIntPtr plaintext_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_encrypt(IntPtr session, byte[] plaintext, UIntPtr plaintext_length, byte[] random, UIntPtr random_length, byte[] message, UIntPtr message_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_decrypt_max_plaintext_length(IntPtr session, UIntPtr message_type, byte[] message, UIntPtr message_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_decrypt(IntPtr session, UIntPtr message_type, byte[] message, UIntPtr message_length, byte[] plaintext, UIntPtr max_plaintext_length);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pickle_session_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pickle_session(IntPtr session, byte[] key, UIntPtr key_length, byte[] pickled, UIntPtr pickled_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_unpickle_session(IntPtr session, byte[] key, UIntPtr key_length, byte[] pickled, UIntPtr pickled_length);

        // ---- Inbound group (Megolm) session ----------------------------------------------
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_inbound_group_session_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_inbound_group_session(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_inbound_group_session_last_error(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_inbound_group_session(IntPtr session);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_init_inbound_group_session(IntPtr session, byte[] session_key, UIntPtr session_key_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_import_inbound_group_session(IntPtr session, byte[] session_key, UIntPtr session_key_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_group_decrypt_max_plaintext_length(IntPtr session, byte[] message, UIntPtr message_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_group_decrypt(IntPtr session, byte[] message, UIntPtr message_length, byte[] plaintext, UIntPtr max_plaintext_length, out uint message_index);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_inbound_group_session_id_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_inbound_group_session_id(IntPtr session, byte[] id, UIntPtr id_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern uint olm_inbound_group_session_first_known_index(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int olm_inbound_group_session_is_verified(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_export_inbound_group_session_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_export_inbound_group_session(IntPtr session, byte[] key, UIntPtr key_length, uint message_index);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pickle_inbound_group_session_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pickle_inbound_group_session(IntPtr session, byte[] key, UIntPtr key_length, byte[] pickled, UIntPtr pickled_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_unpickle_inbound_group_session(IntPtr session, byte[] key, UIntPtr key_length, byte[] pickled, UIntPtr pickled_length);

        // ---- Outbound group (Megolm) session ---------------------------------------------
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_outbound_group_session_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_outbound_group_session(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_outbound_group_session_last_error(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_outbound_group_session(IntPtr session);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_init_outbound_group_session_random_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_init_outbound_group_session(IntPtr session, byte[] random, UIntPtr random_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_group_encrypt_message_length(IntPtr session, UIntPtr plaintext_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_group_encrypt(IntPtr session, byte[] plaintext, UIntPtr plaintext_length, byte[] message, UIntPtr message_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_outbound_group_session_id_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_outbound_group_session_id(IntPtr session, byte[] id, UIntPtr id_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern uint olm_outbound_group_session_message_index(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_outbound_group_session_key_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_outbound_group_session_key(IntPtr session, byte[] key, UIntPtr key_length);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pickle_outbound_group_session_length(IntPtr session);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pickle_outbound_group_session(IntPtr session, byte[] key, UIntPtr key_length, byte[] pickled, UIntPtr pickled_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_unpickle_outbound_group_session(IntPtr session, byte[] key, UIntPtr key_length, byte[] pickled, UIntPtr pickled_length);

        // ---- Utility (sha256 + ed25519 verify) -------------------------------------------
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_utility_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_utility(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_utility_last_error(IntPtr utility);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_utility(IntPtr utility);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sha256_length(IntPtr utility);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sha256(IntPtr utility, byte[] input, UIntPtr input_length, byte[] output, UIntPtr output_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_ed25519_verify(IntPtr utility, byte[] key, UIntPtr key_length, byte[] message, UIntPtr message_length, byte[] signature, UIntPtr signature_length);

        // ---- PK encryption / decryption (Curve25519, used by key backup) ------------------
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_encryption_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_pk_encryption(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_pk_encryption_last_error(IntPtr encryption);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_pk_encryption(IntPtr encryption);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_encryption_set_recipient_key(IntPtr encryption, byte[] public_key, UIntPtr public_key_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_ciphertext_length(IntPtr encryption, UIntPtr plaintext_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_mac_length(IntPtr encryption);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_key_length();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_encrypt_random_length(IntPtr encryption);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_encrypt(IntPtr encryption, byte[] plaintext, UIntPtr plaintext_length, byte[] ciphertext, UIntPtr ciphertext_length, byte[] mac, UIntPtr mac_length, byte[] ephemeral_key, UIntPtr ephemeral_key_length, byte[] random, UIntPtr random_length);

        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_decryption_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_pk_decryption(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_pk_decryption_last_error(IntPtr decryption);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_pk_decryption(IntPtr decryption);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_private_key_length();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_key_from_private(IntPtr decryption, byte[] pubkey, UIntPtr pubkey_length, byte[] privkey, UIntPtr privkey_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_max_plaintext_length(IntPtr decryption, UIntPtr ciphertext_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_decrypt(IntPtr decryption, byte[] ephemeral_key, UIntPtr ephemeral_key_length, byte[] mac, UIntPtr mac_length, byte[] ciphertext, UIntPtr ciphertext_length, byte[] plaintext, UIntPtr max_plaintext_length);

        // ---- PK signing (Ed25519, used by cross-signing) ----------------------------------
        // An OlmPkSigning wraps an Ed25519 keypair derived from a 32-byte seed. The cross-signing
        // master/self-signing/user-signing private keys are exactly such seeds (stored in SSSS).
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_signing_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_pk_signing(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_pk_signing_last_error(IntPtr sign);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_pk_signing(IntPtr sign);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_signing_seed_length();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_signing_public_key_length();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_signing_key_from_seed(IntPtr sign, byte[] pubkey, UIntPtr pubkey_length, byte[] seed, UIntPtr seed_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_signature_length();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_pk_sign(IntPtr sign, byte[] message, UIntPtr message_length, byte[] signature, UIntPtr signature_length);

        // ---- SAS (Short Authentication String, used by interactive device verification) ----
        // An OlmSAS holds an ephemeral ECDH keypair. Two parties exchange public keys, then derive
        // identical short codes (emoji/decimal) and MACs to confirm no MITM.
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sas_size();
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_sas(IntPtr memory);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr olm_sas_last_error(IntPtr sas);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_clear_sas(IntPtr sas);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_create_sas_random_length(IntPtr sas);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_create_sas(IntPtr sas, byte[] random, UIntPtr random_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sas_pubkey_length(IntPtr sas);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sas_get_pubkey(IntPtr sas, byte[] pubkey, UIntPtr pubkey_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sas_set_their_key(IntPtr sas, byte[] their_key, UIntPtr their_key_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int olm_sas_is_their_key_set(IntPtr sas);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sas_generate_bytes(IntPtr sas, byte[] info, UIntPtr info_length, byte[] output, UIntPtr output_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sas_mac_length(IntPtr sas);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sas_calculate_mac(IntPtr sas, byte[] input, UIntPtr input_length, byte[] info, UIntPtr info_length, byte[] mac, UIntPtr mac_length);
        [DllImport(Dll, CallingConvention = Cdecl)] internal static extern UIntPtr olm_sas_calculate_mac_fixed_base64(IntPtr sas, byte[] input, UIntPtr input_length, byte[] info, UIntPtr info_length, byte[] mac, UIntPtr mac_length);
    }
}
#endif
