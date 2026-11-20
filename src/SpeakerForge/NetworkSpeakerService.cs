using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>A speaker's role within a group.</summary>
public enum SpeakerAssignment
{
    /// <summary>Plays the full mix (stereo or source channels).</summary>
    Stereo,
    /// <summary>Left channel of a stereo pair only.</summary>
    Left,
    /// <summary>Right channel of a stereo pair only.</summary>
    Right
}

/// <summary>
/// A named set of network speakers sharing playback - a "room" or group.
/// Persisted by the host app; the engine only manages the live model.
/// </summary>
public sealed class SpeakerGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Group";
    public List<GroupMember> Members { get; } = new();
}

/// <summary>One speaker's assignment inside a <see cref="SpeakerGroup"/>.</summary>
public sealed class GroupMember
{
    /// <summary>UPnP UDN of the renderer.</summary>
    public string DeviceUdn { get; set; } = "";
    public SpeakerAssignment Assignment { get; set; } = SpeakerAssignment.Stereo;
    /// <summary>Per-speaker trim in dB (-12..+12), converted to linear gain on stream URL.</summary>
    public double TrimDb { get; set; }
    /// <summary>Delay trim in ms - positive delays this speaker's start (image/sync tuning).</summary>
    public int DelayMs { get; set; }
}

/// <summary>
/// Top-level facade: device discovery, playback routing to one or many
/// renderers, and group management. Hosts the <see cref="NetworkAudioServer"/>
/// that feeds PCM to renderers.
/// </summary>
public sealed class NetworkSpeakerService : IDisposable
{
    /// <summary>Ports probed during the subnet fallback scan.</summary>
    private static readonly int[] CommonUpnpPorts = { 1400, 9197, 80 };

    private readonly NetworkAudioServer _server;
    private readonly Dictionary<string, INetworkRenderer> _devices = new();
    private readonly Dictionary<string, SpeakerGroup> _groups = new();
    private readonly object _gate = new();

    public NetworkSpeakerService(int serverPort = 8977)
    {
        _server = new NetworkAudioServer(serverPort);
    }

    public NetworkAudioServer Server => _server;

    /// <summary>All discovered controllable devices (UDN -> device).</summary>
    public IReadOnlyDictionary<string, INetworkRenderer> Devices { get { lock (_gate) return _devices; } }

    public IReadOnlyDictionary<string, SpeakerGroup> Groups { get { lock (_gate) return _groups; } }

    /// <summary>Starts the embedded PCM server (call once before streaming).</summary>
    public void Start() => _server.Start();

    public void Stop() => _server.Stop();

    // ---------------- discovery ----------------

    /// <summary>
    /// Scans the LAN and refreshes the device table. SSDP multicast first;
    /// if that finds nothing (or to catch devices that ignore M-SEARCH), a
    /// parallel TCP probe of common UPnP ports across the local /24 follows.
    /// </summary>
    public async Task<List<INetworkRenderer>> DiscoverAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        var found = new List<INetworkRenderer>(await SsdpDiscovery.ScanAsync(timeoutMs, ct));
        var ssdpHosts = new HashSet<string>(found.Select(d => d.Address), StringComparer.OrdinalIgnoreCase);

        // Always run the port probe too - SSDP misses devices that ignore
        // M-SEARCH (multicast isolation, Sonos quirks) even when it finds some.
        var probed = await ProbeSubnetAsync(ct);
        var hostIps = probed.Select(h => h.Split(':')[0]).Distinct().ToList();
        var knownUdns = new HashSet<string>(found.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        var describeTasks = hostIps
            .Where(ip => !ssdpHosts.Contains(ip))
            .Select(ip => DescribeHostAsync(ip, ct));
        foreach (var dev in (await Task.WhenAll(describeTasks)).Where(d => d is not null))
            if (knownUdns.Add(dev.Id))
                found.Add(dev);

        // Google Cast + AirPlay devices via mDNS - same renderer interface,
        // different protocols. Runs alongside UPnP discovery.
        try
        {
            var castDevices = await CastDiscovery.DiscoverAsync(Math.Min(timeoutMs, 2500), ct);
            foreach (var c in castDevices)
            {
                var dev = new CastDevice(c);
                if (knownUdns.Add(dev.Id)) found.Add(dev);
            }
        }
        catch { /* mDNS unavailable - UPnP results still stand */ }
        try
        {
            var airPlayDevices = await AirPlayDiscovery.DiscoverAsync(Math.Min(timeoutMs, 2500), ct);
            foreach (var a in airPlayDevices)
            {
                var dev = new AirPlayDevice(a);
                if (knownUdns.Add(dev.Id)) found.Add(dev);
            }
        }
        catch { /* mDNS unavailable - UPnP results still stand */ }

        lock (_gate)
        {
            foreach (var d in found)
            {
                var key = d.Id;
                _devices[key] = d;
            }
        }
        return found;
    }

    /// <summary>
    /// Adds a manually-entered device (IP or host, optionally host:port).
    /// When no port is given, probes common UPnP ports (Sonos uses 1400, not 80).
    /// </summary>
    public async Task<INetworkRenderer?> AddDeviceByAddressAsync(string host, CancellationToken ct = default)
    {
        foreach (var baseUrl in BuildAddressTargets(host))
        {
            foreach (var path in new[] { "/xml/device_description.xml", "/dmr.xml", "/upnpdevicedesc.xml", "/device.xml" })
            {
                var dev = await UpnpDevice.DescribeAsync($"{baseUrl}{path}", ct);
                if (dev.IsControllable)
                {
                    if (string.IsNullOrEmpty(dev.Udn)) dev.Udn = $"manual-{host}";
                    lock (_gate) _devices[dev.Id] = dev;
                    return dev;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Expands "1.2.3.4" into candidate base URLs across common UPnP ports.
    /// "1.2.3.4:1400" is used as-is.
    /// </summary>
    internal static IEnumerable<string> BuildAddressTargets(string host)
    {
        host = host.Trim();
        if (host.Contains(':'))
        {
            yield return $"http://{host}";
            yield break;
        }
        // Sonos and many DLNA renderers serve UPnP on 1400; try it first, then 80.
        foreach (var port in CommonUpnpPorts)
            yield return $"http://{host}:{port}";
    }

    /// <summary>
    /// Fast parallel TCP sweep of the local /24 for hosts listening on common
    /// UPnP ports. Returns "ip:port" strings for every open socket found.
    /// </summary>
    private static async Task<List<string>> ProbeSubnetAsync(CancellationToken ct)
    {
        var results = new ConcurrentBag<string>();
        var locals = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .ToList();
        if (locals.Count == 0) return new List<string>();

        var probes = locals
            .SelectMany(local =>
            {
                var parts = local.GetAddressBytes();
                var prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";
                return Enumerable.Range(1, 254)
                    .SelectMany(i => CommonUpnpPorts.Select(p => $"{prefix}.{i}:{p}"));
            })
            .Select(addr => ProbePortAsync(addr, ct));

        await Task.WhenAll(probes);
        return results.ToList();

        async Task ProbePortAsync(string addr, CancellationToken token)
        {
            var hostPort = addr.Split(':');
            using var tcp = new TcpClient();
            try
            {
                var connect = tcp.ConnectAsync(hostPort[0], int.Parse(hostPort[1]));
                var timeout = Task.Delay(400, token);
                if (await Task.WhenAny(connect, timeout) == timeout) return;
                await connect;
                if (tcp.Connected) results.Add(addr);
            }
            catch { }
        }
    }

    /// <summary>Describes a host[:port] found by the port probe.</summary>
    private static async Task<UpnpDevice?> DescribeHostAsync(string hostPort, CancellationToken ct)
    {
        var host = hostPort;
        foreach (var baseUrl in BuildAddressTargets(host))
        {
            foreach (var path in new[] { "/xml/device_description.xml", "/dmr.xml", "/upnpdevicedesc.xml", "/device.xml" })
            {
                var dev = await UpnpDevice.DescribeAsync($"{baseUrl}{path}", ct);
                if (dev.IsControllable) return dev;
            }
        }
        return null;
    }

    private static string HostOf(string location)
    {
        try { return new Uri(location).Authority; }
        catch { return location; }
    }

    public void RemoveDevice(string udn)
    {
        lock (_gate) _devices.Remove(udn);
    }

    // ---------------- groups ----------------

    public SpeakerGroup CreateGroup(string name)
    {
        var g = new SpeakerGroup { Name = name };
        lock (_gate) _groups[g.Id] = g;
        return g;
    }

    public bool DeleteGroup(string id)
    {
        lock (_gate) return _groups.Remove(id);
    }

    public SpeakerGroup? GetGroup(string id)
    {
        lock (_gate) return _groups.TryGetValue(id, out var g) ? g : null;
    }

    public void AddMember(string groupId, string deviceUdn, SpeakerAssignment assignment = SpeakerAssignment.Stereo)
    {
        var g = GetGroup(groupId);
        if (g is null || !_devices.ContainsKey(deviceUdn)) return;
        if (g.Members.Any(m => m.DeviceUdn == deviceUdn)) return;
        g.Members.Add(new GroupMember { DeviceUdn = deviceUdn, Assignment = assignment });
    }

    public void RemoveMember(string groupId, string deviceUdn)
    {
        var g = GetGroup(groupId);
        g?.Members.RemoveAll(m => m.DeviceUdn == deviceUdn);
    }

    /// <summary>
    /// Convenience: pair two speakers as a stereo image (L + R).
    /// </summary>
    public SpeakerGroup CreateStereoPair(string name, string leftUdn, string rightUdn)
    {
        var g = CreateGroup(name);
        AddMember(g.Id, leftUdn, SpeakerAssignment.Left);
        AddMember(g.Id, rightUdn, SpeakerAssignment.Right);
        return g;
    }

    // ---------------- playback ----------------

    /// <summary>
    /// Routes the current PCM stream to a single speaker.
    /// </summary>
    public async Task<bool> PlayToDeviceAsync(string deviceUdn, SpeakerAssignment assignment = SpeakerAssignment.Stereo,
        double trimDb = 0, CancellationToken ct = default)
    {
        var dev = GetDevice(deviceUdn);
        if (dev is null) return false;
        var url = _server.BuildStreamUrl(assignment.ToQueryValue(), DbToLinear(trimDb));
        return await dev.PlayUriAsync(url, BuildStreamMetadata(assignment.ToQueryValue()), ct);
    }

    /// <summary>
    /// Routes the PCM stream to every member of a group, applying per-speaker
    /// channel role, trim and delay stagger.
    /// </summary>
    public async Task PlayGroupAsync(string groupId, CancellationToken ct = default)
    {
        var g = GetGroup(groupId);
        if (g is null) return;

        // Order by delay so staggered Play calls land roughly together
        var ordered = g.Members
            .OrderBy(m => m.DelayMs)
            .Select(m => (member: m, device: GetDevice(m.DeviceUdn)))
            .Where(x => x.device is not null)
            .ToList();

        int baseDelay = ordered.Count > 0 ? ordered.Min(x => x.member.DelayMs) : 0;
        var tasks = new List<Task>();
        foreach (var (member, device) in ordered)
        {
            int stagger = member.DelayMs - baseDelay;
            var url = _server.BuildStreamUrl(member.Assignment.ToQueryValue(), DbToLinear(member.TrimDb));

            tasks.Add(Task.Run(async () =>
            {
                if (stagger > 0) await Task.Delay(stagger, ct);
                await device.PlayUriAsync(url, BuildStreamMetadata(member.Assignment.ToQueryValue()), ct);
                if (member.TrimDb != 0)
                    await device.SetVolumeAsync((int)Math.Round(50 + member.TrimDb * 4), ct); // rough mapping
            }, ct));
        }
        await Task.WhenAll(tasks);
    }

    public async Task StopDeviceAsync(string deviceUdn, CancellationToken ct = default)
    {
        var dev = GetDevice(deviceUdn);
        if (dev is null) return;
        await dev.StopAsync(ct);
    }

    public async Task StopGroupAsync(string groupId, CancellationToken ct = default)
    {
        var g = GetGroup(groupId);
        if (g is null) return;
        var tasks = g.Members
            .Select(m => GetDevice(m.DeviceUdn))
            .Where(d => d is not null)
            .Select(d => d!.StopAsync(ct))
            .ToList();
        await Task.WhenAll(tasks);
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        Dictionary<string, INetworkRenderer> snapshot;
        lock (_gate) snapshot = new Dictionary<string, INetworkRenderer>(_devices);
        var tasks = snapshot.Values.Select(d => d.StopAsync(ct)).ToList();
        await Task.WhenAll(tasks);
    }

    /// <summary>Queries one device's transport state.</summary>
    public async Task<string?> GetDeviceStateAsync(string deviceUdn, CancellationToken ct = default)
    {
        var dev = GetDevice(deviceUdn);
        return dev is null ? null : await dev.GetStateAsync(ct);
    }

    /// <summary>Sets absolute volume (0-100) on one device.</summary>
    public async Task<bool> SetDeviceVolumeAsync(string deviceUdn, int volume, CancellationToken ct = default)
    {
        var dev = GetDevice(deviceUdn);
        return dev is not null && await dev.SetVolumeAsync(volume, ct);
    }

    private INetworkRenderer? GetDevice(string udn)
    {
        lock (_gate) return _devices.TryGetValue(udn, out var d) ? d : null;
    }

    private static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>
    /// DIDL-Lite metadata describing the live PCM stream. Renderers like Sonos
    /// reject SetAVTransportURI calls with empty metadata - they need the
    /// protocolInfo to know they can decode audio/L16 at our rate/channels.
    /// </summary>
    private string BuildStreamMetadata(string role)
    {
        int rate = _server.SampleRate;
        int ch = role is "left" or "right" ? 1 : Math.Max(1, _server.SourceChannels);
        return
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
            "<item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
            "<dc:title>AudioForge Network Stream</dc:title>" +
            "<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>" +
            $"<res protocolInfo=\"http-get:*:audio/x-wav:{rate}:{ch}\">*</res>" +
            "</item></DIDL-Lite>";
    }

    public void Dispose()
    {
        Stop();
    }
}

public static class SpeakerAssignmentExtensions
{
    /// <summary>Maps the assignment to the /stream channel-role query value.</summary>
    public static string ToQueryValue(this SpeakerAssignment a) => a switch
    {
        SpeakerAssignment.Left => "left",
        SpeakerAssignment.Right => "right",
        _ => "stereo"
    };
}
