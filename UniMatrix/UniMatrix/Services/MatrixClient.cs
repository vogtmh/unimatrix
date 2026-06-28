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

    /// <summary>One of the account's sessions (devices), as returned by GET /devices.</summary>
    internal class DeviceListEntry
    {
        public string DeviceId { get; set; }
        public string DisplayName { get; set; }
        public string LastSeenIp { get; set; }
        public long LastSeenTs { get; set; }
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

        // Cached OAuth 2.0 (MSC2965) account-management URL; "" once checked and absent.
        private bool _accountMgmtChecked;
        private string _accountManagementUri;

        public MatrixClient()
        {
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("UniMatrix/1.0");
        }

        public string AccessToken
        {
            get { return _accessToken; }
            set { _accessToken = value; }
        }

        /// <summary>The logged-in user's Matrix id (e.g. "@me:matrix.org"). Set on login/restore;
        /// used for account-data calls such as the m.direct DM map.</summary>
        public string UserId { get; set; }

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
            UserId = result.UserId;
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

            App.Log("TX " + roomId + " type=m.room.message content=" + content.Stringify());
            var resp = await PutAsync(path, content);
            return GetString(resp, "event_id");
        }

        /// <summary>
        /// Sends an arbitrary room event with a caller-supplied content object. Used for WebRTC
        /// call signalling (m.call.invite/answer/candidates/hangup), where the content shape is
        /// defined by the Matrix VoIP spec rather than the message helpers above. Returns the
        /// event_id, or null on failure (signalling is best-effort; a dropped candidate just
        /// slows ICE rather than breaking the call).
        /// </summary>
        public async Task<string> SendEventAsync(string roomId, string eventType, JsonObject content)
        {
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(eventType) || content == null)
                return null;

            string txnId = "m" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string path = "/_matrix/client/r0/rooms/" + Uri.EscapeDataString(roomId) +
                          "/send/" + Uri.EscapeDataString(eventType) + "/" + Uri.EscapeDataString(txnId);

            App.Log("TX " + roomId + " type=" + eventType + " content=" + content.Stringify());
            var resp = await PutAsync(path, content);
            return GetString(resp, "event_id");
        }

        /// <summary>
        /// Sends a state event (PUT .../state/{type}/{stateKey}). Used to enable room encryption
        /// (m.room.encryption). Returns the event_id, or null on failure.
        /// </summary>
        public async Task<string> SendStateEventAsync(string roomId, string eventType, JsonObject content, string stateKey = "")
        {
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(eventType) || content == null)
                return null;

            string path = "/_matrix/client/r0/rooms/" + Uri.EscapeDataString(roomId) +
                          "/state/" + Uri.EscapeDataString(eventType) +
                          "/" + Uri.EscapeDataString(stateKey ?? "");

            App.Log("TX-state " + roomId + " type=" + eventType + " state_key=" + (stateKey ?? "") + " content=" + content.Stringify());
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

            App.Log("TX " + roomId + " type=m.room.message content=" + content.Stringify());
            var resp = await PutAsync(path, content);
            return GetString(resp, "event_id");
        }

        /// <summary>
        /// Sends an m.read receipt up to <paramref name="eventId"/>, telling the homeserver the
        /// user has read the room up to that event. This resets the room's server-side
        /// notification_count, so subsequent /sync passes stop reporting it as unread (otherwise
        /// the unread badge reappears every sync/relaunch even after the user has read it).
        /// </summary>
        public async Task SendReadReceiptAsync(string roomId, string eventId)
        {
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(eventId)) return;
            string path = "/_matrix/client/r0/rooms/" + Uri.EscapeDataString(roomId) +
                          "/receipt/m.read/" + Uri.EscapeDataString(eventId);
            await PostAsync(path, new JsonObject(), requireAuth: true);
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
            App.Log("DM: createRoom invite=[" + userId + "] is_direct=true preset=trusted_private_chat");
            var body = new JsonObject
            {
                // trusted_private_chat = invite-only, and invitees get the creator's power level
                // (the usual preset for a peer-to-peer DM).
                ["preset"] = JsonValue.CreateStringValue("trusted_private_chat"),
                ["is_direct"] = JsonValue.CreateBooleanValue(true),
                ["invite"] = new JsonArray { JsonValue.CreateStringValue(userId) }
            };
            JsonObject resp;
            try
            {
                resp = await PostAsync("/_matrix/client/r0/createRoom", body, requireAuth: true);
            }
            catch (Exception ex)
            {
                // A failed invite (unknown user, wrong server, federation off) surfaces here.
                App.Log("DM: createRoom FAILED: " + ex.Message);
                throw;
            }
            string roomId = GetString(resp, "room_id");
            App.Log("DM: createRoom ok room_id=" + (roomId ?? "<null>") +
                    " resp=" + (resp != null ? resp.Stringify() : "<null>"));

            if (!string.IsNullOrEmpty(roomId))
            {
                // Best-effort: the room already works as a DM; flagging it in m.direct just lets
                // clients label/sort it as one. Don't fail the whole operation if this PUT fails.
                try { await AddDirectRoomAsync(userId, roomId); App.Log("DM: m.direct updated for " + userId); }
                catch (Exception ex) { App.Log("DM: m.direct update failed (non-fatal): " + ex.Message); }
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
        /// Fetches a user's global profile avatar (the mxc:// url), or null if they have none /
        /// the lookup fails. Used as a fallback for invite screens when the stripped invite_state
        /// doesn't carry the inviter's avatar. Profile lookups are unauthenticated in the spec.
        /// </summary>
        public async Task<string> GetProfileAvatarAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            try
            {
                string path = "/_matrix/client/r0/profile/" + Uri.EscapeDataString(userId) + "/avatar_url";
                var resp = await GetAsync(path, CancellationToken.None);
                return GetString(resp, "avatar_url");
            }
            catch (Exception ex)
            {
                App.Log("Profile avatar lookup failed for " + userId + ": " + ex.Message);
                return null;
            }
        }

        // ---- Device (session) management ----

        /// <summary>Lists all of the account's sessions (devices) with their display names and last-seen info.</summary>
        public async Task<List<DeviceListEntry>> GetDevicesAsync()
        {
            var list = new List<DeviceListEntry>();
            string path = "/_matrix/client/r0/devices?access_token=" + Uri.EscapeDataString(_accessToken);
            var resp = await GetAsync(path, CancellationToken.None);
            if (resp != null && resp.ContainsKey("devices") && resp["devices"].ValueType == JsonValueType.Array)
            {
                foreach (var v in resp.GetNamedArray("devices"))
                {
                    if (v.ValueType != JsonValueType.Object) continue;
                    var o = v.GetObject();
                    list.Add(new DeviceListEntry
                    {
                        DeviceId = GetString(o, "device_id"),
                        DisplayName = GetString(o, "display_name"),
                        LastSeenIp = GetString(o, "last_seen_ip"),
                        LastSeenTs = (long)GetNumber(o, "last_seen_ts")
                    });
                }
            }
            return list;
        }

        /// <summary>Renames a session (sets its public display name).</summary>
        public async Task RenameDeviceAsync(string deviceId, string displayName)
        {
            string path = "/_matrix/client/r0/devices/" + Uri.EscapeDataString(deviceId);
            var body = new JsonObject { ["display_name"] = JsonValue.CreateStringValue(displayName ?? "") };
            await PutAsync(path, body);
        }

        /// <summary>
        /// On next-gen-auth (OAuth 2.0 / MSC2965) homeservers — e.g. matrix.org via the Matrix
        /// Authentication Service — the Client-Server password device-delete flow is disabled and
        /// sessions are managed through the server's account-management web page instead. Returns
        /// that page's URL when the server advertises OAuth 2.0 auth, or null for a legacy
        /// password-auth homeserver (in which case the in-app DELETE flow is used). Cached.
        /// </summary>
        public async Task<string> GetAccountManagementUriAsync()
        {
            if (_accountMgmtChecked)
                return string.IsNullOrEmpty(_accountManagementUri) ? null : _accountManagementUri;
            _accountMgmtChecked = true;
            _accountManagementUri = "";

            // Preferred: GET /_matrix/client/v1/auth_metadata (spec v1.15+) exposes the OAuth 2.0
            // server metadata, including account_management_uri.
            try
            {
                var uri = new Uri(_baseUrl + "/_matrix/client/v1/auth_metadata");
                using (var resp = await _http.GetAsync(uri))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    JsonObject meta;
                    if (resp.IsSuccessStatusCode && JsonObject.TryParse(text, out meta))
                    {
                        string amu = GetString(meta, "account_management_uri");
                        if (!string.IsNullOrEmpty(amu))
                        {
                            _accountManagementUri = amu;
                            App.Log("DEVICES: OAuth account_management_uri=" + amu);
                            return amu;
                        }
                    }
                }
            }
            catch (Exception ex) { App.Log("DEVICES: auth_metadata lookup failed: " + ex.Message); }

            // Fallback: the .well-known document's org.matrix.msc2965.authentication.account field.
            try
            {
                var uri = new Uri(_baseUrl + "/.well-known/matrix/client");
                using (var resp = await _http.GetAsync(uri))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    JsonObject wk;
                    if (resp.IsSuccessStatusCode && JsonObject.TryParse(text, out wk) &&
                        wk.ContainsKey("org.matrix.msc2965.authentication"))
                    {
                        var auth = wk.GetNamedObject("org.matrix.msc2965.authentication");
                        string acc = GetString(auth, "account");
                        if (!string.IsNullOrEmpty(acc))
                        {
                            _accountManagementUri = acc;
                            App.Log("DEVICES: OAuth account uri (well-known)=" + acc);
                            return acc;
                        }
                    }
                }
            }
            catch (Exception ex) { App.Log("DEVICES: well-known lookup failed: " + ex.Message); }

            App.Log("DEVICES: server uses legacy password auth (no account_management_uri)");
            return null;
        }

        /// <summary>
        /// Deletes (signs out) a single session on a legacy password-auth homeserver via
        /// DELETE /devices/{id}. Guarded by user-interactive auth: the first call returns a 401
        /// carrying a UIA session id, then we resubmit with the account password. On OAuth 2.0
        /// servers use <see cref="GetAccountManagementUriAsync"/> instead — this endpoint is
        /// disabled there. Throws <see cref="MatrixException"/> if the deletion ultimately fails.
        /// </summary>
        public async Task DeleteDeviceAsync(string deviceId, string password)
        {
            string path = "/_matrix/client/r0/devices/" + Uri.EscapeDataString(deviceId) +
                          "?access_token=" + Uri.EscapeDataString(_accessToken);
            var uri = new Uri(_baseUrl + path);
            App.Log("DEVICES: DELETE /devices/" + deviceId);

            // First attempt with no auth dict — expected to 401 with a UIA session id.
            var first = await SendDeleteAsync(uri, new JsonObject());
            if (first.Item1) { App.Log("DEVICES: delete succeeded (no UIA)"); return; }

            string session = null;
            try
            {
                JsonObject info;
                if (JsonObject.TryParse(first.Item2, out info)) session = GetString(info, "session");
            }
            catch { }
            App.Log("DEVICES: delete UIA session=" + (string.IsNullOrEmpty(session) ? "<none>" : "ok"));

            var auth = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue("m.login.password"),
                ["identifier"] = new JsonObject
                {
                    ["type"] = JsonValue.CreateStringValue("m.id.user"),
                    ["user"] = JsonValue.CreateStringValue(UserId ?? "")
                },
                ["password"] = JsonValue.CreateStringValue(password ?? "")
            };
            if (!string.IsNullOrEmpty(session)) auth["session"] = JsonValue.CreateStringValue(session);

            var second = await SendDeleteAsync(uri, new JsonObject { ["auth"] = auth });
            if (second.Item1) { App.Log("DEVICES: delete succeeded after UIA"); return; }

            // Surface a useful error message from the server's JSON body.
            string message = "Couldn't remove the session.";
            try
            {
                JsonObject err;
                if (JsonObject.TryParse(second.Item2, out err))
                {
                    string detail = GetString(err, "error");
                    string code = GetString(err, "errcode");
                    if (!string.IsNullOrEmpty(detail))
                        message = detail + (string.IsNullOrEmpty(code) ? "" : " (" + code + ")");
                }
            }
            catch { }
            throw new MatrixException(message, 0, 0);
        }

        /// <summary>
        /// Deletes (signs out) several sessions. matrix.org (and other MAS/next-gen-auth servers)
        /// no longer serve the bulk POST /delete_devices route (it 404s with M_UNRECOGNIZED), so we
        /// loop the per-device DELETE instead. To avoid hammering the server when the very first
        /// delete fails for a structural reason (auth not accepted / route gone), we abort early and
        /// surface that error. Returns the number of sessions actually removed.
        /// </summary>
        public async Task<int> DeleteDevicesAsync(IEnumerable<string> deviceIds, string password)
        {
            var ids = new List<string>();
            foreach (var id in deviceIds)
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            if (ids.Count == 0) return 0;

            App.Log("DEVICES: bulk delete via per-device loop count=" + ids.Count);
            int removed = 0;
            Exception firstError = null;
            for (int i = 0; i < ids.Count; i++)
            {
                try
                {
                    await DeleteDeviceAsync(ids[i], password);
                    removed++;
                }
                catch (Exception ex)
                {
                    App.Log("DEVICES: bulk delete failed at " + (i + 1) + "/" + ids.Count +
                            " (" + ids[i] + "): " + ex.Message);
                    firstError = ex;
                    // If the FIRST one fails there's a structural problem (auth/route) — don't
                    // pointlessly retry the rest; report it so the user isn't left waiting.
                    if (removed == 0) break;
                }
            }

            App.Log("DEVICES: bulk delete removed=" + removed + "/" + ids.Count);
            if (removed == 0 && firstError != null) throw firstError;
            return removed;
        }

        /// <summary>Sends a DELETE with an optional JSON body. Returns (success, responseBody)
        /// without throwing on non-success status codes (used for the UIA challenge flow).</summary>
        private async Task<Tuple<bool, string>> SendDeleteAsync(Uri uri, JsonObject body)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            if (body != null)
                request.Content = new HttpStringContent(body.Stringify(),
                    Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/json");
            using (var resp = await _http.SendRequestAsync(request))
            {
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 401)
                {
                    string snippet = text == null ? "" : (text.Length > 200 ? text.Substring(0, 200) : text);
                    App.Log("DEVICES: DELETE " + (int)resp.StatusCode + " " + uri.AbsolutePath + " :: " + snippet);
                }
                return Tuple.Create(resp.IsSuccessStatusCode, text);
            }
        }

        /// <summary>
        /// Fetches TURN/STUN server credentials from the homeserver (/voip/turnServer). These are
        /// short-lived (ttl) credentials Element-style clients use so calls work across NATs that
        /// plain STUN can't traverse. Returns null if the homeserver provides none or the call fails
        /// (the caller then falls back to a public STUN server only).
        /// </summary>
        public async Task<TurnServerInfo> GetTurnServerAsync()
        {
            try
            {
                string path = "/_matrix/client/r0/voip/turnServer?access_token=" +
                              Uri.EscapeDataString(_accessToken);
                var resp = await GetAsync(path, CancellationToken.None);
                if (resp == null) return null;

                var info = new TurnServerInfo
                {
                    Username = GetString(resp, "username"),
                    Password = GetString(resp, "password")
                };
                if (resp.ContainsKey("uris") && resp["uris"].ValueType == JsonValueType.Array)
                {
                    foreach (var u in resp.GetNamedArray("uris"))
                    {
                        if (u.ValueType == JsonValueType.String)
                        {
                            string uri = u.GetString();
                            if (!string.IsNullOrEmpty(uri)) info.Uris.Add(uri);
                        }
                    }
                }
                if (info.Uris.Count == 0) return null;
                return info;
            }
            catch (Exception ex)
            {
                App.Log("CALL: turnServer lookup failed: " + ex.Message);
                return null;
            }
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

        // ---- End-to-end encryption (keys, to-device, key backup, account data) ----

        /// <summary>A unique transaction id for idempotent PUTs (timestamp + short GUID).</summary>
        public static string GenerateTxnId()
        {
            return "m" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Uploads this device's identity keys and/or one-time keys (/keys/upload). Either
        /// argument may be null when only the other set is being published. Returns the server's
        /// response, whose one_time_key_counts tells us how many OTKs the server still holds.
        /// </summary>
        public async Task<JsonObject> KeysUploadAsync(JsonObject deviceKeys, JsonObject oneTimeKeys)
        {
            var body = new JsonObject();
            if (deviceKeys != null) body["device_keys"] = deviceKeys;
            if (oneTimeKeys != null) body["one_time_keys"] = oneTimeKeys;
            return await PostAsync("/_matrix/client/r0/keys/upload", body, requireAuth: true);
        }

        /// <summary>Downloads device keys for a set of users (/keys/query).</summary>
        public async Task<JsonObject> KeysQueryAsync(IEnumerable<string> userIds)
        {
            var map = new JsonObject();
            foreach (var uid in userIds)
            {
                if (!string.IsNullOrEmpty(uid)) map[uid] = new JsonArray();
            }
            var body = new JsonObject { ["device_keys"] = map };
            return await PostAsync("/_matrix/client/r0/keys/query", body, requireAuth: true);
        }

        /// <summary>
        /// Claims one one-time key per requested device (/keys/claim) so we can establish Olm
        /// sessions with them. <paramref name="oneTimeKeys"/> maps userId -> (deviceId -> algorithm,
        /// usually "signed_curve25519"). Returns the server response with the claimed keys.
        /// </summary>
        public async Task<JsonObject> KeysClaimAsync(JsonObject oneTimeKeys)
        {
            var body = new JsonObject { ["one_time_keys"] = oneTimeKeys };
            return await PostAsync("/_matrix/client/r0/keys/claim", body, requireAuth: true);
        }

        /// <summary>
        /// Uploads cross-signing signatures (/keys/signatures/upload). <paramref name="signatures"/>
        /// maps userId -> (deviceIdOrKeyId -> signed object incl. its "signatures"). Used to publish
        /// the signature our self-signing key makes over our own device, and the signature our
        /// user-signing key makes over another user's master key. Not UIA-protected.
        /// </summary>
        public async Task<JsonObject> KeysSignaturesUploadAsync(JsonObject signatures)
        {
            return await PostAsync("/_matrix/client/r0/keys/signatures/upload", signatures, requireAuth: true);
        }

        /// <summary>
        /// Publishes the cross-signing public keys (master, self-signing, user-signing) via
        /// /keys/device_signing/upload. This endpoint is User-Interactive-Auth protected (needs a
        /// password stage), so <paramref name="body"/> must already carry an "auth" block. Only used
        /// when bootstrapping cross-signing from scratch (deferred); included for completeness.
        /// </summary>
        public async Task<JsonObject> KeysDeviceSigningUploadAsync(JsonObject body)
        {
            return await PostAsync("/_matrix/client/r0/keys/device_signing/upload", body, requireAuth: true);
        }

        /// <summary>
        /// Sends to-device events (/sendToDevice/{type}/{txn}). <paramref name="messages"/> maps
        /// userId -> (deviceId -> content), with "*" as a deviceId meaning all of a user's devices.
        /// Used to distribute Megolm room keys wrapped in Olm (m.room.encrypted) and SSSS secrets.
        /// </summary>
        public async Task SendToDeviceAsync(string eventType, JsonObject messages)
        {
            string txnId = GenerateTxnId();
            string path = "/_matrix/client/r0/sendToDevice/" + Uri.EscapeDataString(eventType) +
                          "/" + Uri.EscapeDataString(txnId);
            var body = new JsonObject { ["messages"] = messages };
            await PutAsync(path, body);
        }

        /// <summary>Reads a global account-data value (null if never set / 404).</summary>
        public async Task<JsonObject> AccountDataGetAsync(string type)
        {
            if (string.IsNullOrEmpty(UserId)) return null;
            try
            {
                string path = "/_matrix/client/r0/user/" + Uri.EscapeDataString(UserId) +
                              "/account_data/" + Uri.EscapeDataString(type) +
                              "?access_token=" + Uri.EscapeDataString(_accessToken);
                return await GetAsync(path, CancellationToken.None);
            }
            catch { return null; }
        }

        /// <summary>Writes a global account-data value.</summary>
        public async Task AccountDataPutAsync(string type, JsonObject content)
        {
            if (string.IsNullOrEmpty(UserId)) return;
            string path = "/_matrix/client/r0/user/" + Uri.EscapeDataString(UserId) +
                          "/account_data/" + Uri.EscapeDataString(type);
            await PutAsync(path, content);
        }

        /// <summary>
        /// Returns the current server-side key backup version metadata (algorithm, auth_data,
        /// version, count, etag), or null if no backup exists.
        /// </summary>
        public async Task<JsonObject> GetBackupVersionAsync()
        {
            try
            {
                string path = "/_matrix/client/r0/room_keys/version?access_token=" +
                              Uri.EscapeDataString(_accessToken);
                return await GetAsync(path, CancellationToken.None);
            }
            catch { return null; }
        }

        /// <summary>Creates a new key backup version and returns its version id.</summary>
        public async Task<string> CreateBackupVersionAsync(string algorithm, JsonObject authData)
        {
            var body = new JsonObject
            {
                ["algorithm"] = JsonValue.CreateStringValue(algorithm),
                ["auth_data"] = authData
            };
            var resp = await PostAsync("/_matrix/client/r0/room_keys/version", body, requireAuth: true);
            return GetString(resp, "version");
        }

        /// <summary>Uploads one room's Megolm session into the backup at the given version.</summary>
        public async Task BackupKeyPutAsync(string version, string roomId, string sessionId, JsonObject keyData)
        {
            string path = "/_matrix/client/r0/room_keys/keys/" + Uri.EscapeDataString(roomId) +
                          "/" + Uri.EscapeDataString(sessionId) +
                          "?version=" + Uri.EscapeDataString(version);
            await PutAsync(path, keyData);
        }

        /// <summary>Downloads every backed-up Megolm session at the given version.</summary>
        public async Task<JsonObject> BackupKeysGetAsync(string version)
        {
            string path = "/_matrix/client/r0/room_keys/keys?version=" + Uri.EscapeDataString(version) +
                          "&access_token=" + Uri.EscapeDataString(_accessToken);
            return await GetAsync(path, CancellationToken.None);
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
            int retryAfterMs = 0;
            bool parsedStructured = false;
            try
            {
                JsonObject err;
                if (JsonObject.TryParse(text, out err))
                {
                    string code = GetString(err, "errcode");
                    string detail = GetString(err, "error");
                    // 429 rate-limit responses carry the server's requested back-off.
                    retryAfterMs = (int)GetNumber(err, "retry_after_ms");
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

            throw new MatrixException(message, (int)resp.StatusCode, retryAfterMs);
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
        /// <summary>For HTTP 429 (M_LIMIT_EXCEEDED) the server's requested back-off, in ms (0 if none).</summary>
        public int RetryAfterMs { get; }
        public MatrixException(string message, int statusCode, int retryAfterMs = 0) : base(message)
        {
            StatusCode = statusCode;
            RetryAfterMs = retryAfterMs;
        }
    }
}
