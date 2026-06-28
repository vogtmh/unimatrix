using System;
using System.Collections.Generic;

#if SPIKE_MANAGED_RTC
using System.Net;
using SIPSorcery.Net;
#endif

namespace UniMatrix.Services
{
    /// <summary>
    /// MANAGED-RTC SPIKE (on by default; disable with /p:ManagedRtcSpike=false). A throwaway probe that
    /// points a fully-managed SIPSorcery <c>RTCPeerConnection</c> at the SAME LiveKit signalling
    /// channel we already drive (<see cref="LiveKitSignalClient"/>), to learn whether a pure-C# media
    /// stack can run under .NET Native on the Lumia 930 — and crucially whether RAW RTP packets arrive
    /// in managed code (the seam Org.WebRtc M71 lacks, which would let us SFrame-decrypt E2EE audio).
    ///
    /// This is NOT production code. It deliberately mirrors <see cref="LiveKitMediaSession"/>'s event
    /// wiring but uses SIPSorcery instead of Org.WebRtc. When the spike constant is off, the class is
    /// an inert stub so the normal build is unaffected.
    ///
    /// Gating questions this probe answers on the device (watch the log for "MRTC:" lines):
    ///   Q3  does `new RTCPeerConnection` construct without a TypeLoad/missing-API exception?
    ///   Q4  does ICE open a UDP socket in the appcontainer (we see local candidates / ICE states)?
    ///   Q5  do RAW RTP packets arrive (OnRtpPacketReceived)? -> option C is viable.
    /// </summary>
    internal sealed class ManagedRtcProbe
    {
        private readonly LiveKitSignalClient _signal;
        private bool _closed;

        // LiveKit SignalTarget: which peer connection a candidate/desc belongs to (1 = SUBSCRIBER).
        private const int TargetSubscriber = 1;

        public ManagedRtcProbe(LiveKitSignalClient signal)
        {
            _signal = signal;
            _signal.JoinReceived += OnJoin;
            _signal.OfferReceived += OnOffer;
            _signal.TrickleReceived += OnTrickle;
            App.Log("MRTC: probe armed (managed SIPSorcery path)");
        }

#if SPIKE_MANAGED_RTC
        private RTCPeerConnection _pc;
        private List<LiveKitIceServer> _iceServers = new List<LiveKitIceServer>();
        private bool _remoteSet;
        private readonly List<RTCIceCandidateInit> _pendingCandidates = new List<RTCIceCandidateInit>();
        private readonly object _lock = new object();
        private int _rtpCount;

        private void OnJoin(LiveKitJoinResponse join)
        {
            if (_closed || join == null) return;
            if (join.IceServers != null && join.IceServers.Count > 0) _iceServers = join.IceServers;
            App.Log("MRTC: join captured iceServers=" + _iceServers.Count);
            TryCreatePeerConnection();
        }

        private void TryCreatePeerConnection()
        {
            if (_pc != null) return;
            try
            {
                var config = new RTCConfiguration { iceServers = new List<RTCIceServer>() };
                foreach (var s in _iceServers)
                {
                    if (s == null || s.Urls == null || s.Urls.Count == 0) continue;
                    // SIPSorcery's RTCIceServer.urls is a single (space/comma separated) string.
                    config.iceServers.Add(new RTCIceServer
                    {
                        urls = string.Join(",", s.Urls),
                        username = s.Username ?? "",
                        credential = s.Credential ?? ""
                    });
                }

                _pc = new RTCPeerConnection(config);
                App.Log("MRTC: RTCPeerConnection constructed OK (Q3 pass)");

                // Declare a recv-only Opus audio track so our answer carries an m=audio section.
                var opus = new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 111, "opus", 48000, 2,
                    "minptime=10;useinbandfec=1");
                var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false,
                    new List<SDPAudioVideoMediaFormat> { opus }, MediaStreamStatusEnum.RecvOnly);
                _pc.addTrack(audioTrack);

                _pc.onicecandidate += cand =>
                {
                    if (cand == null) return;
                    try { var _ = _signal.SendTrickleAsync(cand.toJSON(), TargetSubscriber); }
                    catch (Exception ex) { App.Log("MRTC: send candidate failed: " + ex.Message); }
                };
                _pc.oniceconnectionstatechange += st => App.Log("MRTC: ICE state " + st);
                _pc.onconnectionstatechange += st => App.Log("MRTC: PC state " + st);
                _pc.OnRtpPacketReceived += (IPEndPoint rep, SDPMediaTypesEnum media, RTPPacket pkt) =>
                {
                    _rtpCount++;
                    if (_rtpCount == 1 || _rtpCount % 50 == 0)
                        App.Log("MRTC: RTP #" + _rtpCount + " media=" + media +
                                " pt=" + pkt.Header.PayloadType + " len=" + (pkt.Payload != null ? pkt.Payload.Length : 0) +
                                " (Q5 pass — raw RTP in managed code)");
                };
            }
            catch (Exception ex)
            {
                App.Log("MRTC: RTCPeerConnection construction FAILED (Q3 fail): " + ex);
            }
        }

        private void OnOffer(LiveKitSessionDescription offer)
        {
            if (_closed || offer == null) return;
            TryCreatePeerConnection();
            if (_pc == null) return;
            try
            {
                var remote = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.Sdp };
                var setResult = _pc.setRemoteDescription(remote);
                App.Log("MRTC: setRemoteDescription -> " + setResult);
                _remoteSet = true;
                FlushPendingCandidates();

                var answer = _pc.createAnswer(null);
                var _ = _pc.setLocalDescription(answer);
                App.Log("MRTC: answer created sdpLen=" + (answer != null && answer.sdp != null ? answer.sdp.Length : 0));
                if (answer != null) { var __ = _signal.SendAnswerAsync(answer.sdp); }
            }
            catch (Exception ex)
            {
                App.Log("MRTC: offer/answer handling FAILED: " + ex);
            }
        }

        private void OnTrickle(LiveKitTrickle trickle)
        {
            if (_closed || trickle == null || trickle.Target != TargetSubscriber) return;
            RTCIceCandidateInit init = ParseCandidate(trickle.CandidateInit);
            if (init == null) return;
            if (_remoteSet && _pc != null)
            {
                try { _pc.addIceCandidate(init); }
                catch (Exception ex) { App.Log("MRTC: addIceCandidate failed: " + ex.Message); }
            }
            else
            {
                lock (_lock) _pendingCandidates.Add(init);
            }
        }

        private void FlushPendingCandidates()
        {
            if (_pc == null) return;
            List<RTCIceCandidateInit> pend;
            lock (_lock)
            {
                if (_pendingCandidates.Count == 0) return;
                pend = new List<RTCIceCandidateInit>(_pendingCandidates);
                _pendingCandidates.Clear();
            }
            foreach (var init in pend)
            {
                try { _pc.addIceCandidate(init); }
                catch (Exception ex) { App.Log("MRTC: addIceCandidate (flush) failed: " + ex.Message); }
            }
        }

        private static RTCIceCandidateInit ParseCandidate(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var o = Windows.Data.Json.JsonObject.Parse(json);
                string cand = o.ContainsKey("candidate") ? o.GetNamedString("candidate") : "";
                if (string.IsNullOrEmpty(cand)) return null;
                string sdpMid = o.ContainsKey("sdpMid") && o["sdpMid"].ValueType == Windows.Data.Json.JsonValueType.String
                    ? o.GetNamedString("sdpMid") : "0";
                ushort mline = 0;
                if (o.ContainsKey("sdpMLineIndex") && o["sdpMLineIndex"].ValueType == Windows.Data.Json.JsonValueType.Number)
                    mline = (ushort)o.GetNamedNumber("sdpMLineIndex");
                return new RTCIceCandidateInit { candidate = cand, sdpMid = sdpMid, sdpMLineIndex = mline };
            }
            catch (Exception ex)
            {
                App.Log("MRTC: candidate parse failed: " + ex.Message);
                return null;
            }
        }
#else
        private void OnJoin(LiveKitJoinResponse join) { }
        private void OnOffer(LiveKitSessionDescription offer) { }
        private void OnTrickle(LiveKitTrickle trickle) { }
#endif

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            if (_signal != null)
            {
                _signal.JoinReceived -= OnJoin;
                _signal.OfferReceived -= OnOffer;
                _signal.TrickleReceived -= OnTrickle;
            }
#if SPIKE_MANAGED_RTC
            try { if (_pc != null) { _pc.close(); _pc = null; } } catch { }
#endif
        }
    }
}
