using System;
using Windows.Security.Credentials;
using Windows.Storage;

namespace UniMatrix.Services
{
    /// <summary>
    /// Stores non-secret preferences in LocalSettings and the Matrix access token
    /// in the Windows credential vault. The account password is never persisted.
    /// </summary>
    internal class PreferencesService
    {
        private const string VaultResource = "UniMatrix";

        private const string HomeserverKey = "matrix_homeserver";
        private const string UserIdKey = "matrix_user_id";
        private const string DeviceIdKey = "matrix_device_id";
        private const string MessageLimitKey = "matrix_message_limit";
        private const string UseSystemAccentKey = "use_system_accent";

        private readonly ApplicationDataContainer _local = ApplicationData.Current.LocalSettings;

        public string Homeserver
        {
            get
            {
                if (_local.Values.ContainsKey(HomeserverKey))
                    return _local.Values[HomeserverKey] as string;
                return "matrix.org";
            }
            set { _local.Values[HomeserverKey] = value; }
        }

        public string UserId
        {
            get { return _local.Values.ContainsKey(UserIdKey) ? _local.Values[UserIdKey] as string : null; }
            set { _local.Values[UserIdKey] = value; }
        }

        public string DeviceId
        {
            get { return _local.Values.ContainsKey(DeviceIdKey) ? _local.Values[DeviceIdKey] as string : null; }
            set { _local.Values[DeviceIdKey] = value; }
        }

        public int MessageLimit
        {
            get
            {
                if (_local.Values.ContainsKey(MessageLimitKey))
                    return (int)_local.Values[MessageLimitKey];
                return 50;
            }
            set { _local.Values[MessageLimitKey] = value; }
        }

        /// <summary>True (default) to follow the system accent color; false for the signature green.</summary>
        public bool UseSystemAccent
        {
            get
            {
                if (_local.Values.ContainsKey(UseSystemAccentKey))
                    return (bool)_local.Values[UseSystemAccentKey];
                return true; // System accent by default.
            }
            set { _local.Values[UseSystemAccentKey] = value; }
        }

        /// <summary>Returns the stored access token, or null if the user is not logged in.</summary>
        public string GetAccessToken()
        {
            if (string.IsNullOrEmpty(UserId)) return null;
            try
            {
                var vault = new PasswordVault();
                var cred = vault.Retrieve(VaultResource, UserId);
                cred.RetrievePassword();
                return cred.Password;
            }
            catch
            {
                // No matching credential stored.
                return null;
            }
        }

        public void SaveAccessToken(string userId, string token)
        {
            var vault = new PasswordVault();
            // Remove any prior token for this user to avoid duplicate vault entries.
            try
            {
                var existing = vault.Retrieve(VaultResource, userId);
                vault.Remove(existing);
            }
            catch { }
            vault.Add(new PasswordCredential(VaultResource, userId, token));
        }

        public void ClearAccessToken()
        {
            try
            {
                var vault = new PasswordVault();
                foreach (var cred in vault.FindAllByResource(VaultResource))
                {
                    vault.Remove(cred);
                }
            }
            catch { }
        }

        public bool IsLoggedIn
        {
            get { return !string.IsNullOrEmpty(GetAccessToken()); }
        }
    }
}
