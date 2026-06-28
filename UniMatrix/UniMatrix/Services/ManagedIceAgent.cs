using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;

namespace UniMatrix.Services
{
    /// <summary>
    /// MANAGED-RTC SPIKE — Stage 2 (dependency-free). Builds on Stage 1 (StunProbe proved the
    /// appcontainer can do UDP + STUN to the SFU) by standing up a minimal but REAL ICE agent in
    /// pure managed code, to answer the next make-or-break question: can we actually ICE-CONNECT to
    /// the LiveKit SFU — exchange authenticated STUN connectivity checks and establish a candidate
    /// pair — without any native/NuGet WebRTC stack?
    ///
    /// It reuses our working signalling (<see cref="LiveKitSignalClient"/>): on the subscriber OFFER
    /// it parses the SFU's ICE ufrag/pwd, generates our own, sends a valid recvonly SDP answer with a
    /// DUMMY DTLS fingerprint (ICE connectivity happens before DTLS, so a real cert isn't needed until
    /// Stage 3), gathers a server-reflexive candidate via STUN, trickles it, and then runs RFC 5389
    /// short-term-credential connectivity checks (USERNAME + MESSAGE-INTEGRITY/HMAC-SHA1 + FINGERPRINT
    /// /CRC32) against the SFU's candidates while answering its inbound checks.
    ///
    /// Uses ONLY Windows.Networking.Sockets + Windows.Security.Cryptography, so it compiles and runs
    /// on the legacy uap10.0.14393 surface (no netstandard2.0 dependency). Logs "ICE:" lines. A
    /// "connectivity check SUCCESS" proves the managed path is viable through ICE -> green-light Stage
    /// 3 (vendor BouncyCastle DTLS + SRTP).
    /// </summary>
    internal sealed class ManagedIceAgent
    {
        private readonly LiveKitSignalClient _signal;
        private readonly object _lock = new object();

        private const uint MagicCookie = 0x2112A442;
        private static readonly byte[] MagicCookieBytes = { 0x21, 0x12, 0xA4, 0x42 };

        // STUN attribute types.
        private const int AttrUsername = 0x0006;
        private const int AttrMessageIntegrity = 0x0008;
        private const int AttrXorMappedAddress = 0x0020;
        private const int AttrMappedAddress = 0x0001;
        private const int AttrPriority = 0x0024;
        private const int AttrIceControlled = 0x8029;
        private const int AttrFingerprint = 0x8028;

        // STUN message types.
        private const int MsgBindingRequest = 0x0001;
        private const int MsgBindingSuccess = 0x0101;
        private const int MsgBindingError = 0x0111;

        // LiveKit SignalTarget (which PC a candidate belongs to): 1 = SUBSCRIBER.
        private const int TargetSubscriber = 1;

        // ICE credentials (ours + the SFU's, parsed from the offer).
        private string _localUfrag;
        private string _localPwd;
        private string _remoteUfrag;
        private string _remotePwd;
        private string _mid = "0";
        private byte[] _tieBreaker;

        private DatagramSocket _socket;
        private string _localPort;
        private bool _closed;
        private bool _connected;

        // First usable STUN/TURN host from the JoinResponse (for srflx gathering).
        private string _stunHost;
        private int _stunPort = 3478;
        private byte[] _gatherTxId;

        // Remote (SFU) candidates to run connectivity checks against.
        private readonly List<RemoteCandidate> _remoteCandidates = new List<RemoteCandidate>();
        // Transaction ids of checks we've sent (so we can match success responses).
        private readonly HashSet<string> _checkTxIds = new HashSet<string>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, IOutputStream> _outStreams =
            new Dictionary<string, IOutputStream>();

        private sealed class RemoteCandidate
        {
            public string Host;
            public int Port;
            public string Key { get { return Host + ":" + Port; } }
        }

        public ManagedIceAgent(LiveKitSignalClient signal)
        {
            _signal = signal;
            _signal.JoinReceived += OnJoin;
            _signal.OfferReceived += OnOffer;
            _signal.TrickleReceived += OnTrickle;
            App.Log("ICE: managed agent armed (Stage 2)");
        }

        private void OnJoin(LiveKitJoinResponse join)
        {
            if (_closed || join == null || join.IceServers == null) return;
            foreach (var s in join.IceServers)
            {
                if (s == null || s.Urls == null) continue;
                foreach (var url in s.Urls)
                {
                    string host; int port;
                    if (TryParseStunUrl(url, out host, out port))
                    {
                        _stunHost = host;
                        _stunPort = port;
                        App.Log("ICE: STUN host for gathering = " + host + ":" + port);
                        return;
                    }
                }
            }
        }

        private void OnOffer(LiveKitSessionDescription offer)
        {
            if (_closed || offer == null || string.IsNullOrEmpty(offer.Sdp)) return;
            var _ = Task.Run(async () =>
            {
                try { await HandleOfferAsync(offer.Sdp); }
                catch (Exception ex) { App.Log("ICE: offer handling failed: " + ex); }
            });
        }

        private async Task HandleOfferAsync(string sdp)
        {
            ParseOffer(sdp);
            if (string.IsNullOrEmpty(_remoteUfrag) || string.IsNullOrEmpty(_remotePwd))
            {
                App.Log("ICE: offer missing ice-ufrag/pwd -> cannot run checks");
                return;
            }

            _localUfrag = RandIceString(8);
            _localPwd = RandIceString(24);
            _tieBreaker = RandomBytes(8);
            App.Log("ICE: local ufrag=" + _localUfrag + " (remote ufrag=" + _remoteUfrag + ")");

            await OpenSocketAsync();
            if (_socket == null) return;

            // Send the answer carrying our ICE credentials + a dummy DTLS fingerprint so the SFU
            // starts ICE toward us. (Real DTLS is Stage 3.)
            string answer = BuildAnswerSdp();
            await _signal.SendAnswerAsync(answer);
            App.Log("ICE: answer sent (sdpLen=" + answer.Length + ", dummy fingerprint)");

            // Gather a server-reflexive candidate (same socket/port the checks will use).
            await SendGatheringRequestAsync();

            // Kick off the connectivity-check retransmission loop.
            var _ = Task.Run(() => CheckLoopAsync());
        }

        private async Task OpenSocketAsync()
        {
            try
            {
                _socket = new DatagramSocket();
                _socket.MessageReceived += OnDatagram;
                await _socket.BindServiceNameAsync(""); // ephemeral local port
                _localPort = _socket.Information != null ? _socket.Information.LocalPort : null;
                App.Log("ICE: UDP socket bound localPort=" + (_localPort ?? "?"));
            }
            catch (Exception ex)
            {
                App.Log("ICE: socket bind failed: " + ex.Message);
                _socket = null;
            }
        }

        // ---- SDP ----

        private void ParseOffer(string sdp)
        {
            foreach (var raw in sdp.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("a=ice-ufrag:")) _remoteUfrag = line.Substring("a=ice-ufrag:".Length).Trim();
                else if (line.StartsWith("a=ice-pwd:")) _remotePwd = line.Substring("a=ice-pwd:".Length).Trim();
                else if (line.StartsWith("a=mid:")) _mid = line.Substring("a=mid:".Length).Trim();
                else if (line.StartsWith("a=candidate:")) AddRemoteCandidate("candidate:" + line.Substring("a=candidate:".Length).Trim());
            }
        }

        private string BuildAnswerSdp()
        {
            // A dummy sha-256 fingerprint (32 bytes). ICE connectivity does not verify it; DTLS (Stage
            // 3) will replace this with our real certificate fingerprint.
            byte[] fp = RandomBytes(32);
            var fpHex = new StringBuilder();
            for (int i = 0; i < fp.Length; i++)
            {
                if (i > 0) fpHex.Append(':');
                fpHex.Append(fp[i].ToString("X2"));
            }

            var sb = new StringBuilder();
            sb.Append("v=0\r\n");
            sb.Append("o=- 4611731400430051336 2 IN IP4 127.0.0.1\r\n");
            sb.Append("s=-\r\n");
            sb.Append("t=0 0\r\n");
            sb.Append("a=group:BUNDLE ").Append(_mid).Append("\r\n");
            sb.Append("a=msid-semantic: WMS\r\n");
            sb.Append("m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n");
            sb.Append("c=IN IP4 0.0.0.0\r\n");
            sb.Append("a=rtcp:9 IN IP4 0.0.0.0\r\n");
            sb.Append("a=ice-ufrag:").Append(_localUfrag).Append("\r\n");
            sb.Append("a=ice-pwd:").Append(_localPwd).Append("\r\n");
            sb.Append("a=ice-options:trickle\r\n");
            sb.Append("a=fingerprint:sha-256 ").Append(fpHex).Append("\r\n");
            sb.Append("a=setup:active\r\n");
            sb.Append("a=mid:").Append(_mid).Append("\r\n");
            sb.Append("a=recvonly\r\n");
            sb.Append("a=rtcp-mux\r\n");
            sb.Append("a=rtpmap:111 opus/48000/2\r\n");
            sb.Append("a=fmtp:111 minptime=10;useinbandfec=1\r\n");
            return sb.ToString();
        }

        private void OnTrickle(LiveKitTrickle trickle)
        {
            if (_closed || trickle == null || trickle.Target != TargetSubscriber) return;
            string cand = ExtractCandidateString(trickle.CandidateInit);
            if (!string.IsNullOrEmpty(cand)) AddRemoteCandidate(cand);
        }

        private static string ExtractCandidateString(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var o = Windows.Data.Json.JsonObject.Parse(json);
                return o.ContainsKey("candidate") ? o.GetNamedString("candidate") : null;
            }
            catch { return null; }
        }

        private void AddRemoteCandidate(string candidate)
        {
            // "candidate:<foundation> <comp> <transport> <priority> <ip> <port> typ <type> ..."
            try
            {
                string body = candidate.StartsWith("candidate:") ? candidate.Substring("candidate:".Length) : candidate;
                string[] t = body.Split(' ');
                if (t.Length < 6) return;
                if (!t[2].Equals("udp", StringComparison.OrdinalIgnoreCase)) return; // UDP only
                string ip = t[4];
                int port;
                if (!int.TryParse(t[5], out port)) return;
                if (ip.Contains(":")) return; // skip IPv6 for this spike

                var rc = new RemoteCandidate { Host = ip, Port = port };
                bool added = false;
                lock (_lock)
                {
                    bool exists = false;
                    foreach (var c in _remoteCandidates) if (c.Key == rc.Key) { exists = true; break; }
                    if (!exists) { _remoteCandidates.Add(rc); added = true; }
                }
                if (added)
                {
                    App.Log("ICE: remote candidate " + rc.Key);
                    var _ = SendCheckAsync(rc); // immediate check
                }
            }
            catch (Exception ex) { App.Log("ICE: candidate parse failed: " + ex.Message); }
        }

        // ---- UDP send / receive ----

        private async Task SendToAsync(string host, int port, byte[] data)
        {
            if (_socket == null || _closed) return;
            string key = host + ":" + port;
            await _sendLock.WaitAsync();
            try
            {
                IOutputStream outStream;
                if (!_outStreams.TryGetValue(key, out outStream))
                {
                    outStream = await _socket.GetOutputStreamAsync(new HostName(host), port.ToString());
                    _outStreams[key] = outStream;
                }
                using (var dw = new DataWriter(outStream))
                {
                    dw.WriteBytes(data);
                    await dw.StoreAsync();
                    dw.DetachStream();
                }
            }
            catch (Exception ex) { App.Log("ICE: send to " + key + " failed: " + ex.Message); }
            finally { _sendLock.Release(); }
        }

        private void OnDatagram(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                var reader = args.GetDataReader();
                uint len = reader.UnconsumedBufferLength;
                if (len < 20) return;
                byte[] buf = new byte[len];
                reader.ReadBytes(buf);

                string srcIp = args.RemoteAddress != null ? args.RemoteAddress.CanonicalName : null;
                int srcPort = 0;
                int.TryParse(args.RemotePort, out srcPort);

                int msgType = (buf[0] << 8) | buf[1];
                byte[] txId = new byte[12];
                Array.Copy(buf, 8, txId, 0, 12);
                string txHex = ToHex(txId);

                if (msgType == MsgBindingSuccess)
                {
                    if (_gatherTxId != null && txHex == ToHex(_gatherTxId))
                    {
                        string mapped = ParseMappedAddress(buf);
                        App.Log("ICE: srflx gathered = " + (mapped ?? "(none)"));
                        if (mapped != null) TrickleSrflx(mapped);
                    }
                    else if (_checkTxIds.Contains(txHex))
                    {
                        if (!_connected)
                        {
                            _connected = true;
                            App.Log("ICE: connectivity check SUCCESS from " + srcIp + ":" + srcPort +
                                    " — managed ICE connected (Stage 2 PASS)");
                        }
                    }
                }
                else if (msgType == MsgBindingRequest)
                {
                    // Inbound connectivity check from the SFU. Answer it (proves our srflx is reachable).
                    App.Log("ICE: inbound check from " + srcIp + ":" + srcPort + " -> responding");
                    var _ = RespondToCheckAsync(txId, srcIp, srcPort);
                }
                else if (msgType == MsgBindingError)
                {
                    App.Log("ICE: STUN error response from " + srcIp + ":" + srcPort);
                }
            }
            catch (Exception ex) { App.Log("ICE: datagram parse failed: " + ex.Message); }
        }

        // ---- Gathering ----

        private async Task SendGatheringRequestAsync()
        {
            if (string.IsNullOrEmpty(_stunHost)) { App.Log("ICE: no STUN host to gather from"); return; }
            _gatherTxId = RandomBytes(12);
            byte[] req = BuildPlainBindingRequest(_gatherTxId);
            await SendToAsync(_stunHost, _stunPort, req);
            App.Log("ICE: gathering request -> " + _stunHost + ":" + _stunPort);
        }

        private void TrickleSrflx(string mapped)
        {
            try
            {
                int colon = mapped.LastIndexOf(':');
                if (colon <= 0) return;
                string ip = mapped.Substring(0, colon);
                string port = mapped.Substring(colon + 1);
                long prio = CandidatePriority(100, 1); // srflx
                string cand = "candidate:1 1 udp " + prio + " " + ip + " " + port +
                              " typ srflx raddr 0.0.0.0 rport " + (_localPort ?? "0") + " generation 0";
                var o = new Windows.Data.Json.JsonObject
                {
                    { "candidate", Windows.Data.Json.JsonValue.CreateStringValue(cand) },
                    { "sdpMid", Windows.Data.Json.JsonValue.CreateStringValue(_mid) },
                    { "sdpMLineIndex", Windows.Data.Json.JsonValue.CreateNumberValue(0) }
                };
                var _ = _signal.SendTrickleAsync(o.Stringify(), TargetSubscriber);
                App.Log("ICE: trickled srflx " + ip + ":" + port);
            }
            catch (Exception ex) { App.Log("ICE: trickle srflx failed: " + ex.Message); }
        }

        // ---- Connectivity checks ----

        private async Task CheckLoopAsync()
        {
            for (int i = 0; i < 25 && !_closed && !_connected; i++)
            {
                List<RemoteCandidate> snapshot;
                lock (_lock) snapshot = new List<RemoteCandidate>(_remoteCandidates);
                foreach (var rc in snapshot)
                {
                    if (_connected || _closed) break;
                    await SendCheckAsync(rc);
                }
                await Task.Delay(400);
            }
            if (!_connected && !_closed)
                App.Log("ICE: no connectivity check succeeded after retries (Stage 2 did not connect)");
        }

        private async Task SendCheckAsync(RemoteCandidate rc)
        {
            if (_socket == null || _closed || _connected) return;
            if (string.IsNullOrEmpty(_remotePwd)) return;
            byte[] txId = RandomBytes(12);
            lock (_lock) _checkTxIds.Add(ToHex(txId));
            // USERNAME for outbound check = "<remoteUfrag>:<localUfrag>".
            string username = _remoteUfrag + ":" + _localUfrag;
            byte[] req = BuildAuthBindingRequest(txId, username, _remotePwd);
            await SendToAsync(rc.Host, rc.Port, req);
        }

        private async Task RespondToCheckAsync(byte[] txId, string srcIp, int srcPort)
        {
            if (_socket == null || _closed || string.IsNullOrEmpty(_localPwd)) return;
            byte[] resp = BuildAuthBindingSuccess(txId, srcIp, srcPort, _localPwd);
            await SendToAsync(srcIp, srcPort, resp);
        }

        // ---- STUN message building ----

        private static byte[] BuildPlainBindingRequest(byte[] txId)
        {
            byte[] msg = new byte[20];
            msg[0] = 0x00; msg[1] = 0x01;       // Binding Request
            msg[2] = 0x00; msg[3] = 0x00;       // length 0
            Array.Copy(MagicCookieBytes, 0, msg, 4, 4);
            Array.Copy(txId, 0, msg, 8, 12);
            return msg;
        }

        private byte[] BuildAuthBindingRequest(byte[] txId, string username, string keyPwd)
        {
            var attrs = new List<byte>();
            AppendAttr(attrs, AttrUsername, Encoding.UTF8.GetBytes(username));

            byte[] prio = new byte[4];
            long p = CandidatePriority(110, 1); // prflx-ish priority for the check
            prio[0] = (byte)((p >> 24) & 0xFF); prio[1] = (byte)((p >> 16) & 0xFF);
            prio[2] = (byte)((p >> 8) & 0xFF); prio[3] = (byte)(p & 0xFF);
            AppendAttr(attrs, AttrPriority, prio);

            // We are the controlled agent (the SFU offered, so it is controlling).
            AppendAttr(attrs, AttrIceControlled, _tieBreaker);

            return FinalizeWithIntegrityAndFingerprint(MsgBindingRequest, txId, attrs, keyPwd);
        }

        private byte[] BuildAuthBindingSuccess(byte[] txId, string mappedIp, int mappedPort, string keyPwd)
        {
            var attrs = new List<byte>();
            byte[] xma = BuildXorMappedAddress(mappedIp, mappedPort, txId);
            if (xma != null) AppendAttr(attrs, AttrXorMappedAddress, xma);
            return FinalizeWithIntegrityAndFingerprint(MsgBindingSuccess, txId, attrs, keyPwd);
        }

        /// <summary>
        /// Assembles a STUN message and appends MESSAGE-INTEGRITY (HMAC-SHA1 over the message with the
        /// length field set to include MI) then FINGERPRINT (CRC32 over the message incl. MI with the
        /// length field set to include FINGERPRINT), per RFC 5389.
        /// </summary>
        private static byte[] FinalizeWithIntegrityAndFingerprint(int msgType, byte[] txId, List<byte> attrs, string keyPwd)
        {
            // ---- MESSAGE-INTEGRITY ----
            int lenWithMi = attrs.Count + 24; // MI attribute = 4 header + 20 value
            byte[] pre = BuildHeaderPlusAttrs(msgType, txId, attrs, lenWithMi);
            byte[] hmac = HmacSha1(Encoding.UTF8.GetBytes(keyPwd), pre);
            AppendAttr(attrs, AttrMessageIntegrity, hmac);

            // ---- FINGERPRINT ----
            int lenWithFp = attrs.Count + 8; // FINGERPRINT attribute = 4 header + 4 value
            byte[] pre2 = BuildHeaderPlusAttrs(msgType, txId, attrs, lenWithFp);
            uint crc = Crc32(pre2) ^ 0x5354554E;
            byte[] fp = { (byte)((crc >> 24) & 0xFF), (byte)((crc >> 16) & 0xFF), (byte)((crc >> 8) & 0xFF), (byte)(crc & 0xFF) };
            AppendAttr(attrs, AttrFingerprint, fp);

            return BuildHeaderPlusAttrs(msgType, txId, attrs, attrs.Count);
        }

        private static byte[] BuildHeaderPlusAttrs(int msgType, byte[] txId, List<byte> attrs, int lengthField)
        {
            byte[] msg = new byte[20 + attrs.Count];
            msg[0] = (byte)((msgType >> 8) & 0xFF);
            msg[1] = (byte)(msgType & 0xFF);
            msg[2] = (byte)((lengthField >> 8) & 0xFF);
            msg[3] = (byte)(lengthField & 0xFF);
            Array.Copy(MagicCookieBytes, 0, msg, 4, 4);
            Array.Copy(txId, 0, msg, 8, 12);
            for (int i = 0; i < attrs.Count; i++) msg[20 + i] = attrs[i];
            return msg;
        }

        private static void AppendAttr(List<byte> attrs, int type, byte[] value)
        {
            attrs.Add((byte)((type >> 8) & 0xFF));
            attrs.Add((byte)(type & 0xFF));
            attrs.Add((byte)((value.Length >> 8) & 0xFF));
            attrs.Add((byte)(value.Length & 0xFF));
            attrs.AddRange(value);
            int pad = (4 - (value.Length % 4)) % 4;
            for (int i = 0; i < pad; i++) attrs.Add(0);
        }

        private static byte[] BuildXorMappedAddress(string ip, int port, byte[] txId)
        {
            byte[] addr = ParseIPv4(ip);
            if (addr == null) return null;
            byte[] v = new byte[8];
            v[0] = 0x00;
            v[1] = 0x01; // IPv4
            int xport = port ^ (int)(MagicCookie >> 16);
            v[2] = (byte)((xport >> 8) & 0xFF);
            v[3] = (byte)(xport & 0xFF);
            v[4] = (byte)(addr[0] ^ MagicCookieBytes[0]);
            v[5] = (byte)(addr[1] ^ MagicCookieBytes[1]);
            v[6] = (byte)(addr[2] ^ MagicCookieBytes[2]);
            v[7] = (byte)(addr[3] ^ MagicCookieBytes[3]);
            return v;
        }

        private static string ParseMappedAddress(byte[] buf)
        {
            int pos = 20;
            while (pos + 4 <= buf.Length)
            {
                int attrType = (buf[pos] << 8) | buf[pos + 1];
                int attrLen = (buf[pos + 2] << 8) | buf[pos + 3];
                int valPos = pos + 4;
                if (valPos + attrLen > buf.Length) break;
                if ((attrType == AttrXorMappedAddress || attrType == AttrMappedAddress) && attrLen >= 8)
                {
                    bool xor = attrType == AttrXorMappedAddress;
                    int family = buf[valPos + 1];
                    int p = (buf[valPos + 2] << 8) | buf[valPos + 3];
                    if (xor) p ^= (int)(MagicCookie >> 16);
                    if (family == 0x01)
                    {
                        byte[] a = new byte[4];
                        Array.Copy(buf, valPos + 4, a, 0, 4);
                        if (xor) { a[0] ^= MagicCookieBytes[0]; a[1] ^= MagicCookieBytes[1]; a[2] ^= MagicCookieBytes[2]; a[3] ^= MagicCookieBytes[3]; }
                        return a[0] + "." + a[1] + "." + a[2] + "." + a[3] + ":" + p;
                    }
                }
                int advance = 4 + attrLen;
                if ((advance % 4) != 0) advance += 4 - (advance % 4);
                pos += advance;
            }
            return null;
        }

        // ---- crypto / helpers ----

        private static byte[] HmacSha1(byte[] key, byte[] data)
        {
            var prov = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha1);
            var k = prov.CreateKey(CryptographicBuffer.CreateFromByteArray(key));
            var sig = CryptographicEngine.Sign(k, CryptographicBuffer.CreateFromByteArray(data));
            byte[] outBytes;
            CryptographicBuffer.CopyToByteArray(sig, out outBytes);
            return outBytes;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();
        private static uint[] BuildCrcTable()
        {
            uint[] t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < data.Length; i++)
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        private static long CandidatePriority(int typePref, int component)
        {
            // RFC 5245: priority = 2^24*typePref + 2^8*localPref + (256 - component).
            const int localPref = 65535;
            return (long)typePref * 16777216 + (long)localPref * 256 + (256 - component);
        }

        private static byte[] ParseIPv4(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return null;
            string[] parts = ip.Split('.');
            if (parts.Length != 4) return null;
            byte[] b = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int v;
                if (!int.TryParse(parts[i], out v) || v < 0 || v > 255) return null;
                b[i] = (byte)v;
            }
            return b;
        }

        private static byte[] RandomBytes(int n)
        {
            byte[] b;
            CryptographicBuffer.CopyToByteArray(CryptographicBuffer.GenerateRandom((uint)n), out b);
            return b;
        }

        private static string RandIceString(int len)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            byte[] r = RandomBytes(len);
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append(chars[r[i] & 0x3F]);
            return sb.ToString();
        }

        private static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        private static bool TryParseStunUrl(string url, out string host, out int port)
        {
            host = null; port = 3478;
            if (string.IsNullOrEmpty(url)) return false;
            url = url.Trim();
            int schemeColon = url.IndexOf(':');
            if (schemeColon <= 0) return false;
            string scheme = url.Substring(0, schemeColon).ToLowerInvariant();
            if (scheme != "stun" && scheme != "turn") return false; // UDP plaintext only
            string rest = url.Substring(schemeColon + 1);
            int q = rest.IndexOf('?');
            if (q >= 0)
            {
                if (rest.Substring(q + 1).ToLowerInvariant().Contains("transport=tcp")) return false;
                rest = rest.Substring(0, q);
            }
            int hostColon = rest.LastIndexOf(':');
            if (hostColon >= 0)
            {
                host = rest.Substring(0, hostColon);
                int parsed;
                if (int.TryParse(rest.Substring(hostColon + 1), out parsed) && parsed > 0) port = parsed;
            }
            else host = rest;
            return !string.IsNullOrEmpty(host);
        }

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
            try { if (_socket != null) { _socket.MessageReceived -= OnDatagram; _socket.Dispose(); } } catch { }
            _socket = null;
            lock (_lock) { _outStreams.Clear(); }
        }
    }
}
