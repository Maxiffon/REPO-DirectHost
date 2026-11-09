// REPO DirectHost — public IPv4 discovery with STUN binding requests (RFC 5389), used by the host to publish
// its addresses in the Steam lobby data.
//
// Split-tunnel VPNs (Flowvy/mihomo, Koala Clash, ...) often route some providers (Google, Cloudflare) through the
// VPN: those STUN servers then report the VPN exit, not the real address. So several servers in different networks
// are asked at once and EVERY distinct answer is published; the client's probe finds out which one really works.
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RepoDirectHost.Net;

namespace RepoDirectHost.Game
{
    internal static class Stun
    {
        /// <summary>Asks all servers in parallel; returns each distinct address with the servers that reported it.</summary>
        public static List<(IPAddress ip, string servers)> QueryAll(int timeoutMs = 2500)
        {
            var found = new List<(IPAddress ip, string servers)>();
            var threads = new List<Thread>();
            foreach (var s in StunCodec.Servers)
            {
                var server = s;
                var t = new Thread(() =>
                {
                    var ip = Query(server, timeoutMs);
                    if (ip == null) return;
                    lock (found)
                    {
                        int i = found.FindIndex(f => f.ip.Equals(ip));
                        string name = server.Split(':')[0];
                        if (i < 0) found.Add((ip, name));
                        else found[i] = (ip, found[i].servers + ", " + name);
                    }
                }) { IsBackground = true, Name = "DirectHost STUN " + server };
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) t.Join(timeoutMs + 3000);
            lock (found) return new List<(IPAddress, string)>(found);
        }

        private static IPAddress Query(string server, int timeoutMs)
        {
            try
            {
                var parts = server.Split(':');
                var target = System.Array.Find(Dns.GetHostAddresses(parts[0]), a => a.AddressFamily == AddressFamily.InterNetwork);
                if (target == null) return null;
                using (var u = new UdpClient(AddressFamily.InterNetwork))
                {
                    u.Client.ReceiveTimeout = timeoutMs;
                    var req = StunCodec.Request(out var tid);
                    var ep = new IPEndPoint(target, int.Parse(parts[1]));
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        u.Send(req, req.Length, ep);
                        try
                        {
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            var resp = u.Receive(ref from);
                            var m = StunCodec.Parse(resp, resp.Length, tid);
                            if (m != null) return m.Address;
                        }
                        catch (SocketException) { }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
