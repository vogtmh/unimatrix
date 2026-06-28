using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace UniMatrix.Services
{
    // ---- Decoded LiveKit signalling message models (only the fields we use) ----

    internal sealed class LiveKitIceServer
    {
        public List<string> Urls = new List<string>();
        public string Username = "";
        public string Credential = "";
    }

    internal sealed class LiveKitJoinResponse
    {
        public List<LiveKitIceServer> IceServers = new List<LiveKitIceServer>();
        public bool SubscriberPrimary;
        public string ParticipantSid = "";
        public string ParticipantIdentity = "";
        public int OtherParticipantCount;
        public string ServerVersion = "";
    }

    internal sealed class LiveKitSessionDescription
    {
        public string Type = "";   // "offer" / "answer"
        public string Sdp = "";
        public uint Id;
    }

    internal sealed class LiveKitTrickle
    {
        public string CandidateInit = "";   // JSON RTCIceCandidateInit
        public int Target;                   // 0 = PUBLISHER, 1 = SUBSCRIBER
        public bool Final;
    }

    /// <summary>
    /// LiveKit signalling client: a WebSocket to the SFU's /rtc endpoint that exchanges binary
    /// protobuf SignalRequest (us -> server) / SignalResponse (server -> us) messages. This handles
    /// ONLY signalling (connect, decode, keepalive, and raise events); the WebRTC media (peer
    /// connections, tracks) is wired on top of these events separately so this layer stays free of
    /// the Org.WebRtc dependency and compiles/validates everywhere.
    ///
    /// LiveKit field numbers and the connect URL come from the livekit/protocol definitions
    /// (SignalRequest/SignalResponse oneof tags). Wire = binary protobuf frames.
    /// </summary>
    internal sealed class LiveKitSignalClient
    {
        private MessageWebSocket _ws;
        private DataWriter _writer;
        private bool _closed;

        // SignalResponse oneof field numbers (server -> client).
        private const int RespJoin = 1;
        private const int RespAnswer = 2;
        private const int RespOffer = 3;
        private const int RespTrickle = 4;
        private const int RespLeave = 8;
        private const int RespPong = 18;     // int64 (deprecated)
        private const int RespPongResp = 20; // Pong message

        // SignalRequest oneof field numbers (client -> server).
        private const int ReqOffer = 1;
        private const int ReqAnswer = 2;
        private const int ReqTrickle = 3;
        private const int ReqAddTrack = 4;
        private const int ReqLeave = 8;
        private const int ReqPing = 14;      // int64 (deprecated)
        private const int ReqPingReq = 16;   // Ping message

        // ---- Events (raised on the WS receive thread; handlers must marshal to UI as needed) ----
        public event Action<LiveKitJoinResponse> JoinReceived;
        public event Action<LiveKitSessionDescription> OfferReceived;   // subscriber offer from server
        public event Action<LiveKitSessionDescription> AnswerReceived;  // answer to our publisher offer
        public event Action<LiveKitTrickle> TrickleReceived;
        public event Action<string> Closed;

        public bool IsConnected => _ws != null && !_closed;

        /// <summary>
        /// Connects to the SFU signalling endpoint. <paramref name="sfuUrl"/> is the wss base from the
        /// SFU credentials (e.g. wss://matrix-org.livekit.cloud); <paramref name="jwt"/> is the LiveKit
        /// access token. Returns true once the socket is open (the JoinReceived event fires shortly after).
        /// </summary>
        public async Task<bool> ConnectAsync(string sfuUrl, string jwt)
        {
            try
            {
                string baseUrl = (sfuUrl ?? "").TrimEnd('/');
                string url = baseUrl + "/rtc?access_token=" + Uri.EscapeDataString(jwt ?? "") +
                             "&auto_subscribe=1&sdk=js&protocol=15&version=0.0.1";

                _ws = new MessageWebSocket();
                _ws.Control.MessageType = SocketMessageType.Binary;
                _ws.MessageReceived += OnMessageReceived;
                _ws.Closed += OnSocketClosed;

                await _ws.ConnectAsync(new Uri(url));
                _writer = new DataWriter(_ws.OutputStream);
                App.Log("LK: signalling socket connected to " + baseUrl + "/rtc");
                return true;
            }
            catch (Exception ex)
            {
                App.Log("LK: connect failed: " + ex.Message);
                Close("connect failed");
                return false;
            }
        }

        private void OnSocketClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            App.Log("LK: socket closed code=" + args.Code + " reason=" + args.Reason);
            Close("socket closed (" + args.Code + ")");
        }

        private void OnMessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                byte[] data;
                using (DataReader reader = args.GetDataReader())
                {
                    uint len = reader.UnconsumedBufferLength;
                    data = new byte[len];
                    reader.ReadBytes(data);
                }
                HandleSignalResponse(data);
            }
            catch (Exception ex)
            {
                App.Log("LK: message read failed: " + ex.Message);
            }
        }

        // ---- Decode SignalResponse (server -> client) ----

        private void HandleSignalResponse(byte[] data)
        {
            var r = new ProtoReader(data);
            while (r.ReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case RespJoin when wire == 2:
                        var join = DecodeJoinResponse(r.ReadMessage());
                        App.Log("LK: JOIN sid=" + join.ParticipantSid + " identity=" + join.ParticipantIdentity +
                                " others=" + join.OtherParticipantCount + " subPrimary=" + join.SubscriberPrimary +
                                " iceServers=" + join.IceServers.Count + " server=" + join.ServerVersion);
                        JoinReceived?.Invoke(join);
                        break;

                    case RespOffer when wire == 2:
                        var offer = DecodeSessionDescription(r.ReadMessage());
                        App.Log("LK: <- OFFER (subscriber) sdpLen=" + offer.Sdp.Length);
                        OfferReceived?.Invoke(offer);
                        break;

                    case RespAnswer when wire == 2:
                        var answer = DecodeSessionDescription(r.ReadMessage());
                        App.Log("LK: <- ANSWER (publisher) sdpLen=" + answer.Sdp.Length);
                        AnswerReceived?.Invoke(answer);
                        break;

                    case RespTrickle when wire == 2:
                        var trickle = DecodeTrickle(r.ReadMessage());
                        TrickleReceived?.Invoke(trickle);
                        break;

                    case RespLeave when wire == 2:
                        r.ReadMessage(); // contents not needed
                        App.Log("LK: <- LEAVE");
                        Close("server leave");
                        break;

                    case RespPongResp when wire == 2:
                        r.ReadMessage(); // pong; nothing to do, keepalive satisfied
                        break;

                    case RespPong when wire == 0:
                        r.ReadVarint(); // deprecated int64 pong
                        break;

                    default:
                        r.SkipField(wire);
                        break;
                }
            }
        }

        private static LiveKitJoinResponse DecodeJoinResponse(ProtoReader r)
        {
            var join = new LiveKitJoinResponse();
            while (r.ReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 2 when wire == 2: // participant (ParticipantInfo)
                        DecodeParticipantInto(r.ReadMessage(), join);
                        break;
                    case 3 when wire == 2: // other_participants (repeated)
                        r.ReadMessage();
                        join.OtherParticipantCount++;
                        break;
                    case 4 when wire == 2: // server_version
                        join.ServerVersion = r.ReadString();
                        break;
                    case 5 when wire == 2: // ice_servers (repeated ICEServer)
                        join.IceServers.Add(DecodeIceServer(r.ReadMessage()));
                        break;
                    case 6 when wire == 0: // subscriber_primary
                        join.SubscriberPrimary = r.ReadBool();
                        break;
                    default:
                        r.SkipField(wire);
                        break;
                }
            }
            return join;
        }

        private static void DecodeParticipantInto(ProtoReader r, LiveKitJoinResponse join)
        {
            while (r.ReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1 when wire == 2: join.ParticipantSid = r.ReadString(); break;      // sid
                    case 2 when wire == 2: join.ParticipantIdentity = r.ReadString(); break;  // identity
                    default: r.SkipField(wire); break;
                }
            }
        }

        private static LiveKitIceServer DecodeIceServer(ProtoReader r)
        {
            var s = new LiveKitIceServer();
            while (r.ReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1 when wire == 2: s.Urls.Add(r.ReadString()); break; // urls (repeated)
                    case 2 when wire == 2: s.Username = r.ReadString(); break;
                    case 3 when wire == 2: s.Credential = r.ReadString(); break;
                    default: r.SkipField(wire); break;
                }
            }
            return s;
        }

        private static LiveKitSessionDescription DecodeSessionDescription(ProtoReader r)
        {
            var sd = new LiveKitSessionDescription();
            while (r.ReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1 when wire == 2: sd.Type = r.ReadString(); break;
                    case 2 when wire == 2: sd.Sdp = r.ReadString(); break;
                    case 3 when wire == 0: sd.Id = r.ReadUInt32(); break;
                    default: r.SkipField(wire); break;
                }
            }
            return sd;
        }

        private static LiveKitTrickle DecodeTrickle(ProtoReader r)
        {
            var t = new LiveKitTrickle();
            while (r.ReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1 when wire == 2: t.CandidateInit = r.ReadString(); break;
                    case 2 when wire == 0: t.Target = r.ReadInt32(); break;
                    case 3 when wire == 0: t.Final = r.ReadBool(); break;
                    default: r.SkipField(wire); break;
                }
            }
            return t;
        }

        // ---- Encode + send SignalRequest (client -> server) ----

        /// <summary>Sends our answer to the server's subscriber offer.</summary>
        public Task SendAnswerAsync(string sdp) => SendSessionDescriptionAsync(ReqAnswer, "answer", sdp);

        /// <summary>Sends our publisher offer to the server.</summary>
        public Task SendOfferAsync(string sdp) => SendSessionDescriptionAsync(ReqOffer, "offer", sdp);

        private Task SendSessionDescriptionAsync(int reqField, string type, string sdp)
        {
            var sd = new ProtoWriter();
            sd.WriteString(1, type);
            sd.WriteString(2, sdp);
            var req = new ProtoWriter();
            req.WriteMessage(reqField, sd.ToArray());
            App.Log("LK: -> " + type + " sdpLen=" + (sdp?.Length ?? 0));
            return SendAsync(req.ToArray());
        }

        /// <summary>Sends a local ICE candidate to the server for the given target PC (0=pub, 1=sub).</summary>
        public Task SendTrickleAsync(string candidateInitJson, int target)
        {
            var t = new ProtoWriter();
            t.WriteString(1, candidateInitJson);
            t.WriteEnum(2, target);
            var req = new ProtoWriter();
            req.WriteMessage(ReqTrickle, t.ToArray());
            return SendAsync(req.ToArray());
        }

        /// <summary>Announces a local track (audio mic) so the server expects it on our publisher PC.</summary>
        public Task SendAddTrackAsync(string cid, string name, int trackType, int trackSource)
        {
            var at = new ProtoWriter();
            at.WriteString(1, cid);            // cid
            at.WriteString(2, name);           // name
            at.WriteEnum(3, trackType);        // type (AUDIO=0)
            at.WriteEnum(8, trackSource);      // source (MICROPHONE=2)
            var req = new ProtoWriter();
            req.WriteMessage(ReqAddTrack, at.ToArray());
            App.Log("LK: -> add_track cid=" + cid + " type=" + trackType + " source=" + trackSource);
            return SendAsync(req.ToArray());
        }

        /// <summary>Tells the server we're leaving the session.</summary>
        public Task SendLeaveAsync()
        {
            var leave = new ProtoWriter(); // empty LeaveRequest is fine
            var req = new ProtoWriter();
            req.WriteMessage(ReqLeave, leave.ToArray());
            return SendAsync(req.ToArray());
        }

        private async Task SendAsync(byte[] bytes)
        {
            if (_writer == null || _closed) return;
            try
            {
                _writer.WriteBytes(bytes);
                await _writer.StoreAsync();
            }
            catch (Exception ex)
            {
                App.Log("LK: send failed: " + ex.Message);
            }
        }

        public void Close(string reason)
        {
            if (_closed) return;
            _closed = true;
            try { _writer?.DetachStream(); } catch { }
            _writer = null;
            try { _ws?.Close(1000, reason ?? "bye"); } catch { }
            _ws = null;
            Closed?.Invoke(reason);
        }
    }
}
