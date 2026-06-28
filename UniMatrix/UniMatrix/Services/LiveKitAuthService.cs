using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Web.Http;

namespace UniMatrix.Services
{
    /// <summary>The LiveKit SFU credentials returned by the MatrixRTC authorization service.</summary>
    internal class LiveKitCredentials
    {
        public string Jwt { get; set; }   // LiveKit access token (JWT) authorizing us to join the room
        public string Url { get; set; }   // LiveKit SFU WebSocket URL, e.g. wss://livekit.call.matrix.org
    }

    /// <summary>
    /// Talks to the MatrixRTC authorization service ("lk-jwt-service") to turn a Matrix OpenID token
    /// into LiveKit SFU credentials (a JWT + SFU WebSocket URL). This is step 2 of joining an Element
    /// (MatrixRTC) call: the homeserver vouches for our identity via the OpenID token, and the focus
    /// service issues a LiveKit token for the specific room.
    ///
    /// Flow (per MSC4143 / Element Call): POST {focusServiceUrl}/sfu/get with
    /// { room, openid_token, device_id } -> { jwt, url }.
    ///
    /// SECURITY: the focus service URL comes from the caller's m.rtc.member event, so it is only
    /// trusted as far as the caller is. We require https and only ever send the SHORT-LIVED, SCOPED
    /// Matrix OpenID token (which merely lets the service verify our identity with our homeserver —
    /// it is NOT our account access token). On matrix.org the focus is Element's hosted service.
    /// </summary>
    internal class LiveKitAuthService
    {
        private readonly HttpClient _http = new HttpClient();

        public LiveKitAuthService()
        {
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("UniMatrix/1.0");
        }

        /// <summary>
        /// Exchanges a Matrix OpenID token for LiveKit SFU credentials for <paramref name="room"/>.
        /// Returns null on any failure (logged). <paramref name="focusServiceUrl"/> and
        /// <paramref name="room"/> come from the caller's m.rtc.member focus info.
        /// </summary>
        public async Task<LiveKitCredentials> GetSfuCredentialsAsync(
            string focusServiceUrl, string room, JsonObject openIdToken, string deviceId)
        {
            if (string.IsNullOrEmpty(focusServiceUrl) || string.IsNullOrEmpty(room) || openIdToken == null)
            {
                App.Log("RTC: SFU token exchange skipped (missing focus/room/openid)");
                return null;
            }

            // Only contact https focus services (the OpenID token must not cross plaintext).
            if (!focusServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                App.Log("RTC: refusing non-https focus service url: " + focusServiceUrl);
                return null;
            }

            string endpoint = focusServiceUrl.TrimEnd('/') + "/sfu/get";

            var body = new JsonObject
            {
                ["room"] = JsonValue.CreateStringValue(room),
                ["openid_token"] = openIdToken,
                ["device_id"] = JsonValue.CreateStringValue(deviceId ?? "")
            };

            try
            {
                App.Log("RTC: requesting SFU token from " + endpoint + " for room " + room);
                var content = new HttpStringContent(
                    body.Stringify(), Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/json");
                using (var resp = await _http.PostAsync(new Uri(endpoint), content))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        string snippet = text != null && text.Length > 200 ? text.Substring(0, 200) : text;
                        App.Log("RTC: SFU token HTTP " + (int)resp.StatusCode + " :: " + snippet);
                        return null;
                    }

                    JsonObject parsed;
                    if (!JsonObject.TryParse(text, out parsed))
                    {
                        App.Log("RTC: SFU token response not JSON");
                        return null;
                    }

                    var creds = new LiveKitCredentials
                    {
                        Jwt = MatrixClient.GetString(parsed, "jwt"),
                        Url = MatrixClient.GetString(parsed, "url")
                    };

                    if (string.IsNullOrEmpty(creds.Jwt) || string.IsNullOrEmpty(creds.Url))
                    {
                        App.Log("RTC: SFU token response missing jwt/url");
                        return null;
                    }

                    App.Log("RTC: SFU credentials obtained -> url=" + creds.Url +
                            " jwtLen=" + creds.Jwt.Length);
                    return creds;
                }
            }
            catch (Exception ex)
            {
                App.Log("RTC: SFU token exchange failed: " + ex.Message);
                return null;
            }
        }
    }
}
