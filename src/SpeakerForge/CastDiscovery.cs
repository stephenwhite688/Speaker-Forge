using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// mDNS browser for Google Cast devices (_googlecast._tcp.local).
/// </summary>
public static class CastDiscovery
{
    private static readonly IPAddress MdnsAddr = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    public sealed class CastInfo
    {
        public string Id = "";
        public string FriendlyName = "";
        public string ModelName = "";
        public string CastType = "";
        public string Host = "";
        public int Port;
    }

    /// <summary>Browses for Cast devices; returns within ~timeoutMs+1s.</summary>
    public static async Task<List<CastInfo>> DiscoverAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        var answers = new DnsWire.Answers();
        await BrowseAsync("_googlecast._tcp.local", answers, timeoutMs, ct);

        var result = new List<CastInfo>();
        foreach (var (instance, txt) in answers.Txts)
        {
            if (!answers.Srvs.TryGetValue(instance, out var srv)) continue;
            if (!answers.Addrs.TryGetValue(srv.Target.TrimEnd('.'), out var ip) &&
                !answers.Addrs.TryGetValue(instance.TrimEnd('.'), out ip)) continue;

            var info = new CastInfo { Host = ip.ToString(), Port = srv.Port == 0 ? 8009 : srv.Port };
            foreach (var kv in txt)
            {
                switch (kv.Key)
                {
                    case "fn": info.FriendlyName = kv.Value; break;
                    case "md": info.ModelName = kv.Value; break;
                    case "ca": info.CastType = kv.Value; break;
                    case "id": info.Id = kv.Value; break;
                }
            }
            if (string.IsNullOrEmpty(info.Id)) info.Id = $"cast-{info.Host}:{info.Port}";
            if (string.IsNullOrEmpty(info.FriendlyName)) info.FriendlyName = info.ModelName;
            result.Add(info);
        }
        return result;
    }

    internal static async Task BrowseAsync(string serviceType, DnsWire.Answers answers,
        int timeoutMs, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = 500;
        udp.EnableBroadcast = true;
        udp.MulticastLoopback = false;
        try { udp.JoinMulticastGroup(MdnsAddr); } catch { }

        var query = DnsWire.BuildQuery(serviceType, "PTR");
        await udp.SendAsync(query, new IPEndPoint(MdnsAddr, MdnsPort), ct);

        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        listenCts.CancelAfter(timeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs + 800);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(listenCts.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            try { DnsWire.ParsePacket(r.Buffer, answers); }
            catch { /* malformed packet - skip */ }
        }
    }
}
