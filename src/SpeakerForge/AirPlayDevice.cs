using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// An AirPlay 1 / RAOP audio receiver exposed as an <see cref="INetworkRenderer"/>.
/// The device pulls nothing - we push L16 RTP audio to it, sourced from the
/// same embedded WAV stream the UPnP path uses.
/// </summary>
public sealed class AirPlayDevice : INetworkRenderer
{
    private readonly RaopClient _client;

    public AirPlayDevice(AirPlayDiscovery.AirPlayInfo info)
    {
        Id = info.DeviceId;
        FriendlyName = info.FriendlyName;
        Manufacturer = string.IsNullOrEmpty(info.Manufacturer) ? "Apple" : info.Manufacturer;
        ModelName = info.Model;
        Address = $"{info.Host}:{info.Port}";
        Protocol = "AirPlay";
        _client = new RaopClient(info.Host, info.Port, info.DeviceId);
    }

    public string Id { get; }
    public string FriendlyName { get; }
    public string Manufacturer { get; }
    public string ModelName { get; }
    public string Address { get; }
    public string Protocol { get; }

    public Task<bool> PlayUriAsync(string uri, string metadata = "", CancellationToken ct = default) =>
        _client.PlayUriAsync(uri, ct);

    public Task<bool> StopAsync(CancellationToken ct = default) => _client.StopAsync(ct);

    public Task<bool> SetVolumeAsync(int volume, CancellationToken ct = default) =>
        _client.SetVolumeAsync(volume, ct);

    public Task<string?> GetStateAsync(CancellationToken ct = default) => _client.GetStateAsync(ct);
}
