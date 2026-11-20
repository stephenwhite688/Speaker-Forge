using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SpeakerForge;

/// <summary>
/// A discovered UPnP/DLNA media renderer (speaker, TV, smart speaker) on the LAN.
/// Populated from the device's XML description document fetched at the SSDP
/// LOCATION URL.
/// </summary>
public sealed class UpnpDevice : INetworkRenderer
{
    /// <summary>Unique device name from the UPnP description.</summary>
    public string Udn { get; set; } = "";

    /// <summary>Human-friendly name shown by the device (e.g. "Kitchen Speaker").</summary>
    public string FriendlyName { get; set; } = "";

    public string Manufacturer { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string ModelNumber { get; set; } = "";

    /// <summary>Base URL of the device description (scheme://host:port/path).</summary>
    public string Location { get; set; } = "";

    /// <summary>AVTransport service control URL (play/pause/stop/set-uri).</summary>
    public string? AvTransportControlUrl { get; set; }

    /// <summary>AVTransport service type, e.g. urn:schemas-upnp-org:service:AVTransport:1</summary>
    public string? AvTransportServiceType { get; set; }

    /// <summary>RenderingControl service control URL (volume/mute).</summary>
    public string? RenderingControlUrl { get; set; }

    public string? RenderingControlServiceType { get; set; }

    /// <summary>True when the device exposes both services we need.</summary>
    public bool IsControllable =>
        !string.IsNullOrEmpty(AvTransportControlUrl) &&
        !string.IsNullOrEmpty(RenderingControlUrl);

    /// <summary>Fetches and parses the device description at <see cref="Location"/>.</summary>
    public static async Task<UpnpDevice> DescribeAsync(string location, CancellationToken ct = default)
    {
        var device = new UpnpDevice { Location = location };
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            var xml = await http.GetStringAsync(location, ct);
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null) return device;

            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            var devEl = root.Element(ns + "device") ?? root;

            string? Text(string name) => devEl.Element(ns + name)?.Value;
            device.Udn = Text("UDN") ?? "";
            device.FriendlyName = Text("friendlyName") ?? "";
            device.Manufacturer = Text("manufacturer") ?? "";
            device.ModelName = Text("modelName") ?? "";
            device.ModelNumber = Text("modelNumber") ?? "";

            // deviceList may contain embedded devices (e.g. zone players) -
            // take the first renderer-ish service set we find, preferring the
            // root device's own serviceList.
            foreach (var serviceList in devEl.Elements(ns + "serviceList"))
            {
                foreach (var svc in serviceList.Elements(ns + "service"))
                {
                    string? st = svc.Element(ns + "serviceType")?.Value;
                    string? cu = svc.Element(ns + "controlURL")?.Value;
                    if (st is null || cu is null) continue;
                    cu = ResolveUrl(location, cu);
                    if (st.Contains("AVTransport") && device.AvTransportControlUrl is null)
                    {
                        device.AvTransportControlUrl = cu;
                        device.AvTransportServiceType = st;
                    }
                    else if (st.Contains("RenderingControl") && device.RenderingControlUrl is null)
                    {
                        device.RenderingControlUrl = cu;
                        device.RenderingControlServiceType = st;
                    }
                }
            }

            // Embedded devices: many renderers nest the actual player one level
            // down (e.g. Sonos ZonePlayer inside a Room). Search recursively.
            if (!device.IsControllable)
            {
                var embedded = await FindEmbeddedAsync(devEl, location, http, ct);
                if (embedded is not null)
                {
                    if (device.AvTransportControlUrl is null) device.AvTransportControlUrl = embedded.AvTransportControlUrl;
                    if (device.AvTransportServiceType is null) device.AvTransportServiceType = embedded.AvTransportServiceType;
                    if (device.RenderingControlUrl is null) device.RenderingControlUrl = embedded.RenderingControlUrl;
                    if (device.RenderingControlServiceType is null) device.RenderingControlServiceType = embedded.RenderingControlServiceType;
                }
            }
        }
        catch { /* unreachable device - return shell record */ }
        return device;
    }

    private static async Task<UpnpDevice?> FindEmbeddedAsync(
        XElement root, string location, HttpClient http, CancellationToken ct)
    {
        XNamespace ns = "urn:schemas-upnp-org:device-1-0";
        foreach (var dl in root.Elements(ns + "deviceList"))
        {
            foreach (var dev in dl.Elements(ns + "device"))
            {
                var sub = new UpnpDevice { Location = location };
                foreach (var sl in dev.Elements(ns + "serviceList"))
                {
                    foreach (var svc in sl.Elements(ns + "service"))
                    {
                        string? st = svc.Element(ns + "serviceType")?.Value;
                        string? cu = svc.Element(ns + "controlURL")?.Value;
                        if (st is null || cu is null) continue;
                        cu = ResolveUrl(location, cu);
                        if (st.Contains("AVTransport") && sub.AvTransportControlUrl is null)
                        {
                            sub.AvTransportControlUrl = cu;
                            sub.AvTransportServiceType = st;
                        }
                        else if (st.Contains("RenderingControl") && sub.RenderingControlUrl is null)
                        {
                            sub.RenderingControlUrl = cu;
                            sub.RenderingControlServiceType = st;
                        }
                    }
                }
                if (sub.IsControllable) return sub;
                // one more level of nesting
                var deeper = await FindEmbeddedAsync(dev, location, http, ct);
                if (deeper is not null) return deeper;
            }
        }
        return null;
    }

    /// <summary>Resolves a possibly-relative control URL against the device base.</summary>
    public static string ResolveUrl(string baseUrl, string controlUrl)
    {
        if (Uri.TryCreate(controlUrl, UriKind.Absolute, out _)) return controlUrl;
        if (Uri.TryCreate(new Uri(baseUrl), controlUrl, out var abs)) return abs.ToString();
        return controlUrl;
    }

    // ---------- INetworkRenderer ----------

    public string Id => string.IsNullOrEmpty(Udn) ? Location : Udn;
    public string Protocol => "UPnP/DLNA";
    public string Address
    {
        get
        {
            try { return new Uri(Location).Authority; }
            catch { return Location; }
        }
    }

    public Task<bool> PlayUriAsync(string uri, string metadata = "", CancellationToken ct = default) =>
        new UpnpControlClient(this).PlayUriAsync(uri, metadata, ct);

    public Task<bool> StopAsync(CancellationToken ct = default) =>
        new UpnpControlClient(this).StopAsync(ct);

    public Task<bool> SetVolumeAsync(int volume, CancellationToken ct = default) =>
        new UpnpControlClient(this).SetVolumeAsync(volume, ct);

    public Task<string?> GetStateAsync(CancellationToken ct = default) =>
        new UpnpControlClient(this).GetTransportStateAsync(ct);

    public override string ToString() =>
        string.IsNullOrEmpty(FriendlyName) ? Location : $"{FriendlyName} ({Manufacturer} {ModelName})".Trim();
}
