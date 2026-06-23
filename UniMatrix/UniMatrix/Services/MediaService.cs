using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Web.Http;
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
        private readonly HttpClient _http = new HttpClient();
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

            string url = _client.ResolveThumbnailUrl(mxc, size);
            if (url == null) return null;

            try
            {
                var response = await _http.GetAsync(new Uri(url));

                // matrix.org sometimes can't generate a thumbnail for certain media and
                // returns 404 even though the original is downloadable. Fall back to the
                // full-resolution download URL in that case.
                if (!response.IsSuccessStatusCode)
                {
                    App.Log("Media thumb HTTP " + (int)response.StatusCode + " for " + mxc + "; trying download...");
                    response.Dispose();
                    string downloadUrl = _client.ResolveDownloadUrl(mxc);
                    if (downloadUrl == null) return null;
                    response = await _http.GetAsync(new Uri(downloadUrl));
                    if (!response.IsSuccessStatusCode)
                    {
                        App.Log("Media download HTTP " + (int)response.StatusCode + " for " + mxc);
                        response.Dispose();
                        return null;
                    }
                }

                var buffer = await response.Content.ReadAsBufferAsync();
                response.Dispose();
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
