using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;

#if WEBRTC
using Org.WebRtc;
#endif

namespace UniMatrix.Services
{
    /// <summary>
    /// The WebRTC media half of a MatrixRTC/LiveKit call. The signalling (WebSocket + protobuf) lives
    /// in <see cref="LiveKitSignalClient"/>; this class owns the Org.WebRtc peer connection(s) and
    /// bridges them to that signalling channel.
    ///
    /// LiveKit uses a subscriber-primary model: the SFU sends us an OFFER on the *subscriber* peer
    /// connection (carrying the other participants' tracks). We answer it and then receive their
    /// audio. Publishing our own microphone (the *publisher* peer connection) is a later milestone;
    /// this first cut is receive-only, so the user can hear the caller.
    ///
    /// All Org.WebRtc usage is compiled only when WEBRTC is defined (ARM configs that reference
    /// libs\webrtc\ARM\Org.WebRtc.winmd). On other platforms the class still exists (so the app
    /// compiles) but does nothing.
    /// </summary>
    internal sealed class LiveKitMediaSession
    {
        private readonly CoreDispatcher _dispatcher;
        private readonly LiveKitSignalClient _signal;
        private List<LiveKitIceServer> _iceServers = new List<LiveKitIceServer>();
        private bool _closed;

        // LiveKit SignalTarget enum values (which peer connection a message/candidate belongs to).
        private const int TargetPublisher = 0;
        private const int TargetSubscriber = 1;

        /// <summary>Raised with human-readable status updates (on the UI thread).</summary>
        public event Action<string> StatusChanged;

        public LiveKitMediaSession(CoreDispatcher dispatcher, LiveKitSignalClient signal)
        {
            _dispatcher = dispatcher;
            _signal = signal;

#if WEBRTC
            // Initialize the native WebRTC library once (shared with the legacy CallService). Must run
            // on the UI thread; this constructor is invoked on it from the accept/join flow.
            try { CallService.EnsureWebRtcLibrary(_dispatcher); }
            catch (Exception ex) { App.Log("LKM: WebRTC library init failed: " + ex.Message); }
#endif

            // Capture the SFU's ICE servers from the JoinResponse. This handler runs synchronously on
            // the signalling receive thread, so the servers are in place before the subscriber OFFER
            // (a later WebSocket frame) is processed.
            _signal.JoinReceived += OnJoin;
            _signal.OfferReceived += OnSubscriberOffer;
            _signal.TrickleReceived += OnTrickle;
        }

        private void OnJoin(LiveKitJoinResponse join)
        {
            if (join != null && join.IceServers != null && join.IceServers.Count > 0)
                _iceServers = join.IceServers;
        }

        private void Status(string s)
        {
            App.Log("LKM: " + s);
            var handler = StatusChanged;
            if (handler == null) return;
            if (_dispatcher == null || _dispatcher.HasThreadAccess) { handler(s); return; }
            var _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => handler(s));
        }

#if WEBRTC
        private WebRtcFactory _factory;
        private RTCPeerConnection _subPc;     // subscriber PC (receives remote tracks)
        private IMediaStreamTrack _remoteAudioTrack;
        private bool _subRemoteSet;
        private readonly List<RTCIceCandidateInit> _pendingSubCandidates = new List<RTCIceCandidateInit>();
        private readonly object _candLock = new object();

        /// <summary>
        /// Handles the SFU's subscriber OFFER: create the subscriber peer connection (Unified Plan),
        /// apply the remote offer, create our answer and send it back. Remote audio then arrives via
        /// OnTrack. Runs the native WebRTC work on a background thread (the native calls block until
        /// the library dispatcher pumps, so the UI thread would deadlock).
        /// </summary>
        private void OnSubscriberOffer(LiveKitSessionDescription offer)
        {
            if (_closed || offer == null) return;
            var _ = Task.Run(async () =>
            {
                try
                {
                    EnsureFactory();
                    EnsureSubscriberPc();

                    var remoteInit = new RTCSessionDescriptionInit { Sdp = offer.Sdp, Type = RTCSdpType.Offer };
                    await _subPc.SetRemoteDescription(new RTCSessionDescription(remoteInit));
                    _subRemoteSet = true;
                    FlushPendingSubCandidates();

                    var answer = await _subPc.CreateAnswer(new RTCAnswerOptions());
                    await _subPc.SetLocalDescription(answer);
                    await _signal.SendAnswerAsync(answer.Sdp);
                    Status("subscriber answer sent (sdpLen=" + (answer.Sdp != null ? answer.Sdp.Length : 0) + ")");
                }
                catch (Exception ex)
                {
                    App.Log("LKM: subscriber offer handling failed: " + ex.Message);
                }
            });
        }

        private void OnTrickle(LiveKitTrickle trickle)
        {
            if (_closed || trickle == null) return;
            // We currently only run a subscriber PC; ignore publisher-targeted candidates for now.
            if (trickle.Target != TargetSubscriber) return;

            RTCIceCandidateInit init = ParseCandidateInit(trickle.CandidateInit);
            if (init == null) return;

            if (_subRemoteSet && _subPc != null)
            {
                try { var _ = _subPc.AddIceCandidate(new RTCIceCandidate(init)); }
                catch (Exception ex) { App.Log("LKM: AddIceCandidate failed: " + ex.Message); }
            }
            else
            {
                lock (_candLock) _pendingSubCandidates.Add(init);
            }
        }

        private void FlushPendingSubCandidates()
        {
            if (_subPc == null) return;
            List<RTCIceCandidateInit> pending;
            lock (_candLock)
            {
                if (_pendingSubCandidates.Count == 0) return;
                pending = new List<RTCIceCandidateInit>(_pendingSubCandidates);
                _pendingSubCandidates.Clear();
            }
            foreach (var init in pending)
            {
                try { var _ = _subPc.AddIceCandidate(new RTCIceCandidate(init)); }
                catch (Exception ex) { App.Log("LKM: AddIceCandidate (flush) failed: " + ex.Message); }
            }
        }

        private void EnsureFactory()
        {
            if (_factory != null) return;
            string captureId = "";
            string renderId = "";
            try
            {
                captureId = Windows.Media.Devices.MediaDevice.GetDefaultAudioCaptureId(
                    Windows.Media.Devices.AudioDeviceRole.Communications) ?? "";
                renderId = Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(
                    Windows.Media.Devices.AudioDeviceRole.Communications) ?? "";
            }
            catch { /* fall back to default device */ }

            _factory = new WebRtcFactory(new WebRtcFactoryConfiguration
            {
                AudioCaptureDeviceId = captureId,
                AudioRenderDeviceId = renderId
            });
        }

        private void EnsureSubscriberPc()
        {
            if (_subPc != null) return;

            var iceServers = new List<RTCIceServer>();
            foreach (var s in _iceServers)
            {
                if (s == null || s.Urls == null || s.Urls.Count == 0) continue;
                iceServers.Add(new RTCIceServer
                {
                    Urls = new List<string>(s.Urls),
                    Username = s.Username ?? "",
                    Credential = s.Credential ?? ""
                });
            }
            if (iceServers.Count == 0)
                iceServers.Add(new RTCIceServer { Urls = new List<string> { "stun:stun.l.google.com:19302" } });

            var config = new RTCConfiguration
            {
                Factory = _factory,
                BundlePolicy = RTCBundlePolicy.MaxBundle,   // LiveKit bundles all media on one transport
                IceTransportPolicy = RTCIceTransportPolicy.All,
                IceServers = iceServers,
                SdpSemantics = RTCSdpSemantics.UnifiedPlan   // LiveKit requires Unified Plan
            };

            _subPc = new RTCPeerConnection(config);
            _subPc.OnIceCandidate += SubPc_OnIceCandidate;
            _subPc.OnTrack += SubPc_OnTrack;
            _subPc.OnIceConnectionStateChange += SubPc_OnIceConnectionStateChange;
            Status("subscriber peer connection created (iceServers=" + iceServers.Count + ")");
        }

        private void SubPc_OnIceCandidate(IRTCPeerConnectionIceEvent evt)
        {
            var cand = evt?.Candidate;
            if (cand == null) return; // end-of-candidates
            string json = BuildCandidateInit(cand);
            var _ = _signal.SendTrickleAsync(json, TargetSubscriber);
        }

        private void SubPc_OnTrack(IRTCTrackEvent evt)
        {
            if (evt?.Track != null && evt.Track.Kind == "audio")
            {
                // Remote audio plays out through the default render device automatically once the
                // track arrives; just keep a reference so it isn't garbage-collected.
                _remoteAudioTrack = evt.Track;
                Status("remote audio track received");
            }
        }

        private void SubPc_OnIceConnectionStateChange()
        {
            var pc = _subPc;
            if (pc == null) return;
            Status("subscriber ICE: " + pc.IceConnectionState);
        }

        private static RTCIceCandidateInit ParseCandidateInit(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                JsonObject o = JsonObject.Parse(json);
                string cand = o.ContainsKey("candidate") ? o.GetNamedString("candidate") : "";
                if (string.IsNullOrEmpty(cand)) return null;
                string sdpMid = o.ContainsKey("sdpMid") && o["sdpMid"].ValueType == JsonValueType.String
                    ? o.GetNamedString("sdpMid") : "";
                ushort mline = 0;
                if (o.ContainsKey("sdpMLineIndex") && o["sdpMLineIndex"].ValueType == JsonValueType.Number)
                    mline = (ushort)o.GetNamedNumber("sdpMLineIndex");
                return new RTCIceCandidateInit { Candidate = cand, SdpMid = sdpMid, SdpMLineIndex = mline };
            }
            catch (Exception ex)
            {
                App.Log("LKM: candidate parse failed: " + ex.Message);
                return null;
            }
        }

        private static string BuildCandidateInit(IRTCIceCandidate cand)
        {
            var o = new JsonObject
            {
                { "candidate", JsonValue.CreateStringValue(cand.Candidate ?? "") },
                { "sdpMid", JsonValue.CreateStringValue(cand.SdpMid ?? "") },
                { "sdpMLineIndex", JsonValue.CreateNumberValue(cand.SdpMLineIndex ?? 0) }
            };
            return o.Stringify();
        }
#endif

        /// <summary>Tears down the peer connection(s) and unsubscribes from signalling events.</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            if (_signal != null)
            {
                _signal.JoinReceived -= OnJoin;
                _signal.OfferReceived -= OnSubscriberOffer;
                _signal.TrickleReceived -= OnTrickle;
            }
#if WEBRTC
            try
            {
                (_remoteAudioTrack as IDisposable)?.Dispose();
                (_subPc as IDisposable)?.Dispose();
            }
            catch { }
            _remoteAudioTrack = null;
            _subPc = null;
            _factory = null;
#endif
        }

#if !WEBRTC
        // Referenced by the constructor's event subscriptions on non-WEBRTC builds so they resolve.
        private void OnSubscriberOffer(LiveKitSessionDescription offer) { }
        private void OnTrickle(LiveKitTrickle trickle) { }
#endif
    }
}
