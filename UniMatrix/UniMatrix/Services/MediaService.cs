using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.Cryptography;
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
                var buffer = await FetchPossiblyEncryptedAsync(mxc, size);
                if (buffer == null) return null;

                var folder = await GetFolderAsync();
                // Use a unique filename rather than a deterministic one: after a cache wipe the
                // old file may still be locked by a live BitmapImage/ImageBrush, and overwriting
                // it (ReplaceExisting) would throw "file in use" and silently lose the avatar.
                string fileName = SafeFileName(mxc) + "_" + NextSeq() + ".img";
                var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteBufferAsync(file, buffer);

                string appUri = "ms-appdata:///local/media/" + file.Name;
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
                // Unique name (see GetThumbnailUriAsync) so re-caching never collides with a
                // locked file left over from a previous session.
                string fileName = SafeFileName(mxc) + "_" + NextSeq() + ".img";
                var copy = await source.CopyAsync(folder, fileName, NameCollisionOption.GenerateUniqueName);
                _db.SetCachedMedia(mxc, "ms-appdata:///local/media/" + copy.Name);
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

        /// <summary>
        /// Returns an ms-appdata:// URI for the FULL-resolution original of an mxc image,
        /// downloading and caching it on first use (separate cache key from the thumbnail).
        /// Used by the full-screen image viewer. Returns null on failure or non-mxc input.
        /// </summary>
        public async Task<string> GetFullImageUriAsync(string mxc)
        {
            if (string.IsNullOrEmpty(mxc) || !mxc.StartsWith("mxc://", StringComparison.OrdinalIgnoreCase))
                return null;

            string cacheKey = "full:" + mxc;
            string cached = _db.GetCachedMedia(cacheKey);
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
                var buffer = await _client.FetchOriginalAsync(mxc);
                if (buffer == null) return null;
                buffer = await MaybeDecryptAsync(mxc, buffer);
                if (buffer == null) return null;

                var folder = await GetFolderAsync();
                // Unique name (see GetThumbnailUriAsync) to avoid "file in use" on re-download.
                string fileName = "full_" + SafeFileName(mxc) + "_" + NextSeq() + ".img";
                var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteBufferAsync(file, buffer);

                string appUri = "ms-appdata:///local/media/" + file.Name;
                _db.SetCachedMedia(cacheKey, appUri);
                return appUri;
            }
            catch (Exception ex)
            {
                App.Log("Full image EXC for " + mxc + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Best-effort deletion of every cached media file on disk. Called during a cache wipe so
        /// orphaned files don't accumulate. Files still locked by a live image (e.g. an avatar
        /// currently on screen) are skipped silently; their now-unique names mean a re-download
        /// writes a fresh file instead of fighting the lock.
        /// </summary>
        public async Task ClearMediaCacheAsync()
        {
            try
            {
                var folder = await GetFolderAsync();
                var files = await folder.GetFilesAsync();
                foreach (var file in files)
                {
                    try { await file.DeleteAsync(StorageDeleteOption.PermanentDelete); }
                    catch { /* In use (bound to a live Image) -> leave it; harmless. */ }
                }
            }
            catch (Exception ex)
            {
                App.Log("ClearMediaCache EXC: " + ex.Message);
            }
        }

        // Monotonic counter so concurrently-cached files within a session never collide on name.
        private static int _seq;
        private static int NextSeq()
        {
            return System.Threading.Interlocked.Increment(ref _seq);
        }

        /// <summary>
        /// Fetches media that may be an encrypted attachment. The homeserver can't thumbnail an
        /// encrypted blob, so when a decryption key is on file we fetch the full ciphertext and
        /// decrypt it; otherwise we use the normal (server-thumbnailed) path.
        /// </summary>
        private async Task<Windows.Storage.Streams.IBuffer> FetchPossiblyEncryptedAsync(string mxc, int size)
        {
            string fileJson = _db.GetAttachmentKey(mxc);
            if (string.IsNullOrEmpty(fileJson))
                return await _client.FetchMediaAsync(mxc, size);

            var cipher = await _client.FetchOriginalAsync(mxc);
            return await MaybeDecryptAsync(mxc, cipher);
        }

        /// <summary>Decrypts a downloaded buffer when an attachment key exists for its mxc; returns
        /// the buffer unchanged (plaintext) when there is no key.</summary>
        private async Task<Windows.Storage.Streams.IBuffer> MaybeDecryptAsync(string mxc, Windows.Storage.Streams.IBuffer buffer)
        {
            if (buffer == null) return null;
            string fileJson = _db.GetAttachmentKey(mxc);
            if (string.IsNullOrEmpty(fileJson)) return buffer;

            JsonObject file;
            if (!JsonObject.TryParse(fileJson, out file)) return null;

            byte[] cipherBytes;
            CryptographicBuffer.CopyToByteArray(buffer, out cipherBytes);
            byte[] plain = AttachmentCrypto.Decrypt(cipherBytes, file);
            if (plain == null) return null;

            await Task.FromResult(0);
            return CryptographicBuffer.CreateFromByteArray(plain);
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
