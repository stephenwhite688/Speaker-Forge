using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// Embedded PCM audio server. The host application pushes processed
/// interleaved float PCM via <see cref="Broadcast"/>; each connected renderer
/// pulls a stream over HTTP GET /stream with per-client channel selection and
/// gain, so one source can feed stereo pairs and multi-room groups.
///
/// Raw TcpListener is used instead of HttpListener to avoid http.sys URL-ACL
/// requirements for non-localhost binds.
/// </summary>
public sealed class NetworkAudioServer : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ClientSession> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>Port the server listens on (0 = auto-assign on start).</summary>
    public int Port { get; }

    /// <summary>Sample rate of the PCM pushed via <see cref="Broadcast"/>.</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>Channel count of the PCM pushed via <see cref="Broadcast"/>.</summary>
    public int SourceChannels { get; set; } = 2;

    /// <summary>Frames per broadcast chunk hint (used for silence on underrun).</summary>
    public int ChunkFrames { get; set; } = 1024;

    public NetworkAudioServer(int port = 8977) => Port = port;

    /// <summary>TCP port actually bound (after <see cref="Start"/>).</summary>
    public int BoundPort { get; private set; }

    /// <summary>Number of renderer clients currently pulling audio.</summary>
    public int ConnectedClients { get { lock (_gate) return _clients.Count; } }

    /// <summary>Base HTTP URL renderers use, e.g. http://192.168.1.10:8977</summary>
    public string? LocalUrl
    {
        get
        {
            var ip = GetLocalIp();
            return ip is null ? null : $"http://{ip}:{BoundPort}";
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_listener is not null) return;
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        try { cts?.Cancel(); } catch { }
    }

    /// <summary>
    /// Pushes one block of interleaved float PCM to every connected client.
    /// Safe to call from the realtime feed thread; non-blocking per client
    /// (slow clients drop oldest chunks rather than stall the source).
    /// </summary>
    public void Broadcast(float[] interleaved, int frames)
    {
        if (interleaved is null || frames <= 0) return;
        List<ClientSession> snapshot;
        lock (_gate) snapshot = _clients.ToList();
        foreach (var c in snapshot) c.TryEnqueue(interleaved, frames);
    }

    /// <summary>Builds the stream URL for a speaker role.</summary>
    /// <param name="role">"stereo", "left", or "right".</param>
    /// <param name="gain">Linear gain applied to this speaker (1.0 = unity).</param>
    public string BuildStreamUrl(string role, double gain = 1.0)
    {
        var baseUrl = LocalUrl ?? $"http://127.0.0.1:{BoundPort}";
        return $"{baseUrl}/stream?ch={Uri.EscapeDataString(role)}&gain={gain.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            var session = new ClientSession(tcp, this);
            lock (_gate) _clients.Add(session);
            _ = Task.Run(() => session.RunAsync(ct));
        }
    }

    private void RemoveClient(ClientSession session)
    {
        lock (_gate) _clients.Remove(session);
        session.Dispose();
    }

    private static IPAddress? GetLocalIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                        return addr.Address;
                }
            }
        }
        catch { }
        return null;
    }

    // ---------------- per-client session ----------------

    private sealed class ClientSession : IDisposable
    {
        private const int QueueCapacity = 128;
        private readonly TcpClient _tcp;
        private readonly NetworkAudioServer _server;
        private readonly Channel<(float[] Data, int Frames)> _queue =
            Channel.CreateBounded<(float[], int)>(new BoundedChannelOptions(QueueCapacity)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        private int _lastFrames = 1024;
        private bool _disposed;

        public ClientSession(TcpClient tcp, NetworkAudioServer server)
        {
            _tcp = tcp;
            _server = server;
        }

        public void TryEnqueue(float[] data, int frames)
        {
            _lastFrames = frames;
            // copy: caller reuses the buffer
            var copy = new float[data.Length];
            Array.Copy(data, copy, data.Length);
            _queue.Writer.TryWrite((copy, frames));
        }

        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                var stream = _tcp.GetStream();
                stream.ReadTimeout = 10000;

                // Read HTTP request headers (single GET, no body expected)
                var headerBuf = new byte[8192];
                int total = 0, read;
                string request = "";
                while (total < headerBuf.Length &&
                       (read = await stream.ReadAsync(headerBuf.AsMemory(total, headerBuf.Length - total), ct)) > 0)
                {
                    total += read;
                    request = Encoding.ASCII.GetString(headerBuf, 0, total);
                    if (request.Contains("\r\n\r\n")) break;
                }

                var firstLine = request.Split('\n')[0].Trim();
                // GET /stream?ch=left&gain=0.8 HTTP/1.1
                var path = firstLine.Split(' ').Length > 1 ? firstLine.Split(' ')[1] : "/";
                var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";
                var args = ParseQuery(query);
                string role = args.TryGetValue("ch", out var r) ? r : "stereo";
                double gain = args.TryGetValue("gain", out var g) && double.TryParse(g, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var gv) ? gv : 1.0;

                if (!path.StartsWith("/stream"))
                {
                    var notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(notFound, ct);
                    return;
                }

                int srcCh = Math.Max(1, _server.SourceChannels);
                int outCh = role is "left" or "right" ? 1 : srcCh;

                // WAV-framed stream: virtually every DLNA renderer (Sonos
                // included) accepts audio/x-wav, while raw audio/L16 is often
                // rejected outright. Sizes are 0xFFFFFFFF = "unknown/live".
                var headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: audio/x-wav\r\n" +
                    $"Cache-Control: no-cache\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(headers, ct);
                await stream.WriteAsync(BuildWavHeader(_server.SampleRate, outCh, 16), ct);

                var pcm = new byte[0];
                var silenceFrames = _server.ChunkFrames;

                while (!ct.IsCancellationRequested && !_disposed)
                {
                    float[] data = Array.Empty<float>();
                    int frames = 0;
                    try
                    {
                        (data, frames) = await _queue.Reader.ReadAsync(ct).AsTask()
                            .WaitAsync(TimeSpan.FromMilliseconds(40), ct)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // underrun - emit silence to keep the renderer fed
                        frames = silenceFrames;
                        data = Array.Empty<float>();
                    }
                    catch { break; }

                    int samples = frames * outCh;
                    if (pcm.Length < samples * 2) pcm = new byte[samples * 2];
                    int pos = 0;

                    for (int i = 0; i < frames; i++)
                    {
                        for (int oc = 0; oc < outCh; oc++)
                        {
                            float s = 0f;
                            if (data.Length > 0)
                            {
                                int srcIdx;
                                if (role == "left") srcIdx = i * srcCh;                    // L
                                else if (role == "right") srcIdx = i * srcCh + Math.Min(1, srcCh - 1); // R
                                else srcIdx = i * srcCh + Math.Min(oc, srcCh - 1);          // stereo pass
                                s = data[srcIdx] * (float)gain;
                            }
                            s = Math.Clamp(s, -1f, 1f);
                            short v = (short)(s * 32767f);
                            pcm[pos++] = (byte)(v & 0xFF);
                            pcm[pos++] = (byte)(v >> 8);
                        }
                    }

                    await stream.WriteAsync(pcm.AsMemory(0, samples * 2), ct);
                }
            }
            catch { /* client went away or server stopping */ }
            finally
            {
                _server.RemoveClient(this);
            }
        }

        /// <summary>
        /// Canonical 44-byte PCM WAV header for a live stream: both size
        /// fields are 0xFFFFFFFF so renderers read until the connection ends.
        /// </summary>
        private static byte[] BuildWavHeader(int sampleRate, int channels, int bitsPerSample)
        {
            int blockAlign = channels * bitsPerSample / 8;
            int byteRate = sampleRate * blockAlign;
            var h = new byte[44];
            void U32(int off, uint v) { h[off] = (byte)v; h[off + 1] = (byte)(v >> 8); h[off + 2] = (byte)(v >> 16); h[off + 3] = (byte)(v >> 24); }
            void U16(int off, ushort v) { h[off] = (byte)v; h[off + 1] = (byte)(v >> 8); }
            Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
            U32(4, 0xFFFFFFFF);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(h, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(h, 12);
            U32(16, 16);          // fmt chunk size
            U16(20, 1);           // PCM
            U16(22, (ushort)channels);
            U32(24, (uint)sampleRate);
            U32(28, (uint)byteRate);
            U16(32, (ushort)blockAlign);
            U16(34, (ushort)bitsPerSample);
            Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
            U32(40, 0xFFFFFFFF);  // live/unknown length
            return h;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                map[kv[0]] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            }
            return map;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _tcp.Close(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
