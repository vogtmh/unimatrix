using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace UniMatrix.Services
{
    /// <summary>A single sign-in shown on the account "Sessions" page.</summary>
    internal sealed class MasSession
    {
        public string Id;          // the MAS session id (needed for the end-session mutation)
        public string DeviceId;    // the Matrix device id (used to match the C-S device list)
        public bool IsOauth2;      // true => Oauth2Session, false => CompatSession
    }

    /// <summary>
    /// Talks to a Matrix Authentication Service (MAS / next-gen-auth, MSC3861) account server such
    /// as account.matrix.org. matrix.org has migrated to MAS, which DISABLES the Client-Server
    /// device-management endpoints (DELETE /devices/{id}, POST /delete_devices) for app tokens —
    /// the only sanctioned way to remove a session is the MAS account web page, and that page's
    /// OAuth login won't render in the Lumia's old browser.
    ///
    /// This client replicates exactly what that web page does, but over the app's own HTTP stack so
    /// no browser is involved:
    ///   1. GET  /login?kind=manage_account  -> grab the csrf cookie + hidden csrf form token
    ///   2. POST /login?kind=manage_account  with csrf + username + password -> a browser session cookie
    ///   3. POST /graphql  (authenticated by that cookie) to list and end app sessions
    ///
    /// It is unofficial: it depends on the MAS HTML login form and private GraphQL API, which could
    /// change. It only works for password logins (not SSO/2FA).
    /// </summary>
    internal sealed class MasClient
    {
        private readonly HttpClient _http;
        private string _masOrigin; // e.g. "https://account.matrix.org"
        private bool _loggedIn;

        public bool IsLoggedIn { get { return _loggedIn; } }

        public MasClient()
        {
            // A dedicated filter keeps MAS cookies isolated and follows the login redirect chain.
            var filter = new HttpBaseProtocolFilter { AllowAutoRedirect = true };
            _http = new HttpClient(filter);
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("UniMatrix/1.0");
        }

        /// <summary>
        /// Signs in to the MAS account UI with a username (localpart or full MXID) and password.
        /// Returns true on success. Safe to call again to re-establish an expired session.
        /// </summary>
        public async Task<bool> LoginAsync(string accountUri, string username, string password)
        {
            _loggedIn = false;
            try
            {
                var acct = new Uri(accountUri);
                _masOrigin = acct.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped);
            }
            catch (Exception ex)
            {
                App.Log("MAS: bad account uri '" + accountUri + "': " + ex.Message);
                return false;
            }

            var loginUri = new Uri(_masOrigin + "/login?kind=manage_account");

            // 1. GET the login page to obtain the csrf cookie + hidden csrf form token.
            string html;
            try
            {
                using (var resp = await _http.GetAsync(loginUri))
                    html = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex) { App.Log("MAS: login GET failed: " + ex.Message); return false; }

            string csrf = ExtractCsrf(html);
            if (string.IsNullOrEmpty(csrf))
            {
                App.Log("MAS: no csrf token on login page (form changed or SSO-only?)");
                return false;
            }

            // 2. POST the credentials. The cookie jar carries the csrf cookie automatically.
            try
            {
                var form = new HttpFormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("csrf", csrf),
                    new KeyValuePair<string, string>("username", username ?? ""),
                    new KeyValuePair<string, string>("password", password ?? "")
                });
                using (var resp = await _http.PostAsync(loginUri, form))
                    await resp.Content.ReadAsStringAsync(); // drain; redirects already followed
            }
            catch (Exception ex) { App.Log("MAS: login POST failed: " + ex.Message); return false; }

            // 3. Confirm the session is authenticated by asking who we are.
            var who = await GraphQLAsync("{ viewer { __typename } }", null);
            _loggedIn = ViewerIsUser(who);
            App.Log("MAS: login " + (_loggedIn ? "ok" : "failed (wrong password / SSO / 2FA?)") +
                    " user=" + username);
            return _loggedIn;
        }

        /// <summary>Lists every active app session (Matrix client sign-in) on the account.</summary>
        public async Task<List<MasSession>> ListSessionsAsync()
        {
            var result = new List<MasSession>();
            const string query =
                "query S($after: String) { viewer { __typename ... on User { " +
                "appSessions(first: 100, state: ACTIVE, after: $after) { " +
                "edges { node { __typename " +
                "... on CompatSession { id deviceId } " +
                "... on Oauth2Session { id scope } } } " +
                "pageInfo { hasNextPage endCursor } } } } }";

            string after = null;
            for (int page = 0; page < 100; page++) // hard cap so a bad cursor can't loop forever
            {
                var vars = new JsonObject();
                if (after != null) vars["after"] = JsonValue.CreateStringValue(after);

                var data = await GraphQLAsync(query, vars);
                if (data == null) break;
                if (data.ContainsKey("errors"))
                    App.Log("MAS: list errors :: " + Trunc(data.GetNamedArray("errors").Stringify()));

                JsonObject conn = TryGetAppSessions(data);
                if (conn == null) break;

                JsonArray edges = conn.ContainsKey("edges") && conn["edges"].ValueType == JsonValueType.Array
                    ? conn.GetNamedArray("edges") : null;
                if (edges != null)
                {
                    foreach (var edge in edges)
                    {
                        try
                        {
                            var node = edge.GetObject().GetNamedObject("node");
                            string tn = node.GetNamedString("__typename", "");
                            string id = node.GetNamedString("id", "");
                            if (string.IsNullOrEmpty(id)) continue;
                            bool isOauth = tn == "Oauth2Session";
                            // CompatSession exposes deviceId directly; Oauth2Session encodes it in
                            // its OAuth scope (MSC2967: ...client:device:<id>).
                            string dev = isOauth
                                ? DeviceIdFromScope(node.GetNamedString("scope", ""))
                                : node.GetNamedString("deviceId", "");
                            result.Add(new MasSession
                            {
                                Id = id,
                                DeviceId = string.IsNullOrEmpty(dev) ? null : dev,
                                IsOauth2 = isOauth
                            });
                        }
                        catch { }
                    }
                }

                bool hasNext = false;
                try
                {
                    var pi = conn.GetNamedObject("pageInfo");
                    hasNext = pi.GetNamedBoolean("hasNextPage", false);
                    after = pi.GetNamedString("endCursor", null);
                }
                catch { }
                if (!hasNext || string.IsNullOrEmpty(after)) break;
            }

            App.Log("MAS: listed " + result.Count + " active sessions");
            return result;
        }

        /// <summary>Ends (signs out) a single session. Returns true on success.</summary>
        public async Task<bool> EndSessionAsync(MasSession s)
        {
            if (s == null || string.IsNullOrEmpty(s.Id)) return false;
            string query = s.IsOauth2
                ? "mutation E($id: ID!) { endOauth2Session(input: {oauth2SessionId: $id}) { status } }"
                : "mutation E($id: ID!) { endCompatSession(input: {compatSessionId: $id}) { status } }";
            var vars = new JsonObject { ["id"] = JsonValue.CreateStringValue(s.Id) };

            var data = await GraphQLAsync(query, vars);
            if (data == null) return false;
            if (data.ContainsKey("errors"))
            {
                App.Log("MAS: end session " + s.Id + " errors :: " + Trunc(data.Stringify()));
                return false;
            }
            return true;
        }

        // ---- internals ----

        private static string ExtractCsrf(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var m = Regex.Match(html, "name=\"csrf\"\\s+value=\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
            // Tolerate attribute order (value before name).
            m = Regex.Match(html, "value=\"([^\"]+)\"\\s+name=\"csrf\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Extracts the Matrix device id from an OAuth scope string (MSC2967 device scope,
        /// e.g. "urn:matrix:org.matrix.msc2967.client:device:ABCD" or the stable
        /// "urn:matrix:client:device:ABCD"). Returns null if the scope has no device token.</summary>
        private static string DeviceIdFromScope(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return null;
            foreach (var token in scope.Split(' '))
            {
                int idx = token.IndexOf(":device:");
                if (idx >= 0) return token.Substring(idx + ":device:".Length);
            }
            return null;
        }

        private async Task<JsonObject> GraphQLAsync(string query, JsonObject variables)
        {
            if (string.IsNullOrEmpty(_masOrigin)) return null;
            var body = new JsonObject
            {
                ["query"] = JsonValue.CreateStringValue(query),
                ["variables"] = variables ?? new JsonObject()
            };
            var content = new HttpStringContent(body.Stringify(),
                Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/json");
            var uri = new Uri(_masOrigin + "/graphql");
            try
            {
                using (var resp = await _http.PostAsync(uri, content))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        App.Log("MAS: graphql " + (int)resp.StatusCode + " :: " + Trunc(text));
                    JsonObject obj;
                    return JsonObject.TryParse(text, out obj) ? obj : null;
                }
            }
            catch (Exception ex) { App.Log("MAS: graphql exc: " + ex.Message); return null; }
        }

        private static bool ViewerIsUser(JsonObject response)
        {
            try
            {
                var viewer = response.GetNamedObject("data").GetNamedObject("viewer");
                return viewer.GetNamedString("__typename", "") == "User";
            }
            catch { return false; }
        }

        private static JsonObject TryGetAppSessions(JsonObject response)
        {
            try
            {
                var viewer = response.GetNamedObject("data").GetNamedObject("viewer");
                if (!viewer.ContainsKey("appSessions")) return null; // Anonymous viewer
                return viewer.GetNamedObject("appSessions");
            }
            catch { return null; }
        }

        private static string Trunc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 300 ? s.Substring(0, 300) : s;
        }
    }
}
