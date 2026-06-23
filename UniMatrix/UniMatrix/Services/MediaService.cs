using System;
using System.Threading.Tasks;
using Windows.Storage;
using UniMatrix.Data;

namespace UniMatrix.Services
{
    /// <summary>
    /// Downloads Matrix media (mxc://) into the app's local media folder and records
    /// the cached ms-appdata URI in the database so repeated views avoid re-downloading.
    /// The returned URIs bind directly to an Image.Source.
    /// </summary>
    internal class MediaService
    {
        private readonly MatrixClient _client;
        private readonly MatrixDatabase _db;
        private StorageFolder _mediaFolder;

        public MediaService(MatrixClient client, MatrixDatabase db)
        {
            _client = client;
            _db = db;
        }

        private async Task<StorageFolder> GetFolderAsync()
        {
            if (_mediaFolder == null)
            {
                _mediaFolder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync("media", CreationCollisionOption.OpenIfExists);
            }
            return _mediaFolder;
        }

        /// <summary>
        /// Returns an ms-appdata:// URI for the given mxc thumbnail, downloading and
        /// caching it on first use. Returns null on failure or for non-mxc input.
        /// </summary>
        public async Task<string> GetThumbnailUriAsync(string mxc, int size)
        {
            if (string.IsNullOrEmpty(mxc) || !mxc.StartsWith("mxc://", StringComparison.OrdinalIgnoreCase))
                return null;

            string cached = _db.GetCachedMedia(mxc);
            if (!string.IsNullOrEmpty(cached))
            {
                try
                {
                    await StorageFile.GetFileFromApplicationUriAsync(new Uri(cached));
                    return cached; // Still present on disk.
                }
                catch { /* Removed; fall through to re-download. */ }
            }

            try
            {
                // FetchMediaAsync tries authenticated (v1) then legacy (r0) endpoints,
                // and thumbnail then full download, so both old and new media resolve.
                var buffer = await _client.FetchMediaAsync(mxc, size);
                if (buffer == null) return null;

                var folder = await GetFolderAsync();
                string fileName = SafeFileName(mxc) + ".img";
                var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBufferAsync(file, buffer);

                string appUri = "ms-appdata:///local/media/" + fileName;
                _db.SetCachedMedia(mxc, appUri);
                return appUri;
            }
            catch (Exception ex)
            {
                App.Log("Media EXC for " + mxc + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Copies an already-picked local file into the media cache under the freshly-assigned
        /// mxc's filename, and registers it in the DB. After uploading a picture we call this so
        /// that when the same m.image event comes back through /sync the thumbnail is an instant
        /// cache hit instead of being re-downloaded from the server.
        /// </summary>
        public async Task CacheLocalFileForMxcAsync(StorageFile source, string mxc)
        {
            if (source == null || string.IsNullOrEmpty(mxc) ||
                !mxc.StartsWith("mxc://", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var folder = await GetFolderAsync();
                string fileName = SafeFileName(mxc) + ".img";
                await source.CopyAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
                _db.SetCachedMedia(mxc, "ms-appdata:///local/media/" + fileName);
            }
            catch (Exception ex)
            {
                App.Log("CacheLocalFileForMxc EXC: " + ex.Message);
            }
        }

        /// <summary>
        /// Copies a picked file into the media folder under a unique temp name and returns its
        /// ms-appdata URI. Used to show an instant local-echo preview bubble before the upload
        /// completes (we don't yet know the mxc URI at echo time).
        /// </summary>
        public async Task<string> CacheLocalPreviewAsync(StorageFile source)
        {
            if (source == null) return null;
            try
            {
                var folder = await GetFolderAsync();
                string fileName = "echo_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".img";
                await source.CopyAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
                return "ms-appdata:///local/media/" + fileName;
            }
            catch (Exception ex)
            {
                App.Log("CacheLocalPreview EXC: " + ex.Message);
                return null;
            }
        }

        private static string SafeFileName(string mxc)
        {
            // mxc://server/mediaId -> server_mediaId with non-alphanumerics stripped.
            string s = mxc.Substring(6);
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }
    }
}
