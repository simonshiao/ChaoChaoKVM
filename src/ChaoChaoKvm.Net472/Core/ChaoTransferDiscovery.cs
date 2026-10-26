using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ChaoChaoKvm.Core
{
    public sealed class ChaoTransferPeerState
    {
        public string TargetIp { get; set; }
        public string PeerIp { get; set; }
        public bool OutboundConnected { get; set; }
        public bool InboundSeen { get; set; }
    }

    public static class ChaoTransferDiscovery
    {
        public static ChaoTransferPeerState TryGetState()
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", 48223, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(350)) return null;
                    client.EndConnect(ar);
                    client.ReceiveTimeout = 700;
                    client.SendTimeout = 700;
                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.AutoFlush = true;
                        writer.WriteLine("{\"cmd\":\"status\"}");
                        string line = reader.ReadLine();
                        if (string.IsNullOrWhiteSpace(line)) return null;
                        Dictionary<string, object> root = JsonUtil.Parse(line);
                        object rawStatus;
                        if (!root.TryGetValue("status", out rawStatus)) return null;
                        Dictionary<string, object> status = rawStatus as Dictionary<string, object>;
                        if (status == null) return null;
                        return new ChaoTransferPeerState
                        {
                            TargetIp = JsonUtil.GetString(status, "target_ip", ""),
                            PeerIp = JsonUtil.GetString(status, "peer_ip", ""),
                            OutboundConnected = JsonUtil.GetBool(status, "outbound_connected", false),
                            InboundSeen = JsonUtil.GetBool(status, "inbound_seen", false)
                        };
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
