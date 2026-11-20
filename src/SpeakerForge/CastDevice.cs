using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// A Google Cast device (Chromecast, Nest, Cast-enabled speaker) exposed as an
/// <see cref="INetworkRenderer"/>. Wraps <see cref="CastClient"/> - the Cast
/// device pulls the same WAV stream URL our embedded server hosts.
/// </summary>
public sealed class CastDevice : INetworkRenderer
{
    private readonly CastClient _client;

    public CastDevice(CastDiscovery.CastInfo info)
    {
        Id = info.Id;
        FriendlyName = info.FriendlyName;
        Manufacturer = "Google";
        ModelName = info.ModelName;
        Address = $"{info.Host}:{info.Port}";
        Protocol = "Google Cast";
        _client = new CastClient(info.Host, info.Port == 0 ? 8009 : info.Port);
    }

    public string Id { get; }
    public string FriendlyName { get; }
    public string Manufacturer { get; }
    public string ModelName { get; }
    public string Address { get; }
    public string Protocol { get; }

    public Task<bool> PlayUriAsync(string uri, string metadata = "", CancellationToken ct = default) =>
        _client.PlayUriAsync(uri, "audio/wav", liveStream: true, ct);

    public Task<bool> StopAsync(CancellationToken ct = default) => _client.StopAsync(ct);

    public Task<bool> SetVolumeAsync(int volume, CancellationToken ct = default) =>
        _client.SetVolumeAsync(volume, ct);

    public Task<string?> GetStateAsync(CancellationToken ct = default) => _client.GetStateAsync(ct);
}
