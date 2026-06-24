using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using UniMatrix.Models;

#if WEBRTC
using Org.WebRtc;
#endif

namespace UniMatrix.Services
{
    /// <summary>
    /// 1:1 WebRTC audio calling over Matrix VoIP signalling (m.call.*).
    ///
    /// This is the UniMatrix counterpart of the PeerCC sample's Conductor, but the signalling
    /// channel is Matrix room events instead of the sample's socket server:
    ///   - outgoing: CreateOffer -> m.call.invite ; local ICE -> m.call.candidates ; HangupAsync -> m.call.hangup
    ///   - incoming: m.call.invite -> (user accepts) -> CreateAnswer -> m.call.answer ; remote ICE consumed from m.call.candidates
    ///
    /// All Org.WebRtc usage is compiled only when the WEBRTC constant is defined (ARM configs, where
    /// libs\webrtc\ARM\Org.WebRtc.winmd is referenced). On other platforms the class still exists so
    /// the rest of the app compiles, but the call methods report that calling is unavailable.
    ///
    /// We target Matrix VoIP version 0 (the simplest 1:1 profile: no party_id, no glare resolution,
    /// no select_answer) which is enough for a first on-device audio test.
    /// </summary>
    internal sealed class CallService
    {
        private CoreDispatcher _dispatcher;
        private MatrixClient _client;

        // TURN/STUN credentials fetched from the homeserver before each call (plain model, so it
        // lives outside the #if WEBRTC region). Null when the homeserver provides none.
        private TurnServerInfo _turn;

        // Active-call identity (shared between platforms so the UI logic is identical).
        private string _callId;
        private string _roomId;
        private bool _inCall;
        // MSC2746 party identifiers: our own id for this call (sent on every event we emit) and the
        // remote's id (captured from their invite/answer, echoed back in m.call.select_answer).
        private string _localPartyId;
        private string _remotePartyId;

        /// <summary>True when this build actually has the native WebRTC library available.</summary>
        public bool IsWebRtcAvailable
        {
            get
            {
#if WEBRTC
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>True while a call is being set up or is connected.</summary>
        public bool InCall { get { return _inCall; } }

        /// <summary>The room of the active (or pending) call, or null.</summary>
        public string CurrentRoomId { get { return _roomId; } }

        // ---- Events for the UI (all raised on the UI thread) ----

        /// <summary>An incoming call is ringing. Argument is the room id. UI should prompt accept/decline.</summary>
        public event Action<string> IncomingCall;

        /// <summary>Media is flowing (ICE connected).</summary>
        public event Action CallConnected;

        /// <summary>The call ended (hung up, declined, failed or remote gone).</summary>
        public event Action CallEnded;

        /// <summary>Human-readable status for the debug overlay / call UI.</summary>
        public event Action<string> CallStatusChanged;

        public void Initialize(CoreDispatcher dispatcher, MatrixClient client)
        {
            _dispatcher = dispatcher;
            _client = client;
        }

        // ---- Matrix VoIP signalling key names (spec) ----
        private const string KVersion = "version";
        private const string KCallId = "call_id";
        private const string KOffer = "offer";
        private const string KAnswer = "answer";
        private const string KCandidates = "candidates";
        private const string KSdp = "sdp";
        private const string KType = "type";
        private const string KLifetime = "lifetime";
        private const string KCandidate = "candidate";
        private const string KSdpMid = "sdpMid";
        private const string KSdpMLineIndex = "sdpMLineIndex";
        // MSC2746 (VoIP v1) keys. Modern Element ties answer/candidate events to a party_id and
        // drops events whose party_id it doesn't recognise, so a v0 (party-less) answer leaves the
        // peer stuck "Connecting". We therefore signal version "1" with a party_id throughout.
        private const string KPartyId = "party_id";
        private const string KCapabilities = "capabilities";
        private const string KSelectedPartyId = "selected_party_id";
        // VoIP version we advertise. MSC2746 uses the string "1" (v0 used the number 0).
        private static IJsonValue CallVersionValue { get { return JsonValue.CreateStringValue("1"); } }

        private void Status(string s)
        {
            App.Log("CALL: " + s);
            // Status is raised from many threads (UI, background Task.Run, the native WebRTC
            // callback thread), so marshal to the UI thread before notifying the call screen.
            var handler = CallStatusChanged;
            if (handler != null) RunOnUi(() => handler(s));
        }

#if WEBRTC
        private static bool _libInitialized;

        private WebRtcFactory _factory;
        private RTCPeerConnection _pc;
        private IMediaStreamTrack _selfAudioTrack;
        private IMediaStreamTrack _peerAudioTrack;

        private bool _isCaller;
        private bool _remoteDescriptionSet;
        // ICE candidates that arrive before the remote description is applied must be buffered,
        // otherwise AddIceCandidate throws. They're flushed once SetRemoteDescription succeeds.
        private readonly List<RTCIceCandidateInit> _pendingRemoteCandidates = new List<RTCIceCandidateInit>();
        // For an incoming call we hold the offer until the user accepts (so we don't grab the mic
        // or create the peer connection for a call that's about to be declined).
        private string _pendingOfferSdp;

        // ICE candidate counters for diagnostics (how many we sent / received / buffered).
        private int _localCandCount;
        private int _remoteCandApplied;
        private int _remoteCandBuffered;

        // Outgoing local ICE candidates are batched into a single m.call.candidates event instead
        // of one event per candidate: matrix.org rate-limits room sends (M_LIMIT_EXCEEDED / HTTP
        // 429), so firing ~10 separate candidate events floods the limiter and most candidates
        // never reach the peer — ICE then fails. The Matrix VoIP spec's "candidates" array exists
        // precisely so a batch can go in one event. We coalesce candidates that arrive within a
        // short window from the first one, then flush them together (also flushed on
        // end-of-candidates and call teardown).
        private readonly List<JsonObject> _outgoingCandidates = new List<JsonObject>();
        private readonly object _outgoingLock = new object();
        private System.Threading.Timer _candidateFlushTimer;
        private const int CandidateBatchDelayMs = 750;

        /// <summary>Initializes the native WebRTC library exactly once, on the UI thread.</summary>
        private void EnsureLibrary()
        {
            if (_libInitialized) return;
            var queue = EventQueueMaker.Bind(_dispatcher);
            var cfg = new WebRtcLibConfiguration { Queue = queue };
            cfg.AudioCaptureFrameProcessingQueue = EventQueue.GetOrCreateThreadQueueByName("AudioCaptureProcessingQueue");
            cfg.AudioRenderFrameProcessingQueue = EventQueue.GetOrCreateThreadQueueByName("AudioRenderProcessingQueue");
            cfg.VideoFrameProcessingQueue = EventQueue.GetOrCreateThreadQueueByName("VideoFrameProcessingQueue");
            WebRtcLib.Setup(cfg);
            _libInitialized = true;
            Status("WebRTC library initialized.");
        }

        /// <summary>Requests microphone access (the OS prompts on first use). UI-thread only.</summary>
        private static async Task<bool> RequestMicAsync()
        {
            try
            {
                var capture = new Windows.Media.Capture.MediaCapture();
                var settings = new Windows.Media.Capture.MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio,
                    AudioDeviceId = "",
                    VideoDeviceId = ""
                };
                await capture.InitializeAsync(settings);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("CALL: mic access failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Initializes the native library and requests the microphone. Must run on the UI thread
        /// (the WebRTC event queue is bound to the UI dispatcher and the OS mic prompt needs the
        /// UI thread). Call this before <see cref="CreatePeerConnection"/>.
        /// </summary>
        private async Task<bool> PrepareCallAsync()
        {
            EnsureLibrary();
            if (!await RequestMicAsync())
            {
                Status("Microphone permission denied.");
                return false;
            }
            // Pull short-lived TURN credentials from the homeserver so the call can traverse NATs
            // that plain STUN can't. Best-effort: if it fails we fall back to public STUN only.
            _turn = await _client.GetTurnServerAsync();
            if (_turn != null)
                Status("TURN servers: " + _turn.Uris.Count + " (user=" + (string.IsNullOrEmpty(_turn.Username) ? "none" : "set") + ")");
            else
                Status("No TURN servers from homeserver; using public STUN only.");
            return true;
        }

        /// <summary>
        /// Builds the factory, peer connection, audio track and wires events. Runs on a BACKGROUND
        /// thread on purpose: the native constructors block the calling thread until the library
        /// pumps work through the dispatcher they're bound to, so invoking them on the UI thread
        /// deadlocks the whole app (frozen call screen, dead Hang up button).
        /// </summary>
        private bool CreatePeerConnection()
        {
            string captureId = "";
            string renderId = "";
            try
            {
                captureId = Windows.Media.Devices.MediaDevice.GetDefaultAudioCaptureId(
                    Windows.Media.Devices.AudioDeviceRole.Communications) ?? "";
                renderId = Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(
                    Windows.Media.Devices.AudioDeviceRole.Communications) ?? "";
            }
            catch { /* fall back to "" = default device */ }

            var factoryConfig = new WebRtcFactoryConfiguration
            {
                AudioCaptureDeviceId = captureId,
                AudioRenderDeviceId = renderId
            };
            _factory = new WebRtcFactory(factoryConfig);

            // Prefer the homeserver's TURN servers (they relay media when a direct path can't be
            // found); always keep a public STUN server as a fallback for candidate discovery.
            var iceServers = new List<RTCIceServer>();
            if (_turn != null && _turn.Uris.Count > 0)
            {
                iceServers.Add(new RTCIceServer
                {
                    Urls = new List<string>(_turn.Uris),
                    Username = _turn.Username ?? "",
                    Credential = _turn.Password ?? ""
                });
            }
            iceServers.Add(new RTCIceServer { Urls = new List<string> { "stun:stun.l.google.com:19302" } });

            var config = new RTCConfiguration
            {
                Factory = _factory,
                BundlePolicy = RTCBundlePolicy.Balanced,
                IceTransportPolicy = RTCIceTransportPolicy.All,
                IceServers = iceServers
            };

            _pc = new RTCPeerConnection(config);
            _pc.OnIceCandidate += Pc_OnIceCandidate;
            _pc.OnTrack += Pc_OnTrack;
            _pc.OnIceConnectionStateChange += Pc_OnIceConnectionStateChange;
            _pc.OnIceGatheringStateChange += Pc_OnIceGatheringStateChange;

            // Audio-only: create a single local audio track. No video capturer/track.
            var audioOptions = new AudioOptions { Factory = _factory };
            var audioSource = AudioTrackSource.Create(audioOptions);
            _selfAudioTrack = MediaStreamTrack.CreateAudioTrack("SELF_AUDIO", audioSource);
            _pc.AddTrack(_selfAudioTrack);

            Status("Peer connection created.");
            return true;
        }

        private void Pc_OnIceConnectionStateChange()
        {
            var pc = _pc;
            if (pc == null) return;
            var state = pc.IceConnectionState;
            Status("ICE state: " + state);
            if (state == RTCIceConnectionState.Connected || state == RTCIceConnectionState.Completed)
            {
                RunOnUi(() => CallConnected?.Invoke());
            }
            else if (state == RTCIceConnectionState.Failed ||
                     state == RTCIceConnectionState.Closed ||
                     state == RTCIceConnectionState.Disconnected)
            {
                // A disconnect can be transient; only tear down on failed/closed.
                if (state != RTCIceConnectionState.Disconnected)
                    RunOnUi(() => EndCallLocal(notifyRemote: false));
            }
        }

        private void Pc_OnTrack(IRTCTrackEvent evt)
        {
            // Remote audio renders through the audio device automatically once the track arrives;
            // we just keep a reference so it isn't collected.
            if (evt?.Track != null && evt.Track.Kind == "audio")
            {
                _peerAudioTrack = evt.Track;
                Status("Remote audio track received.");
            }
        }

        private void Pc_OnIceCandidate(IRTCPeerConnectionIceEvent evt)
        {
            var cand = evt?.Candidate;
            if (cand == null)
            {
                // End-of-candidates: send whatever is still queued right away.
                FlushOutgoingCandidates();
                Status("Local ICE gathering complete (" + _localCandCount + " sent).");
                return;
            }
            if (string.IsNullOrEmpty(_callId) || string.IsNullOrEmpty(_roomId)) return;

            _localCandCount++;
            var candJson = new JsonObject
            {
                { KCandidate, JsonValue.CreateStringValue(cand.Candidate ?? "") },
                { KSdpMid, JsonValue.CreateStringValue(cand.SdpMid ?? "") },
                { KSdpMLineIndex, JsonValue.CreateNumberValue(cand.SdpMLineIndex ?? 0) }
            };

            // Queue the candidate and arm a one-shot flush. A fixed window from the first queued
            // candidate (rather than resetting on every candidate) bounds us to roughly one
            // m.call.candidates event per window, which keeps us under the homeserver's rate limit.
            lock (_outgoingLock)
            {
                _outgoingCandidates.Add(candJson);
                if (_candidateFlushTimer == null)
                {
                    _candidateFlushTimer = new System.Threading.Timer(
                        _ => FlushOutgoingCandidates(), null,
                        CandidateBatchDelayMs, System.Threading.Timeout.Infinite);
                }
            }
        }

        /// <summary>
        /// Sends all queued local ICE candidates in a single m.call.candidates event. Called from
        /// the batch timer, on end-of-candidates and during teardown. Safe to call when empty.
        /// </summary>
        private void FlushOutgoingCandidates()
        {
            JsonArray arr;
            string callId = _callId;
            lock (_outgoingLock)
            {
                if (_candidateFlushTimer != null)
                {
                    _candidateFlushTimer.Dispose();
                    _candidateFlushTimer = null;
                }
                if (_outgoingCandidates.Count == 0) return;
                arr = new JsonArray();
                foreach (var c in _outgoingCandidates) arr.Add(c);
                _outgoingCandidates.Clear();
            }
            if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(_roomId)) return;

            var content = new JsonObject
            {
                { KCallId, JsonValue.CreateStringValue(callId) },
                { KPartyId, JsonValue.CreateStringValue(_localPartyId ?? "") },
                { KVersion, CallVersionValue },
                { KCandidates, arr }
            };
            Status("Sending " + arr.Count + " local candidate(s).");
            SendSignal("m.call.candidates", content);
        }

        private void Pc_OnIceGatheringStateChange()
        {
            var pc = _pc;
            if (pc == null) return;
            Status("ICE gathering: " + pc.IceGatheringState);
        }

        private void FlushPendingCandidates()
        {
            if (_pc == null) return;
            if (_pendingRemoteCandidates.Count > 0)
                Status("Flushing " + _pendingRemoteCandidates.Count + " buffered remote candidate(s).");
            foreach (var init in _pendingRemoteCandidates)
            {
                try { var _ = _pc.AddIceCandidate(new RTCIceCandidate(init)); }
                catch (Exception ex) { App.Log("CALL: AddIceCandidate (flush) failed: " + ex.Message); }
            }
            _pendingRemoteCandidates.Clear();
        }
#endif

        /// <summary>Places an outgoing audio call to the given room.</summary>
        public async Task PlaceCallAsync(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return;
            if (_inCall) { Status("Already in a call."); return; }
#if WEBRTC
            try
            {
                _roomId = roomId;
                _callId = "c" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                          Guid.NewGuid().ToString("N").Substring(0, 8);
                _localPartyId = NewPartyId();
                _remotePartyId = null;
                _isCaller = true;
                _inCall = true;
                _remoteDescriptionSet = false;

                if (!await PrepareCallAsync()) { EndCallLocal(notifyRemote: false); return; }

                // The native WebRTC calls block the calling thread until the library pumps work
                // through the dispatcher they're bound to. Running them on the UI thread therefore
                // freezes the call screen (stuck "Calling...", unresponsive Hang up button), so we
                // build the connection and create the offer on a background thread.
                bool ok = await Task.Run(async () =>
                {
                    if (!CreatePeerConnection()) return false;

                    var offerOptions = new RTCOfferOptions
                    {
                        OfferToReceiveAudio = true,
                        OfferToReceiveVideo = false
                    };
                    var offer = await _pc.CreateOffer(offerOptions);
                    await _pc.SetLocalDescription(offer);

                    var offerJson = new JsonObject
                    {
                        { KType, JsonValue.CreateStringValue("offer") },
                        { KSdp, JsonValue.CreateStringValue(offer.Sdp) }
                    };
                    var content = new JsonObject
                    {
                        { KCallId, JsonValue.CreateStringValue(_callId) },
                        { KPartyId, JsonValue.CreateStringValue(_localPartyId) },
                        { KVersion, CallVersionValue },
                        { KCapabilities, BuildCapabilities() },
                        { KLifetime, JsonValue.CreateNumberValue(60000) },
                        { KOffer, offerJson }
                    };
                    App.Log("CALL: SEND m.call.invite v=\"1\" party=" + _localPartyId +
                            " sdpLen=" + (offer.Sdp != null ? offer.Sdp.Length : 0));
                    await _client.SendEventAsync(roomId, "m.call.invite", content);
                    return true;
                });

                if (!ok) { EndCallLocal(notifyRemote: false); return; }
                Status("Calling...");
            }
            catch (Exception ex)
            {
                App.Log("CALL: PlaceCall failed: " + ex);
                EndCallLocal(notifyRemote: false);
            }
#else
            Status("Calling is not available in this build (WebRTC library not linked).");
            await Task.CompletedTask;
#endif
        }

        /// <summary>Accepts the currently ringing incoming call.</summary>
        public async Task AcceptIncomingAsync()
        {
#if WEBRTC
            if (!_inCall || _isCaller || _pendingOfferSdp == null)
            {
                Status("No incoming call to accept.");
                return;
            }
            try
            {
                _localPartyId = NewPartyId();
                if (!await PrepareCallAsync()) { EndCallLocal(notifyRemote: true); return; }

                // Build the connection and craft the answer off the UI thread (see PlaceCallAsync).
                bool ok = await Task.Run(async () =>
                {
                    if (!CreatePeerConnection()) return false;

                    var remoteInit = new RTCSessionDescriptionInit { Sdp = _pendingOfferSdp, Type = RTCSdpType.Offer };
                    await _pc.SetRemoteDescription(new RTCSessionDescription(remoteInit));
                    _remoteDescriptionSet = true;
                    FlushPendingCandidates();

                    var answer = await _pc.CreateAnswer(new RTCAnswerOptions());
                    await _pc.SetLocalDescription(answer);

                    var answerJson = new JsonObject
                    {
                        { KType, JsonValue.CreateStringValue("answer") },
                        { KSdp, JsonValue.CreateStringValue(answer.Sdp) }
                    };
                    var content = new JsonObject
                    {
                        { KCallId, JsonValue.CreateStringValue(_callId) },
                        { KPartyId, JsonValue.CreateStringValue(_localPartyId) },
                        { KVersion, CallVersionValue },
                        { KCapabilities, BuildCapabilities() },
                        { KAnswer, answerJson }
                    };
                    App.Log("CALL: SEND m.call.answer v=\"1\" party=" + _localPartyId +
                            " sdpLen=" + (answer.Sdp != null ? answer.Sdp.Length : 0));
                    await _client.SendEventAsync(_roomId, "m.call.answer", content);
                    return true;
                });

                if (!ok) { EndCallLocal(notifyRemote: true); return; }
                _pendingOfferSdp = null;
                Status("Call accepted.");
            }
            catch (Exception ex)
            {
                App.Log("CALL: Accept failed: " + ex);
                EndCallLocal(notifyRemote: true);
            }
#else
            await Task.CompletedTask;
#endif
        }

        /// <summary>Hangs up / declines the active or ringing call.</summary>
        public async Task HangupAsync()
        {
            if (!_inCall) return;
            await Task.Run(() => { }); // keep signature async-friendly
            EndCallLocal(notifyRemote: true);
        }

        /// <summary>
        /// Feeds a raw m.call.* signalling event (from /sync) into the state machine. Safe to call
        /// for every call event; events for other/old calls or our own echoes are filtered out.
        /// </summary>
        public async Task HandleSignalAsync(CallSignal signal)
        {
            if (signal == null || signal.Content == null) return;
            // Ignore our own events echoed back by sync.
            if (!string.IsNullOrEmpty(_client?.UserId) && signal.Sender == _client.UserId) return;

#if WEBRTC
            string type = signal.Type;
            string callId = MatrixClient.GetString(signal.Content, KCallId);

            // Diagnostic: record exactly what the remote sent us. This is essential for interop
            // debugging (e.g. Element) — it shows the version/party_id of inbound events and whether
            // the peer is replying at all when we place a call.
            App.Log("CALL: RECV " + type + " from " + (signal.Sender ?? "?") +
                    " v=" + DescribeVersion(signal.Content) +
                    " party=" + (MatrixClient.GetString(signal.Content, KPartyId) ?? "-") +
                    " callId=" + (callId ?? "-") + " (mine=" + (_callId ?? "-") + ")");

            try
            {
                switch (type)
                {
                    case "m.call.invite":
                        await OnRemoteInvite(signal, callId);
                        break;
                    case "m.call.answer":
                        await OnRemoteAnswer(signal, callId);
                        break;
                    case "m.call.candidates":
                        await OnRemoteCandidates(signal, callId);
                        break;
                    case "m.call.select_answer":
                        // MSC2746: caller tells everyone which answerer won. If we answered and a
                        // different party was selected, the caller picked another device — stop.
                        if (callId == _callId && _inCall && !_isCaller)
                        {
                            string selected = MatrixClient.GetString(signal.Content, KSelectedPartyId);
                            if (!string.IsNullOrEmpty(selected) && !string.IsNullOrEmpty(_localPartyId) &&
                                selected != _localPartyId)
                            {
                                Status("Another device answered; ending here.");
                                EndCallLocal(notifyRemote: false);
                            }
                        }
                        break;
                    case "m.call.hangup":
                    case "m.call.reject":
                        if (callId == _callId && _inCall)
                        {
                            Status("Remote ended the call.");
                            EndCallLocal(notifyRemote: false);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                App.Log("CALL: HandleSignal (" + type + ") failed: " + ex);
            }
#else
            await Task.CompletedTask;
#endif
        }

#if WEBRTC
        private async Task OnRemoteInvite(CallSignal signal, string callId)
        {
            // Drop stale invites (replayed history) and reject if already busy.
            long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - signal.Timestamp;
            if (signal.Timestamp > 0 && ageMs > 60000)
            {
                Status("Ignoring stale invite (" + (ageMs / 1000) + "s old).");
                return;
            }
            if (_inCall)
            {
                Status("Busy; ignoring incoming invite.");
                return;
            }

            JsonObject offer = GetObject(signal.Content, KOffer);
            string sdp = offer != null ? MatrixClient.GetString(offer, KSdp) : null;
            if (string.IsNullOrEmpty(sdp))
            {
                Status("Invite without SDP; ignoring.");
                return;
            }

            _roomId = signal.RoomId;
            _callId = callId;
            _remotePartyId = MatrixClient.GetString(signal.Content, KPartyId);
            _isCaller = false;
            _inCall = true;
            _remoteDescriptionSet = false;
            _pendingOfferSdp = sdp;

            Status("Incoming call from " + (signal.Sender ?? "unknown"));
            RunOnUi(() => IncomingCall?.Invoke(_roomId));
            await Task.CompletedTask;
        }

        private async Task OnRemoteAnswer(CallSignal signal, string callId)
        {
            if (!_inCall || !_isCaller || callId != _callId || _pc == null)
            {
                Status("Ignoring answer (inCall=" + _inCall + " isCaller=" + _isCaller +
                       " idMatch=" + (callId == _callId) + " pc=" + (_pc != null) + ").");
                return;
            }

            JsonObject answer = GetObject(signal.Content, KAnswer);
            string sdp = answer != null ? MatrixClient.GetString(answer, KSdp) : null;
            if (string.IsNullOrEmpty(sdp)) return;

            // MSC2746: the first answerer wins. Record their party_id and tell every device in the
            // room which answer we picked, so the others stop ringing. Ignore later answers.
            if (!string.IsNullOrEmpty(_remotePartyId))
            {
                Status("Ignoring extra answer from a second party.");
                return;
            }
            _remotePartyId = MatrixClient.GetString(signal.Content, KPartyId);

            var init = new RTCSessionDescriptionInit { Sdp = sdp, Type = RTCSdpType.Answer };
            await _pc.SetRemoteDescription(new RTCSessionDescription(init));
            _remoteDescriptionSet = true;
            FlushPendingCandidates();

            if (_remotePartyId != null)
            {
                var selectContent = new JsonObject
                {
                    { KCallId, JsonValue.CreateStringValue(_callId) },
                    { KPartyId, JsonValue.CreateStringValue(_localPartyId ?? "") },
                    { KVersion, CallVersionValue },
                    { KSelectedPartyId, JsonValue.CreateStringValue(_remotePartyId) }
                };
                SendSignal("m.call.select_answer", selectContent);
            }
            Status("Answer received; negotiating media.");
        }

        private async Task OnRemoteCandidates(CallSignal signal, string callId)
        {
            if (!_inCall || callId != _callId)
            {
                Status("Ignoring candidates (inCall=" + _inCall + " idMatch=" + (callId == _callId) + ").");
                return;
            }

            JsonArray candidates = GetArray(signal.Content, KCandidates);
            if (candidates == null) return;

            foreach (var item in candidates)
            {
                if (item.ValueType != JsonValueType.Object) continue;
                JsonObject c = item.GetObject();
                string cand = MatrixClient.GetString(c, KCandidate);
                if (string.IsNullOrEmpty(cand)) continue; // end-of-candidates marker
                string sdpMid = MatrixClient.GetString(c, KSdpMid);
                ushort sdpMLineIndex = 0;
                if (c.ContainsKey(KSdpMLineIndex) && c[KSdpMLineIndex].ValueType == JsonValueType.Number)
                    sdpMLineIndex = (ushort)c.GetNamedNumber(KSdpMLineIndex);

                var init = new RTCIceCandidateInit
                {
                    Candidate = cand,
                    SdpMid = sdpMid,
                    SdpMLineIndex = sdpMLineIndex
                };

                if (_remoteDescriptionSet && _pc != null)
                {
                    try { await _pc.AddIceCandidate(new RTCIceCandidate(init)); _remoteCandApplied++; }
                    catch (Exception ex) { App.Log("CALL: AddIceCandidate failed: " + ex.Message); }
                }
                else
                {
                    _pendingRemoteCandidates.Add(init);
                    _remoteCandBuffered++;
                }
            }
            Status("Remote candidates: applied=" + _remoteCandApplied + " buffered=" + _remoteCandBuffered + ".");
        }
#endif

        /// <summary>Sends a signalling event without blocking the caller (best-effort).</summary>
        private void SendSignal(string eventType, JsonObject content)
        {
            string roomId = _roomId;
            if (string.IsNullOrEmpty(roomId) || _client == null) return;
            var _ = SendSignalAsync(eventType, content, roomId);
        }

        private async Task SendSignalAsync(string eventType, JsonObject content, string roomId)
        {
            // Retry on rate-limiting (HTTP 429 / M_LIMIT_EXCEEDED) honouring the server's requested
            // back-off. Candidate batches are essential for ICE, so a dropped one can fail the call.
            const int maxAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await _client.SendEventAsync(roomId, eventType, content);
                    return;
                }
                catch (MatrixException mex) when (mex.StatusCode == 429 && attempt < maxAttempts)
                {
                    int delay = mex.RetryAfterMs > 0 ? mex.RetryAfterMs : 1000 * attempt;
                    if (delay > 5000) delay = 5000;
                    App.Log("CALL: " + eventType + " rate-limited; retrying in " + delay + "ms (attempt " + attempt + ").");
                    await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    App.Log("CALL: send " + eventType + " failed: " + ex.Message);
                    return;
                }
            }
        }

        /// <summary>Tears down the active call, optionally notifying the peer with m.call.hangup.</summary>
        private void EndCallLocal(bool notifyRemote)
        {
            if (!_inCall && _callId == null) return;

            if (notifyRemote && !string.IsNullOrEmpty(_callId) && !string.IsNullOrEmpty(_roomId))
            {
                var content = new JsonObject
                {
                    { KCallId, JsonValue.CreateStringValue(_callId) },
                    { KPartyId, JsonValue.CreateStringValue(_localPartyId ?? "") },
                    { KVersion, CallVersionValue }
                };
                SendSignal("m.call.hangup", content);
            }

#if WEBRTC
            try
            {
                if (_pc != null)
                {
                    _pc.OnIceCandidate -= Pc_OnIceCandidate;
                    _pc.OnTrack -= Pc_OnTrack;
                    _pc.OnIceConnectionStateChange -= Pc_OnIceConnectionStateChange;
                    _pc.OnIceGatheringStateChange -= Pc_OnIceGatheringStateChange;
                }
                (_selfAudioTrack as IDisposable)?.Dispose();
                (_peerAudioTrack as IDisposable)?.Dispose();
                (_pc as IDisposable)?.Dispose();
            }
            catch (Exception ex) { App.Log("CALL: teardown error: " + ex.Message); }
            _pc = null;
            _factory = null;
            _selfAudioTrack = null;
            _peerAudioTrack = null;
            _remoteDescriptionSet = false;
            _pendingRemoteCandidates.Clear();
            _pendingOfferSdp = null;
            _isCaller = false;
            _localCandCount = 0;
            _remoteCandApplied = 0;
            _remoteCandBuffered = 0;
            lock (_outgoingLock)
            {
                if (_candidateFlushTimer != null)
                {
                    _candidateFlushTimer.Dispose();
                    _candidateFlushTimer = null;
                }
                _outgoingCandidates.Clear();
            }
#endif
            _inCall = false;
            _callId = null;
            _roomId = null;
            _localPartyId = null;
            _remotePartyId = null;

            Status("Call ended.");
            RunOnUi(() => CallEnded?.Invoke());
        }

        private void RunOnUi(Action action)
        {
            if (_dispatcher == null) { action(); return; }
            if (_dispatcher.HasThreadAccess) { action(); return; }
            var _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        /// <summary>Generates an opaque MSC2746 party_id identifying this client for one call.</summary>
        private static string NewPartyId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        /// <summary>Builds the MSC2746 capabilities object (audio-only; no transfer or DTMF).</summary>
        private static JsonObject BuildCapabilities()
        {
            return new JsonObject
            {
                { "m.call.transferee", JsonValue.CreateBooleanValue(false) },
                { "m.call.dtmf", JsonValue.CreateBooleanValue(false) }
            };
        }

        /// <summary>Renders the "version" field for logs (it may be a string "1" or the number 0).</summary>
        private static string DescribeVersion(JsonObject content)
        {
            if (content == null || !content.ContainsKey(KVersion)) return "-";
            var v = content[KVersion];
            if (v.ValueType == JsonValueType.String) return "\"" + v.GetString() + "\"";
            if (v.ValueType == JsonValueType.Number) return v.GetNumber().ToString();
            return v.ValueType.ToString();
        }

        // ---- small JSON helpers (mirror SyncProcessor's) ----
        private static JsonObject GetObject(JsonObject parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key)) return null;
            return parent[key].ValueType == JsonValueType.Object ? parent.GetNamedObject(key) : null;
        }

        private static JsonArray GetArray(JsonObject parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key)) return null;
            return parent[key].ValueType == JsonValueType.Array ? parent.GetNamedArray(key) : null;
        }
    }
}
