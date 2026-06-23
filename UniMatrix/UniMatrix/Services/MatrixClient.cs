using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Web.Http;
using UniMatrix.Models;

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

        /// <summary>
        /// Uploads raw media bytes to the homeserver's content repository and returns the
        /// resulting mxc:// URI (null on failure). Tries the current v3 endpoint first, then
        /// falls back to the legacy r0 one for older servers. Authentication uses ONLY the
        /// access_token query parameter (never an Authorization header), matching the rest of
        /// the client and avoiding matrix.org's M_MISSING_TOKEN "mixing" rejection.
        /// </summary>
        public async Task<string> UploadMediaAsync(Windows.Storage.Streams.IBuffer bytes, string contentType, string filename)
        {
            if (bytes == null) return null;
            if (string.IsNullOrEmpty(contentType)) contentType = "application/octet-stream";
            string tok = Uri.EscapeDataString(_accessToken);
            string nameQuery = string.IsNullOrEmpty(filename) ? "" : "&filename=" + Uri.EscapeDataString(filename);

            var candidates = new[]
            {
                _baseUrl + "/_matrix/media/v3/upload?access_token=" + tok + nameQuery,
                _baseUrl + "/_matrix/media/r0/upload?access_token=" + tok + nameQuery,
            };

            foreach (var url in candidates)
            {
                try
                {
                    var content = new HttpBufferContent(bytes);
                    content.Headers.ContentType = new Windows.Web.Http.Headers.HttpMediaTypeHeaderValue(contentType);
                    using (var resp = await _http.PostAsync(new Uri(url), content))
                    {
                        string text = await resp.Content.ReadAsStringAsync();
                        if (resp.IsSuccessStatusCode)
                        {
                            string mxc = GetString(Parse(text), "content_uri");
                            if (!string.IsNullOrEmpty(mxc)) return mxc;
                        }
                        else
                        {
                            string snippet = text != null && text.Length > 160 ? text.Substring(0, 160) : text;
                            App.Log("Upload " + (int)resp.StatusCode + " :: " + snippet);
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.Log("Upload EXC: " + ex.Message);
                }
            }
            return null;
        }

        /// <summary>
        /// Sends an m.image message referencing an already-uploaded mxc:// URI. The info block
        /// (mimetype/size/dimensions) is optional metadata that other clients use for layout.
        /// </summary>
        public async Task<string> SendImageMessageAsync(string roomId, string mxc, string filename,
            string mimetype, int width, int height, ulong size)
        {
            string txnId = "m" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string path = "/_matrix/client/r0/rooms/" + Uri.EscapeDataString(roomId) +
                          "/send/m.room.message/" + Uri.EscapeDataString(txnId);

            var info = new JsonObject();
            if (!string.IsNullOrEmpty(mimetype)) info["mimetype"] = JsonValue.CreateStringValue(mimetype);
            if (width > 0) info["w"] = JsonValue.CreateNumberValue(width);
            if (height > 0) info["h"] = JsonValue.CreateNumberValue(height);
            if (size > 0) info["size"] = JsonValue.CreateNumberValue(size);

            var content = new JsonObject
            {
                ["msgtype"] = JsonValue.CreateStringValue("m.image"),
                ["body"] = JsonValue.CreateStringValue(string.IsNullOrEmpty(filename) ? "image" : filename),
                ["url"] = JsonValue.CreateStringValue(mxc),
                ["info"] = info
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

        /// <summary>
        /// Starts a direct 1:1 chat with the given user (e.g. "@alice:matrix.org"). Creates an
        /// invite-only room with is_direct=true (so the invitee's client treats it as a DM), then
        /// records the room in the user's m.direct account-data map so every client — ours and
        /// theirs — recognizes it as a direct chat. Returns the new room id.
        /// </summary>
        public async Task<string> CreateDirectChatAsync(string userId)
        {
            userId = (userId ?? "").Trim();
            var body = new JsonObject
            {
                // trusted_private_chat = invite-only, and invitees get the creator's power level
                // (the usual preset for a peer-to-peer DM).
                ["preset"] = JsonValue.CreateStringValue("trusted_private_chat"),
                ["is_direct"] = JsonValue.CreateBooleanValue(true),
                ["invite"] = new JsonArray { JsonValue.CreateStringValue(userId) }
            };
            var resp = await PostAsync("/_matrix/client/r0/createRoom", body, requireAuth: true);
            string roomId = GetString(resp, "room_id");

            if (!string.IsNullOrEmpty(roomId))
            {
                // Best-effort: the room already works as a DM; flagging it in m.direct just lets
                // clients label/sort it as one. Don't fail the whole operation if this PUT fails.
                try { await AddDirectRoomAsync(userId, roomId); }
                catch { }
            }
            return roomId;
        }

        /// <summary>
        /// Merges a (userId -&gt; roomId) entry into the m.direct account-data map, preserving any
        /// existing direct rooms for other users (and for this user).
        /// </summary>
        private async Task AddDirectRoomAsync(string userId, string roomId)
        {
            if (string.IsNullOrEmpty(UserId)) return;
            string basePath = "/_matrix/client/r0/user/" + Uri.EscapeDataString(UserId) +
                              "/account_data/m.direct";

            // Read the current map. A 404 (never set) surfaces as an exception from EnsureSuccess;
            // treat that as an empty map.
            JsonObject map = null;
            try
            {
                string getPath = basePath + "?access_token=" + Uri.EscapeDataString(_accessToken);
                map = await GetAsync(getPath, CancellationToken.None);
            }
            catch { map = null; }
            if (map == null) map = new JsonObject();

            JsonArray list = (map.ContainsKey(userId) && map[userId].ValueType == JsonValueType.Array)
                ? map.GetNamedArray(userId)
                : new JsonArray();

            bool exists = false;
            foreach (var item in list)
            {
                if (item.ValueType == JsonValueType.String && item.GetString() == roomId) { exists = true; break; }
            }
            if (!exists) list.Add(JsonValue.CreateStringValue(roomId));
            map[userId] = list;

            await PutAsync(basePath, map);
        }

        public async Task LeaveRoomAsync(string roomId)
        {
            string id = Uri.EscapeDataString(roomId);
            await PostAsync("/_matrix/client/r0/rooms/" + id + "/leave", new JsonObject(), requireAuth: true);
            try { await PostAsync("/_matrix/client/r0/rooms/" + id + "/forget", new JsonObject(), requireAuth: true); }
            catch { /* Best effort: leaving is what matters; forget can fail harmlessly. */ }
        }

        /// <summary>
        /// Joins a room by its id (!abc:server) or alias (#room:server). The room then
        /// appears on the next /sync pass. Returns the joined room id.
        /// </summary>
        public async Task<string> JoinRoomAsync(string roomIdOrAlias)
        {
            string target = Uri.EscapeDataString(roomIdOrAlias.Trim());
            var resp = await PostAsync("/_matrix/client/r0/join/" + target, new JsonObject(), requireAuth: true);
            return GetString(resp, "room_id");
        }

        /// <summary>
        /// Queries the public room directory. Pass an empty <paramref name="searchTerm"/> to
        /// list the most popular rooms, or a term to filter. <paramref name="server"/> may name a
        /// remote homeserver's directory (e.g. "matrix.org"); leave empty for the user's own.
        /// </summary>
        public async Task<List<PublicRoomEntry>> GetPublicRoomsAsync(string server, string searchTerm, CancellationToken ct)
        {
            string path = "/_matrix/client/r0/publicRooms";
            if (!string.IsNullOrWhiteSpace(server))
                path += "?server=" + Uri.EscapeDataString(server.Trim());

            var body = new JsonObject { ["limit"] = JsonValue.CreateNumberValue(50) };
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                body["filter"] = new JsonObject
                {
                    ["generic_search_term"] = JsonValue.CreateStringValue(searchTerm.Trim())
                };
            }

            var resp = await PostAsync(path, body, requireAuth: true);

            var list = new List<PublicRoomEntry>();
            if (resp != null && resp.ContainsKey("chunk") && resp["chunk"].ValueType == JsonValueType.Array)
            {
                foreach (var item in resp.GetNamedArray("chunk"))
                {
                    if (item.ValueType != JsonValueType.Object) continue;
                    var o = item.GetObject();
                    list.Add(new PublicRoomEntry
                    {
                        RoomId = GetString(o, "room_id"),
                        Name = GetString(o, "name"),
                        Topic = GetString(o, "topic"),
                        CanonicalAlias = GetString(o, "canonical_alias"),
                        MemberCount = (int)GetNumber(o, "num_joined_members")
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// Fetches a page of room history (newest-first, dir=b). Pass the <paramref name="from"/>
        /// token returned in a previous response's "end" field to page further back. Note that
        /// <paramref name="limit"/> counts ALL events (state, membership, etc.), not just messages.
        /// </summary>
        public async Task<JsonObject> GetRoomMessagesAsync(string roomId, int limit, string from, CancellationToken ct)
        {
            string path = "/_matrix/client/r0/rooms/" + Uri.EscapeDataString(roomId) +
                          "/messages?dir=b&limit=" + limit +
                          "&access_token=" + Uri.EscapeDataString(_accessToken);
            if (!string.IsNullOrEmpty(from))
                path += "&from=" + Uri.EscapeDataString(from);
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

        /// <summary>
        /// Downloads the FULL-resolution original (no thumbnail) for an mxc:// URL, returning
        /// null on failure. Used by the full-screen image viewer. Tries the authenticated v1
        /// download endpoint first, then the legacy r0 one. Auth via access_token query param only.
        /// </summary>
        public async Task<Windows.Storage.Streams.IBuffer> FetchOriginalAsync(string mxc)
        {
            string serverAndId = ParseMxc(mxc);
            if (serverAndId == null) return null;

            string tok = Uri.EscapeDataString(_accessToken);
            var candidates = new[]
            {
                _baseUrl + "/_matrix/client/v1/media/download/" + serverAndId + "?access_token=" + tok,
                _baseUrl + "/_matrix/media/r0/download/" + serverAndId + "?access_token=" + tok,
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
                        App.Log("Original " + (int)resp.StatusCode + " " + mxc + " :: " + body);
                    }
                }
                catch (Exception ex)
                {
                    App.Log("Original EXC " + mxc + ": " + ex.Message);
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
            // ConfigureAwait(false) keeps the (potentially large) response read and
            // JSON parse off the UI thread so the app doesn't freeze on big syncs.
            using (var resp = await _http.GetAsync(uri).AsTask(ct).ConfigureAwait(false))
            {
                string text = await resp.Content.ReadAsStringAsync().AsTask(ct).ConfigureAwait(false);
                EnsureSuccess(resp, text);
                // Parse on a background thread — Windows.Data.Json can take seconds
                // on a large initial-sync payload on low-end hardware.
                var obj = await Task.Run(() => Parse(text), ct).ConfigureAwait(false);
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

        internal static double GetNumber(JsonObject obj, string key)
        {
            try
            {
                if (obj != null && obj.ContainsKey(key) && obj[key].ValueType == JsonValueType.Number)
                    return obj.GetNamedNumber(key);
            }
            catch { }
            return 0;
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
