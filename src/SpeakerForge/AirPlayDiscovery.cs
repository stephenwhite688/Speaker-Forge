using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// mDNS browser for AirPlay audio receivers. _raop._tcp = RAOP (audio-only,
/// AirPlay 1); _airplay._tcp = full AirPlay devices (also carries audio).
/// </summary>
public static class AirPlayDiscovery
{
    public sealed class AirPlayInfo
    {
        public string DeviceId = "";
        public string FriendlyName = "";
        public string Model = "";
        public string Manufacturer = "";
        public string Host = "";
        public int Port;
        public bool RequiresPassword;
    }

    public static async Task<List<AirPlayInfo>> DiscoverAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        var answers = new DnsWire.Answers();
        // browse both service types into the same answer set
        await CastDiscovery.BrowseAsync("_raop._tcp.local", answers, timeoutMs, ct);
        await CastDiscovery.BrowseAsync("_airplay._tcp.local", answers, timeoutMs / 2, ct);

        var result = new List<AirPlayInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, txt) in answers.Txts)
        {
            if (!answers.Srvs.TryGetValue(instance, out var srv)) continue;
            if (!answers.Addrs.TryGetValue(srv.Target.TrimEnd('.'), out var ip) &&
                !answers.Addrs.TryGetValue(instance.TrimEnd('.'), out ip)) continue;

            var info = new AirPlayInfo { Host = ip.ToString(), Port = srv.Port };
            foreach (var kv in txt)
            {
                switch (kv.Key)
                {
                    case "deviceid": info.DeviceId = kv.Value; break;
                    case "model": info.Model = kv.Value; break;
                    case "manufacturer": info.Manufacturer = kv.Value; break;
                    case "name": info.FriendlyName = kv.Value; break;
                    case "sourceVersion": break;
                }
            }
            // some devices only send fn= style or nothing usable - fall back to host
            if (string.IsNullOrEmpty(info.FriendlyName)) info.FriendlyName = info.Model;
            if (string.IsNullOrEmpty(info.FriendlyName)) info.FriendlyName = $"AirPlay ({info.Host})";
            if (string.IsNullOrEmpty(info.DeviceId)) info.DeviceId = $"airplay-{info.Host}:{info.Port}";

            var key = $"{info.Host}:{info.Port}";
            if (seen.Add(key)) result.Add(info);
        }
        return result;
    }
}
