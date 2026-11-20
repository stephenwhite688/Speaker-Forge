using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// SSDP (Simple Service Discovery Protocol) scanner for UPnP media renderers.
/// Sends M-SEARCH multicast to 239.255.255.250:1900 and describes every
/// responding device location.
/// </summary>
public static class SsdpDiscovery
{
    private const string MulticastAddr = "239.255.255.250";
    private const int MulticastPort = 1900;

    /// <summary>
    /// Scans the LAN for UPnP MediaRenderer devices.
    /// </summary>
    /// <param name="timeoutMs">How long to listen for responses (default 3000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Controllable renderer devices found.</returns>
    public static async Task<List<UpnpDevice>> ScanAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.MulticastLoopback = false;

        // Join the multicast group on all local interfaces
        foreach (var nic in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            if (nic.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(nic))
            {
                try { udp.JoinMulticastGroup(IPAddress.Parse(MulticastAddr), nic); } catch { }
            }
        }
        try { udp.JoinMulticastGroup(IPAddress.Parse(MulticastAddr)); } catch { }

        // ssdp:all catches every UPnP device - vendors like Sonos announce as
        // ZonePlayer rather than MediaRenderer, so targeted ST queries miss them.
        // Description parsing below filters for controllable renderers.
        var search =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: ssdp:all\r\n\r\n";

        var target = new IPEndPoint(IPAddress.Parse(MulticastAddr), MulticastPort);
        await udp.SendAsync(Encoding.ASCII.GetBytes(search), target, ct);

        // Targeted searches too - some devices only answer service/device STs.
        foreach (var st in new[]
        {
            "urn:schemas-upnp-org:device:MediaRenderer:1",
            "urn:schemas-upnp-org:service:AVTransport:1"
        })
        {
            var s = search.Replace("ST: ssdp:all", $"ST: {st}");
            await udp.SendAsync(Encoding.ASCII.GetBytes(s), target, ct);
        }

        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        listenCts.CancelAfter(timeoutMs + 1500);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(listenCts.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                continue; // poll again until the deadline
            }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            var text = Encoding.ASCII.GetString(result.Buffer);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                    locations.Add(trimmed["LOCATION:".Length..].Trim());
            }
        }

        var devices = new List<UpnpDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in locations)
        {
            var dev = await UpnpDevice.DescribeAsync(loc, ct);
            if (!dev.IsControllable) continue;
            var key = string.IsNullOrEmpty(dev.Udn) ? loc : dev.Udn;
            if (seen.Add(key)) devices.Add(dev);
        }
        return devices;
    }

    /// <summary>Describes a single device at a known LOCATION URL.</summary>
    public static Task<UpnpDevice> DescribeAsync(string location, CancellationToken ct = default) =>
        UpnpDevice.DescribeAsync(location, ct);
}
