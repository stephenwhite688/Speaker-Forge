using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SpeakerForge;

/// <summary>
/// Minimal UPnP/SOAP control client for AVTransport and RenderingControl
/// services. Each call is a synchronous-style SOAP 1.1 action over HTTP POST.
/// </summary>
public sealed class UpnpControlClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(6) };
    private readonly UpnpDevice _device;

    public UpnpControlClient(UpnpDevice device) => _device = device;

    public UpnpDevice Device => _device;

    // ---------- AVTransport ----------

    /// <summary>Sets the URI the renderer should play and starts playback.</summary>
    public async Task<bool> PlayUriAsync(string uri, string metadata = "", CancellationToken ct = default)
    {
        if (_device.AvTransportControlUrl is null || _device.AvTransportServiceType is null) return false;
        var set = await InvokeAsync(_device.AvTransportServiceType, "SetAVTransportURI", _device.AvTransportControlUrl, ct,
            ("InstanceID", "0"), ("CurrentURI", uri), ("CurrentURIMetaData", metadata));
        if (set is null) return false;
        var play = await InvokeAsync(_device.AvTransportServiceType, "Play", _device.AvTransportControlUrl, ct,
            ("InstanceID", "0"), ("Speed", "1"));
        return play is not null;
    }

    public Task<bool> PlayAsync(CancellationToken ct = default) =>
        InvokeBoolAsync(_device.AvTransportServiceType, "Play", _device.AvTransportControlUrl, ct,
            ("InstanceID", "0"), ("Speed", "1"));

    public Task<bool> PauseAsync(CancellationToken ct = default) =>
        InvokeBoolAsync(_device.AvTransportServiceType, "Pause", _device.AvTransportControlUrl, ct,
            ("InstanceID", "0"));

    public Task<bool> StopAsync(CancellationToken ct = default) =>
        InvokeBoolAsync(_device.AvTransportServiceType, "Stop", _device.AvTransportControlUrl, ct,
            ("InstanceID", "0"));

    /// <summary>Gets transport state: PLAYING / PAUSED / STOPPED / TRANSITIONING.</summary>
    public async Task<string?> GetTransportStateAsync(CancellationToken ct = default)
    {
        var res = await InvokeAsync(_device.AvTransportServiceType, "GetTransportInfo", _device.AvTransportControlUrl, ct,
            ("InstanceID", "0"));
        return res is not null && res.TryGetValue("CurrentTransportState", out var s) ? s : null;
    }

    // ---------- RenderingControl ----------

    /// <summary>Sets master volume 0-100.</summary>
    public Task<bool> SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        volume = Math.Clamp(volume, 0, 100);
        return InvokeBoolAsync(_device.RenderingControlServiceType, "SetVolume", _device.RenderingControlUrl, ct,
            ("InstanceID", "0"), ("Channel", "Master"), ("DesiredVolume", volume.ToString()));
    }

    public async Task<int?> GetVolumeAsync(CancellationToken ct = default)
    {
        var res = await InvokeAsync(_device.RenderingControlServiceType, "GetVolume", _device.RenderingControlUrl, ct,
            ("InstanceID", "0"), ("Channel", "Master"));
        if (res is null || !res.TryGetValue("CurrentVolume", out var s)) return null;
        return int.TryParse(s, out var v) ? v : null;
    }

    private async Task<bool> InvokeBoolAsync(string? serviceType, string action, string? url, CancellationToken ct,
        params (string Name, string Value)[] args)
        => await InvokeAsync(serviceType, action, url, ct, args) is not null;

    private async Task<Dictionary<string, string>?> InvokeAsync(string? serviceType, string action, string? url, CancellationToken ct,
        params (string Name, string Value)[] args)
    {
        if (serviceType is null || url is null) return null;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        sb.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        sb.Append("<s:Body>");
        sb.Append($"<u:{action} xmlns:u=\"{serviceType}\">");
        foreach (var (name, value) in args)
            sb.Append($"<{name}>{Escape(value)}</{name}>");
        sb.Append($"</u:{action}>");
        sb.Append("</s:Body></s:Envelope>");

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml")
        };
        req.Headers.Add("SOAPACTION", $"\"{serviceType}#{action}\"");

        using var res = await Http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return null;

        var doc = XDocument.Parse(body);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bodyEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body");
        if (bodyEl is null) return result;
        foreach (var el in bodyEl.Descendants())
        {
            if (el.HasElements) continue;
            result[el.Name.LocalName] = el.Value;
        }
        return result;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
