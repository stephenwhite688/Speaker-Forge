using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// Minimal AirPlay 1 / RAOP sender. RTSP control channel to the device,
/// unicast UDP/RTP L16 audio pushed in real time. Reads PCM from the same
/// embedded HTTP WAV stream the UPnP path uses - no Helix coupling.
///
/// Known limitations: no AirPlay 2 devices (HomePod, newer Apple TV),
/// no pairing/encryption (devices that require a pairing PIN won't work),
/// no receiver clock-drift correction beyond local pacing.
/// </summary>
public sealed class RaopClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _deviceId;

    private TcpClient? _rtsp;
    private NetworkStream? _ns;
    private UdpClient? _rtp;
    private UdpClient? _rtcp;
    private int _rtpPort, _rtcpPort;      // our local ports (client_port)
    private int _serverRtpPort, _serverRtcpPort; // where the device wants media
    private string? _session;
    private int _cseq;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private bool _playing;

    // RTP state
    private ushort _seq;
    private uint _rtpTimestamp;
    private uint _ssrc;
    private int _sampleRate = 44100;
    private int _channels = 2;

    private static readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10) };

    public RaopClient(string host, int port, string deviceId = "")
    { _host = host; _port = port == 0 ? 5000 : port; _deviceId = deviceId; }

    // ---------------- RTSP ----------------

    private async Task<string> RtspAsync(string method, string url, string headers = "", CancellationToken ct = default)
    {
        if (_ns is null) throw new InvalidOperationException("not connected");
        int cseq = Interlocked.Increment(ref _cseq);
        var sb = new StringBuilder();
        sb.Append($"{method} {url} RTSP/1.0\r\nCSeq: {cseq}\r\nUser-Agent: AudioForge/1.0\r\n");
        if (_session is not null) sb.Append($"Session: {_session}\r\n");
        if (headers.Length > 0) sb.Append(headers).Append("\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await _ns.WriteAsync(bytes, ct);

        // read response headers (body is rare for our verbs; read generously)
        var buf = new byte[8192];
        var ms = new MemoryStream();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await _ns.ReadAsync(buf, ct);
            if (n <= 0) throw new EndOfStreamException("RTSP closed");
            ms.Write(buf, 0, n);
            var arr = ms.GetBuffer();
            var len = (int)ms.Length;
            for (int i = 0; i + 3 < len; i++)
                if (arr[i] == '\r' && arr[i + 1] == '\n' && arr[i + 2] == '\r' && arr[i + 3] == '\n')
                { headerEnd = i + 4; break; }
        }
        return Encoding.ASCII.GetString(ms.GetBuffer(), 0, headerEnd);
    }

    private static string Header(string response, string name)
    {
        foreach (var line in response.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            var idx = t.IndexOf(':');
            if (idx > 0 && t[..idx].Equals(name, StringComparison.OrdinalIgnoreCase))
                return t[(idx + 1)..].Trim();
        }
        return "";
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_ns is not null) return;
        _rtsp?.Dispose();
        _rtsp = new TcpClient { NoDelay = true };
        await _rtsp.ConnectAsync(_host, _port, ct);
        _ns = _rtsp.GetStream();
        _cseq = 0;

        // POST /info - device info; some devices require it before SETUP
        try { await RtspAsync("POST", "rtsp://" + _host + "/info", "Content-Length: 0\r\n", ct); } catch { }

        // pick client ports for RTCP reception; audio RTP goes to server ports
        _rtp = new UdpClient(AddressFamily.InterNetwork);
        _rtcp = new UdpClient(AddressFamily.InterNetwork);
        _rtpPort = ((IPEndPoint)_rtp.Client.LocalEndPoint!).Port;
        _rtcpPort = ((IPEndPoint)_rtcp.Client.LocalEndPoint!).Port;
        if (_rtpPort == _rtcpPort) { _rtcp = new UdpClient(AddressFamily.InterNetwork); _rtcpPort = ((IPEndPoint)_rtcp.Client.LocalEndPoint!).Port; }

        var setup = await RtspAsync("SETUP", "rtsp://" + _host + "/",
            $"Transport: RTP/AVP/UDP;unicast;client_port={_rtpPort}-{_rtcpPort};mode=\"RECORD\";timing-protocol=NTP\r\n",
            ct);
        _session = Header(setup, "Session");
        if (_session.Contains(';')) _session = _session[.._session.IndexOf(';')];

        // response Transport header carries the ports the device listens on
        var transport = Header(setup, "Transport");
        int sp = ExtractPort(transport, "server_port");
        if (sp > 0) _serverRtpPort = sp;
        else _serverRtpPort = _rtpPort; // some devices echo client ports
        _serverRtcpPort = ExtractPort(transport, "server_port", 1) is int p && p > 0 ? p : _serverRtpPort + 1;
        if (_serverRtpPort == 0) _serverRtpPort = 6000;
        if (_serverRtcpPort == 0) _serverRtcpPort = 6001;
    }

    private static int ExtractPort(string transport, string key, int occurrence = 0)
    {
        int idx = -1;
        for (int i = 0; i <= occurrence; i++)
        {
            idx = transport.IndexOf(key + "=", idx + 1, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;
        }
        idx += key.Length + 1;
        int dash = transport.IndexOf('-', idx);
        var first = transport[idx..(dash > idx ? dash : transport.Length)];
        return int.TryParse(first.Trim(), out var p) ? p : 0;
    }

    // ---------------- playback ----------------

    public async Task<bool> PlayUriAsync(string wavUrl, CancellationToken ct = default)
    {
        await StopAsync(ct);
        await ConnectAsync(ct);

        // RECORD with NTP start range
        var ntp = ToNtp(DateTime.UtcNow);
        try
        {
            await RtspAsync("RECORD", "rtsp://" + _host + "/",
                $"Range: ntp={ntp}-\r\nRTP-Info: seq={_seq};rtptime={_rtpTimestamp}\r\n", ct);
        }
        catch { /* some devices accept anyway */ }

        _playing = true;
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _streamTask = Task.Run(() => StreamLoopAsync(wavUrl, _sampleRate, _channels, _streamCts.Token));
        return true;
    }

    public async Task<bool> StopAsync(CancellationToken ct = default)
    {
        _playing = false;
        try { _streamCts?.Cancel(); } catch { }
        if (_streamTask is not null) { try { await _streamTask; } catch { } _streamTask = null; }
        if (_ns is not null && _session is not null)
        {
            try
            {
                await RtspAsync("FLUSH", "rtsp://" + _host + "/",
                    $"RTP-Info: seq={_seq};rtptime={_rtpTimestamp}\r\n", ct);
            }
            catch { }
        }
        return true;
    }

    public async Task<bool> SetVolumeAsync(int volume01to100, CancellationToken ct = default)
    {
        // AirPlay 1 has no standard volume RTSP verb - volume is handled by
        // the audio level encoded? RAOP devices take volume via 'volume' SET_PARAMETER.
        if (_ns is null || _session is null) return false;
        try
        {
            await RtspAsync("SET_PARAMETER", "rtsp://" + _host + "/",
                "Content-Type: text/parameters\r\nContent-Length: 16\r\n\r\nvolume: " +
                (volume01to100 / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), ct);
        }
        catch { }
        return true;
    }

    public Task<string?> GetStateAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(_playing ? "PLAYING" : (_ns is not null ? "READY" : null));

    // ---------------- audio pump ----------------

    private async Task StreamLoopAsync(string url, int rate, int channels, CancellationToken ct)
    {
        // fetch WAV stream from our own server, packetize L16 -> RTP
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var net = await resp.Content.ReadAsStreamAsync(ct);
        var br = new BinaryReader(net);

        // parse WAV header: fmt chunk (rate/channels); stream is positioned at
        // the data chunk when this returns true (non-seekable HTTP stream)
        var (found, wavRate, wavChannels) = await ReadWavInfoAsync(br, ct);
        if (!found) return;
        _sampleRate = wavRate; _channels = wavChannels;
        int frameBytes = wavChannels * 2;
        int samplesPerPacket = Math.Max(1, wavRate / 100);       // ~10ms
        int payloadBytes = samplesPerPacket * frameBytes;
        var payload = new byte[payloadBytes];
        var rtp = new byte[12 + payloadBytes];
        rtp[0] = 0x80; rtp[1] = 96;                              // V=2, PT=96 (L16)
        _ssrc = (uint)Random.Shared.Next();
        _rtpTimestamp = 0; _seq = (ushort)Random.Shared.Next(0, 60000);

        var dest = new IPEndPoint(IPAddress.Parse(_host), _serverRtpPort);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long bytesSent = 0;
        double nextSendMs = 0;

        try
        {
            while (!ct.IsCancellationRequested && _playing)
            {
                int read = 0;
                while (read < payloadBytes)
                {
                    int n = await net.ReadAsync(payload.AsMemory(read, payloadBytes - read), ct);
                    if (n <= 0) { _playing = false; return; }       // stream ended
                    read += n;
                }

                // RTP header
                rtp[2] = (byte)(_seq >> 8); rtp[3] = (byte)_seq;
                uint ts = _rtpTimestamp;
                rtp[4] = (byte)(ts >> 24); rtp[5] = (byte)(ts >> 16);
                rtp[6] = (byte)(ts >> 8); rtp[7] = (byte)ts;
                rtp[8] = (byte)(_ssrc >> 24); rtp[9] = (byte)(_ssrc >> 16);
                rtp[10] = (byte)(_ssrc >> 8); rtp[11] = (byte)_ssrc;
                Buffer.BlockCopy(payload, 0, rtp, 12, payloadBytes);
                await _rtp!.SendAsync(rtp, dest, ct);

                _seq++;
                _rtpTimestamp += (uint)samplesPerPacket;
                bytesSent += payloadBytes;

                // pace at real time: samplesPerPacket/rate seconds per packet
                nextSendMs += samplesPerPacket * 1000.0 / rate;
                double wait = nextSendMs - clock.ElapsedMilliseconds;
                if (wait > 1) await Task.Delay((int)wait, ct);
                else if (wait < -500) { clock.Restart(); nextSendMs = 0; } // fell behind - resync
            }
        }
        catch (OperationCanceledException) { }
        catch { _playing = false; }
    }

    private static async Task<(bool found, int rate, int channels)> ReadWavInfoAsync(BinaryReader br, CancellationToken ct)
    {
        // RIFF/WAVE: scan chunks; capture fmt (rate/channels). Leaves the
        // stream positioned at the first data byte when 'data' is found.
        var riff = new byte[12];
        if (await br.BaseStream.ReadAsync(riff, ct) < 12) return (false, 0, 0);
        int rate = 0, channels = 0;
        var skipBuf = new byte[8192];
        while (true)
        {
            var ch = new byte[8];
            if (await br.BaseStream.ReadAsync(ch, ct) < 8) break;
            uint size = BitConverter.ToUInt32(ch, 4);
            if (ch[0] == 'f' && ch[1] == 'm' && ch[2] == 't' && ch[3] == ' ')
            {
                var fmt = new byte[Math.Min(size, 16)];
                int got = await br.BaseStream.ReadAsync(fmt, ct);
                if (got >= 8)
                {
                    channels = BitConverter.ToUInt16(fmt, 2);
                    rate = (int)BitConverter.ToUInt32(fmt, 4);
                }
                await SkipAsync(br.BaseStream, size - (uint)got + (size & 1), skipBuf, ct);
            }
            else if (ch[0] == 'd' && ch[1] == 'a' && ch[2] == 't' && ch[3] == 'a')
            {
                return (true, rate == 0 ? 44100 : rate, channels == 0 ? 2 : channels);
            }
            else
            {
                await SkipAsync(br.BaseStream, size + (size & 1), skipBuf, ct);
            }
        }
        return (false, 0, 0);
    }

    private static async Task SkipAsync(Stream s, long count, byte[] buf, CancellationToken ct)
    {
        while (count > 0)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)), ct);
            if (n <= 0) return;
            count -= n;
        }
    }

    private static string ToNtp(DateTime utc)
    {
        var span = (utc - new DateTime(1900, 1, 1));
        ulong ntpSec = (ulong)span.TotalSeconds;
        double f = span.TotalSeconds - Math.Floor(span.TotalSeconds);
        ulong frac32 = (ulong)(f * 4294967296.0);
        return $"{ntpSec}-{frac32}";
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { }
        try { _rtsp?.Dispose(); } catch { }
        try { _rtp?.Dispose(); } catch { }
        try { _rtcp?.Dispose(); } catch { }
    }
}
