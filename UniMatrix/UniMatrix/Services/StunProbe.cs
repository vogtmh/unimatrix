using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace UniMatrix.Services
{
    /// <summary>
    /// MANAGED-RTC SPIKE — Stage 1 (dependency-free). Before sinking effort into vendoring BouncyCastle
    /// (DTLS) + SIPSorcery source, this answers the make-or-break foundation question: can the UWP
    /// appcontainer open a UDP socket and exchange datagrams with the SFU's STUN/TURN servers at all?
    ///
    /// It sends a plain STUN Binding Request (RFC 5389) over UDP to each STUN/TURN host advertised in
    /// the LiveKit JoinResponse ICE servers and logs the XOR-MAPPED-ADDRESS from the success response.
    /// A reply proves: (a) the appcontainer allows outbound+inbound UDP, and (b) we can reach the SFU's
    /// ICE infrastructure in pure managed code. Uses ONLY Windows.Networking.Sockets + Windows.Security
    /// .Cryptography, so it compiles and runs on the legacy uap10.0.14393 surface (no netstandard2.0
    /// dependency, unlike SIPSorcery). Logs "STUN:" lines.
    /// </summary>
    internal sealed class StunProbe
    {
        private const uint MagicCookie = 0x2112A442;
        private readonly List<DatagramSocket> _sockets = new List<DatagramSocket>();
        private bool _closed;

        /// <summary>Probe every usable STUN/TURN host in the given ICE servers (UDP only).</summary>
        public async Task ProbeAsync(IList<LiveKitIceServer> iceServers)
        {
            if (iceServers == null || iceServers.Count == 0)
            {
                App.Log("STUN: no ICE servers to probe");
                return;
            }

            var seen = new HashSet<string>();
            foreach (var srv in iceServers)
            {
                if (srv == null || srv.Urls == null) continue;
                foreach (var url in srv.Urls)
                {
                    string host; int port;
                    if (!TryParseStunUrl(url, out host, out port)) continue;
                    string key = host + ":" + port;
                    if (!seen.Add(key)) continue;
                    await ProbeOneAsync(host, port);
                }
            }
        }

        private async Task ProbeOneAsync(string host, int port)
        {
            if (_closed) return;
            DatagramSocket socket = null;
            try
            {
                App.Log("STUN: probing " + host + ":" + port);
                socket = new DatagramSocket();
                socket.MessageReceived += OnMessage;
                lock (_sockets) _sockets.Add(socket);

                await socket.ConnectAsync(new HostName(host), port.ToString());

                byte[] req = BuildBindingRequest();
                using (var dw = new DataWriter(socket.OutputStream))
                {
                    dw.WriteBytes(req);
                    await dw.StoreAsync();
                    dw.DetachStream();
                }
                App.Log("STUN: binding request sent to " + host + ":" + port + " (" + req.Length + " bytes)");
            }
            catch (Exception ex)
            {
                App.Log("STUN: probe FAILED for " + host + ":" + port + " -> " + ex.Message);
                if (socket != null)
                {
                    try { socket.Dispose(); } catch { }
                }
            }
        }

        private void OnMessage(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                var reader = args.GetDataReader();
                uint len = reader.UnconsumedBufferLength;
                if (len < 20) { App.Log("STUN: short datagram (" + len + " bytes)"); return; }
                byte[] buf = new byte[len];
                reader.ReadBytes(buf);

                int msgType = (buf[0] << 8) | buf[1];
                string remote = args.RemoteAddress != null ? args.RemoteAddress.DisplayName : "?";
                if (msgType == 0x0101)
                {
                    string mapped = ParseXorMappedAddress(buf);
                    App.Log("STUN: SUCCESS from " + remote + " — reflexive address " +
                            (mapped ?? "(not found)") + " — appcontainer UDP works (Stage 1 PASS)");
                }
                else if (msgType == 0x0111)
                {
                    App.Log("STUN: ERROR response from " + remote + " (round-trip OK; appcontainer UDP works)");
                }
                else
                {
                    App.Log("STUN: response type 0x" + msgType.ToString("X4") + " from " + remote);
                }
            }
            catch (Exception ex)
            {
                App.Log("STUN: parse failed -> " + ex.Message);
            }
        }

        private static byte[] BuildBindingRequest()
        {
            byte[] msg = new byte[20];
            msg[0] = 0x00; msg[1] = 0x01;            // type = Binding Request
            msg[2] = 0x00; msg[3] = 0x00;            // length = 0 (no attributes)
            msg[4] = 0x21; msg[5] = 0x12;            // magic cookie
            msg[6] = 0xA4; msg[7] = 0x42;
            // 12-byte transaction id (random)
            var rnd = CryptographicBuffer.GenerateRandom(12);
            byte[] tx = new byte[12];
            CryptographicBuffer.CopyToByteArray(rnd, out tx);
            Array.Copy(tx, 0, msg, 8, 12);
            return msg;
        }

        private static string ParseXorMappedAddress(byte[] buf)
        {
            int pos = 20; // attributes begin after the 20-byte header
            while (pos + 4 <= buf.Length)
            {
                int attrType = (buf[pos] << 8) | buf[pos + 1];
                int attrLen = (buf[pos + 2] << 8) | buf[pos + 3];
                int valPos = pos + 4;
                if (valPos + attrLen > buf.Length) break;

                // 0x0020 XOR-MAPPED-ADDRESS, 0x0001 MAPPED-ADDRESS
                if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8)
                {
                    bool xor = attrType == 0x0020;
                    int family = buf[valPos + 1];
                    int p = (buf[valPos + 2] << 8) | buf[valPos + 3];
                    if (xor) p ^= (int)(MagicCookie >> 16);
                    if (family == 0x01) // IPv4
                    {
                        byte[] a = new byte[4];
                        Array.Copy(buf, valPos + 4, a, 0, 4);
                        if (xor)
                        {
                            a[0] ^= 0x21; a[1] ^= 0x12; a[2] ^= 0xA4; a[3] ^= 0x42;
                        }
                        return a[0] + "." + a[1] + "." + a[2] + "." + a[3] + ":" + p;
                    }
                    return "(ipv6):" + p;
                }

                // attributes are padded to 4-byte boundaries
                int advance = 4 + attrLen;
                if ((advance % 4) != 0) advance += 4 - (advance % 4);
                pos += advance;
            }
            return null;
        }

        private static bool TryParseStunUrl(string url, out string host, out int port)
        {
            host = null; port = 3478;
            if (string.IsNullOrEmpty(url)) return false;
            url = url.Trim();

            int schemeColon = url.IndexOf(':');
            if (schemeColon <= 0) return false;
            string scheme = url.Substring(0, schemeColon).ToLowerInvariant();
            if (scheme != "stun" && scheme != "turn") return false; // skip stuns/turns (TLS) — UDP only

            string rest = url.Substring(schemeColon + 1);

            // strip ?transport=... ; skip TCP transports (we only test UDP here)
            int q = rest.IndexOf('?');
            if (q >= 0)
            {
                string query = rest.Substring(q + 1).ToLowerInvariant();
                if (query.Contains("transport=tcp")) return false;
                rest = rest.Substring(0, q);
            }

            // rest = host[:port] (hosts are SFU domain names, so a trailing :port is unambiguous)
            int hostColon = rest.LastIndexOf(':');
            if (hostColon >= 0)
            {
                host = rest.Substring(0, hostColon);
                int parsed;
                if (int.TryParse(rest.Substring(hostColon + 1), out parsed) && parsed > 0)
                    port = parsed;
            }
            else
            {
                host = rest;
            }
            return !string.IsNullOrEmpty(host);
        }

        public void Close()
        {
            _closed = true;
            List<DatagramSocket> copy;
            lock (_sockets)
            {
                copy = new List<DatagramSocket>(_sockets);
                _sockets.Clear();
            }
            foreach (var s in copy)
            {
                try { s.MessageReceived -= OnMessage; } catch { }
                try { s.Dispose(); } catch { }
            }
        }
    }
}
