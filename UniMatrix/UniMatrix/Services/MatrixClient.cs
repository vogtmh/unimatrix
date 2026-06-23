using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Web.Http;

namespace UniMatrix.Services
{
    /// <summary>Result of a successful login.</summary>
    internal class LoginResult
    {
        public string UserId { get; set; }
        public string AccessToken { get; set; }
        public string DeviceId { get; set; }
    }

    /// <summary>
    /// Thin wrapper over the Matrix Client-Server API (r0). Handles login,
    /// the long-polling /sync loop, sending messages, room creation, history
    /// pagination and mxc:// media URL resolution.
    /// </summary>
    internal class MatrixClient
    {
        private readonly HttpClient _http = new HttpClient();
        private string _baseUrl;
        private string _accessToken;

        public MatrixClient()
        {
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("UniMatrix/1.0");
        }

        public string AccessToken
        {
            get { return _accessToken; }
            set { _accessToken = value; }
        }

        /// <summary>The homeserver base URL, e.g. https://matrix.org (no trailing slash).</summary>
        public string BaseUrl => _baseUrl;

        /// <summary>Normalizes a user-entered homeserver into a https base URL.</summary>
        public void SetHomeserver(string homeserver)
        {
            if (string.IsNullOrWhiteSpace(homeserver))
            {
                _baseUrl = "https://matrix.org";
                return;
            }
            homeserver = homeserver.Trim().TrimEnd('/');
            if (!homeserver.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !homeserver.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                homeserver = "https://" + homeserver;
            }
            _baseUrl = homeserver;
        }

        /// <summary>The server name part of the homeserver, e.g. matrix.org. Used for mxc resolution fallback.</summary>
        public string ServerName
        {
            get
            {
                try
                {
                    var uri = new Uri(_baseUrl);
                    return uri.Host;
                }
                catch { return _baseUrl; }
            }
        }

        // ---- Authentication ----

        public async Task<LoginResult> LoginAsync(string user, string password)
        {
            var identifier = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue("m.id.user"),
                ["user"] = JsonValue.CreateStringValue(user)
            };
            var body = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue("m.login.password"),
                ["identifier"] = identifier,
                ["password"] = JsonValue.CreateStringValue(password),
                ["initial_device_display_name"] = JsonValue.CreateStringValue("UniMatrix (Windows Mobile)")
            };

            var resp = await PostAsync("/_matrix/client/r0/login", body, requireAuth: false);
            var result = new LoginResult
            {
                UserId = GetString(resp, "user_id"),
                AccessToken = GetString(resp, "access_token"),
                DeviceId = GetString(resp, "device_id")
            };
            _accessToken = result.AccessToken;
            return result;
        }

        public async Task LogoutAsync()
        {
            try { await PostAsync("/_matrix/client/r0/logout", new JsonObject(), requireAuth: true); }
            catch { /* Best effort; token is cleared locally regardless. */ }
            _accessToken = null;
        }

        // ---- Sync ----

        /// <summary>
        /// A compact sync filter: drops presence/typing noise, lazy-loads room
        /// members (so the initial sync isn't bloated by full member lists) and
        /// caps the timeline. This keeps the initial sync small enough to parse
        /// quickly on low-memory devices like the Lumia 930.
        /// </summary>
        private const string SyncFilter =
            "{\"presence\":{\"types\":[]}," +
            "\"room\":{\"timeline\":{\"limit\":20}," +
            "\"state\":{\"lazy_load_members\":true}," +
            "\"ephemeral\":{\"types\":[]}}}";

        /// <summary>
        /// Runs a single /sync request. When <paramref name="since"/> is null this is an
        /// initial sync; otherwise it long-polls for up to <paramref name="timeoutMs"/>.
        /// </summary>
        public async Task<JsonObject> SyncAsync(string since, int timeoutMs, CancellationToken ct)
        {
            string path = "/_matrix/client/r0/sync?access_token=" + Uri.EscapeDataString(_accessToken);
            path += "&filter=" + Uri.EscapeDataString(SyncFilter);
            bool initial = string.IsNullOrEmpty(since);
            if (initial)
            {
                path += "&timeout=0";
            }
            else
            {
                path += "&since=" + Uri.EscapeDataString(since) + "&timeout=" + timeoutMs;
            }

            // Hard client-side cap so a stalled request surfaces as an error instead
            // of leaving the sync LED stuck orange forever. The long-poll timeout is
            // server-side; we allow generous extra time for transfer + parse.
            int clientTimeoutMs = initial ? 120000 : timeoutMs + 20000;
            using (var timeoutCts = new CancellationTokenSource(clientTimeoutMs))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
            {
                try
                {
                    return await GetAsync(path, linked.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new MatrixException(
                        "Sync timed out after " + (clientTimeoutMs / 1000) + "s", 0);
                }
            }
        }

        // ---- Messages ----

        public async Task<string> SendTextMessageAsync(string roomId, string body)
        {
            string txnId = "m" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string path = "/_matrix/client/r0/rooms/" + Uri.EscapeDataString(roomId) +
                          "/send/m.room.message/" + Uri.EscapeDataString(txnId);

            var content = new JsonObject
            {
                ["msgtype"] = JsonValue.CreateStringValue("m.text"),
                ["body"] = JsonValue.CreateStringValue(body)
            };

            var resp = await PutAsync(path, content);
            return GetString(resp, "event_id");
        }

        public async Task<string> CreateRoomAsync(string name, bool isPublic)
        {
            var body = new JsonObject
            {
                ["name"] = JsonValue.CreateStringValue(name),
                ["preset"] = JsonValue.CreateStringValue(isPublic ? "public_chat" : "private_chat"),
                ["visibility"] = JsonValue.CreateStringValue(isPublic ? "public" : "private")
            };
            var resp = await PostAsync("/_matrix/client/r0/createRoom", body, requireAuth: true);
            return GetString(resp, "room_id");
        }

        /// <summary>Fetches the most recent messages for a room (used to backfill history).</summary>
        public async Task<JsonObject> GetRoomMessagesAsync(string roomId, int limit, CancellationToken ct)
        {
            string path = "/_matrix/client/r0/rooms/" + Uri.EscapeDataString(roomId) +
                          "/messages?dir=b&limit=" + limit +
                          "&access_token=" + Uri.EscapeDataString(_accessToken);
            return await GetAsync(path, ct);
        }

        // ---- Media ----

        /// <summary>
        /// Downloads media bytes for an mxc:// URL, returning null on failure.
        /// matrix.org now serves newer media only via the authenticated media API
        /// (/_matrix/client/v1/media, requires a Bearer token), while older media is
        /// still on the legacy unauthenticated /_matrix/media/r0 endpoints. We try the
        /// authenticated thumbnail/download first, then fall back to the legacy ones,
        /// so both old and new media resolve.
        /// </summary>
        public async Task<Windows.Storage.Streams.IBuffer> FetchMediaAsync(string mxc, int size)
        {
            string serverAndId = ParseMxc(mxc);
            if (serverAndId == null) return null;

            // Authenticate using ONLY the access_token query parameter (matching the
            // original JS UniMatrix client). matrix.org rejects requests that mix an
            // Authorization header with the query param (M_MISSING_TOKEN), so we never
            // send a header here. Authenticated v1 endpoints first (required for newer
            // media), then legacy r0 as a fallback for older media.
            string tok = Uri.EscapeDataString(_accessToken);
            var candidates = new[]
            {
                _baseUrl + "/_matrix/client/v1/media/thumbnail/" + serverAndId +
                    "?width=" + size + "&height=" + size + "&method=scale&access_token=" + tok,
                _baseUrl + "/_matrix/client/v1/media/download/" + serverAndId +
                    "?access_token=" + tok,
                _baseUrl + "/_matrix/media/r0/thumbnail/" + serverAndId +
                    "?width=" + size + "&height=" + size + "&method=scale&access_token=" + tok,
                _baseUrl + "/_matrix/media/r0/download/" + serverAndId +
                    "?access_token=" + tok,
            };

            foreach (var url in candidates)
            {
                try
                {
                    using (var resp = await _http.GetAsync(new Uri(url)))
                    {
                        if (resp.IsSuccessStatusCode)
                            return await resp.Content.ReadAsBufferAsync();

                        string body = "";
                        try { body = await resp.Content.ReadAsStringAsync(); }
                        catch { }
                        if (body != null && body.Length > 160) body = body.Substring(0, 160);
                        App.Log("Media " + (int)resp.StatusCode + " " + mxc + " :: " + body);
                    }
                }
                catch (Exception ex)
                {
                    App.Log("Media EXC " + mxc + ": " + ex.Message);
                }
            }
            return null;
        }

        private static string ParseMxc(string mxc)
        {
            if (string.IsNullOrEmpty(mxc) || !mxc.StartsWith("mxc://", StringComparison.OrdinalIgnoreCase))
                return null;
            // mxc://server/mediaId  ->  server/mediaId
            return mxc.Substring(6);
        }

        // ---- HTTP helpers ----

        private async Task<JsonObject> GetAsync(string path, CancellationToken ct)
        {
            var uri = new Uri(_baseUrl + path);
            bool isSync = path.StartsWith("/_matrix/client/r0/sync", StringComparison.OrdinalIgnoreCase);
            if (isSync) App.Log("HTTP GET sync: sending request...");
            // ConfigureAwait(false) keeps the (potentially large) response read and
            // JSON parse off the UI thread so the app doesn't freeze on big syncs.
            using (var resp = await _http.GetAsync(uri).AsTask(ct).ConfigureAwait(false))
            {
                if (isSync) App.Log("HTTP GET sync: status " + (int)resp.StatusCode + ", reading body...");
                string text = await resp.Content.ReadAsStringAsync().AsTask(ct).ConfigureAwait(false);
                if (isSync) App.Log("HTTP GET sync: body read, " + text.Length + " chars. Parsing...");
                EnsureSuccess(resp, text);
                // Parse on a background thread — Windows.Data.Json can take seconds
                // on a large initial-sync payload on low-end hardware.
                var obj = await Task.Run(() => Parse(text), ct).ConfigureAwait(false);
                if (isSync) App.Log("HTTP GET sync: parse complete.");
                return obj;
            }
        }

        private async Task<JsonObject> PostAsync(string path, JsonObject body, bool requireAuth)
        {
            string fullPath = path;
            if (requireAuth)
            {
                fullPath += (path.Contains("?") ? "&" : "?") +
                            "access_token=" + Uri.EscapeDataString(_accessToken);
            }
            var uri = new Uri(_baseUrl + fullPath);
            var content = new HttpStringContent(body.Stringify(), Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/json");
            using (var resp = await _http.PostAsync(uri, content))
            {
                string text = await resp.Content.ReadAsStringAsync();
                EnsureSuccess(resp, text);
                return Parse(text);
            }
        }

        private async Task<JsonObject> PutAsync(string path, JsonObject body)
        {
            string fullPath = path + (path.Contains("?") ? "&" : "?") +
                              "access_token=" + Uri.EscapeDataString(_accessToken);
            var uri = new Uri(_baseUrl + fullPath);
            var content = new HttpStringContent(body.Stringify(), Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
            using (var resp = await _http.SendRequestAsync(request))
            {
                string text = await resp.Content.ReadAsStringAsync();
                EnsureSuccess(resp, text);
                return Parse(text);
            }
        }

        private static void EnsureSuccess(HttpResponseMessage resp, string text)
        {
            if (resp.IsSuccessStatusCode) return;

            string message = "HTTP " + (int)resp.StatusCode;
            bool parsedStructured = false;
            try
            {
                JsonObject err;
                if (JsonObject.TryParse(text, out err))
                {
                    string code = GetString(err, "errcode");
                    string detail = GetString(err, "error");
                    if (!string.IsNullOrEmpty(detail))
                    {
                        message = detail + (string.IsNullOrEmpty(code) ? "" : " (" + code + ")");
                        parsedStructured = true;
                    }
                }
            }
            catch { }

            // For non-Matrix errors (e.g. proxy HTML pages) include a snippet of the body.
            if (!parsedStructured && !string.IsNullOrEmpty(text))
            {
                string snippet = text.Length > 200 ? text.Substring(0, 200) : text;
                message += ": " + snippet;
            }

            throw new MatrixException(message, (int)resp.StatusCode);
        }

        private static JsonObject Parse(string text)
        {
            JsonObject obj;
            if (JsonObject.TryParse(text, out obj)) return obj;
            return new JsonObject();
        }

        internal static string GetString(JsonObject obj, string key)
        {
            try
            {
                if (obj != null && obj.ContainsKey(key) && obj[key].ValueType == JsonValueType.String)
                    return obj.GetNamedString(key);
            }
            catch { }
            return null;
        }
    }

    internal class MatrixException : Exception
    {
        public int StatusCode { get; }
        public MatrixException(string message, int statusCode) : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
