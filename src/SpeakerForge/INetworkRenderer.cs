using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// A network speaker the engine can stream to, regardless of the underlying
/// protocol (UPnP/DLNA, Google Cast, AirPlay...). The UI only ever sees this
/// interface - protocol details stay inside each implementation.
/// </summary>
public interface INetworkRenderer
{
    /// <summary>Stable unique id (UPnP UDN, Cast UUID, etc.).</summary>
    string Id { get; }

    /// <summary>Human-friendly name shown in the UI.</summary>
    string FriendlyName { get; }

    /// <summary>Manufacturer name, if known.</summary>
    string Manufacturer { get; }

    /// <summary>Model name/description, if known.</summary>
    string ModelName { get; }

    /// <summary>Short address for display, e.g. "192.168.1.131:1400".</summary>
    string Address { get; }

    /// <summary>Which protocol drives this renderer (for diagnostics only).</summary>
    string Protocol { get; }

    /// <summary>Points the renderer at a stream URL and starts playback.</summary>
    Task<bool> PlayUriAsync(string uri, string metadata = "", CancellationToken ct = default);

    /// <summary>Stops playback on this renderer.</summary>
    Task<bool> StopAsync(CancellationToken ct = default);

    /// <summary>Sets output volume 0-100.</summary>
    Task<bool> SetVolumeAsync(int volume, CancellationToken ct = default);

    /// <summary>Queries transport state (PLAYING/STOPPED/...) if supported.</summary>
    Task<string?> GetStateAsync(CancellationToken ct = default);
}
