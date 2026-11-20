using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakerForge;

/// <summary>
/// Minimal Google Cast v2 client: TLS to port 8009, length-prefixed protobuf
/// CastMessages, JSON payloads on the standard namespaces. Supports the
/// default media receiver: LAUNCH -> LOAD/PLAY/STOP + receiver volume.
/// </summary>
public sealed class CastClient : IAsyncDisposable
{
    private const string NsConnection = "urn:x-cast:com.google.cast.tp.connection";
    private const string NsHeartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    private const string NsReceiver = "urn:x-cast:com.google.cast.receiver";
    private const string NsMedia = "urn:x-cast:com.google.cast.media";
    private const string DefaultMediaReceiverAppId = "CC1AD845";

    private readonly string _host;
    private readonly int _port;
    private readonly string _senderId = "sender-af" + Guid.NewGuid().ToString("N")[..6];

    private TcpClient? _tcp;
    private SslStream? _ssl;
    private SemaphoreSlim _writeLock = new(1, 1);
    private string? _sessionId;
    private Timer? _heartbeat;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private int _requestId;

    public CastClient(string host, int port = 8009) { _host = host; _port = port; }

    public bool IsConnected => _ssl is not null;

    // ---------------- connection ----------------

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;
        _tcp?.Dispose();
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_host, _port, ct);
        _ssl = new SslStream(_tcp.GetStream(), false,
            (_, _, _, _) => true); // Cast uses self-signed certs
        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _host,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                | System.Security.Authentication.SslProtocols.Tls13
        }, ct);

        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

        // virtual connection
        await SendAsync(NsConnection, "receiver-0", "{\"type\":\"CONNECT\"}", ct);
        _heartbeat = new Timer(_ =>
        {
            try { _ = SendAsync(NsHeartbeat, "receiver-0", "{\"type\":\"PING\"}"); } catch { }
        }, null, 5000, 5000);

        // launch default media receiver, grab session id
        var status = await RequestAsync(NsReceiver, "receiver-0",
            "{\"type\":\"LAUNCH\",\"appId\":\"" + DefaultMediaReceiverAppId + "\"}", ct);
        _sessionId = ExtractSessionId(status) ?? "session-0";
    }

    // ---------------- media ----------------

    public async Task<bool> PlayUriAsync(string uri, string contentType = "audio/wav",
        bool liveStream = true, CancellationToken ct = default)
    {
        await ConnectAsync(ct);
        var dest = _sessionId ?? "receiver-0";
        var loadJson = "{\"type\":\"LOAD\",\"requestId\":" + NextId() + "," +
            "\"media\":{\"contentId\":\"" + JsonEscape(uri) + "\"," +
            "\"contentType\":\"" + contentType + "\"," +
            "\"streamType\":\"" + (liveStream ? "LIVE" : "BUFFERED") + "\"," +
            "\"metadata\":{}},\"autoplay\":true,\"currentTime\":0}";
        await SendAsync(NsMedia, dest, loadJson, ct);
        return true;
    }

    public async Task<bool> StopAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return true;
        await SendAsync(NsMedia, _sessionId ?? "receiver-0",
            "{\"type\":\"STOP\",\"requestId\":" + NextId() + "}", ct);
        return true;
    }

    public async Task<bool> SetVolumeAsync(int volume01to100, CancellationToken ct = default)
    {
        await ConnectAsync(ct);
        double level = Math.Clamp(volume01to100, 0, 100) / 100.0;
        await SendAsync(NsReceiver, "receiver-0",
            "{\"type\":\"SET_VOLUME\",\"requestId\":" + NextId() +
            ",\"volume\":{\"level\":" + level.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}", ct);
        return true;
    }

    public async Task<string?> GetStateAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return null;
        await SendAsync(NsMedia, _sessionId ?? "receiver-0",
            "{\"type\":\"GET_STATUS\",\"requestId\":" + NextId() + "}", ct);
        return "QUERY_SENT"; // media status arrives async; UI shows listeners anyway
    }

    // ---------------- wire protocol ----------------

    private int NextId() => Interlocked.Increment(ref _requestId);

    private async Task<string?> RequestAsync(string ns, string dest, string json, CancellationToken ct)
    {
        // fire and forget for v1 - status arrives on read loop; small delay lets
        // the LAUNCH response land before we need sessionId (we read it lazily
        // from a cached status below).
        await SendAsync(ns, dest, json, ct);
        await Task.Delay(400, ct);
        return _lastReceiverStatus;
    }

    private string? _lastReceiverStatus;

    private static string? ExtractSessionId(string? statusJson)
    {
        if (statusJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            if (doc.RootElement.TryGetProperty("applications", out var apps) && apps.GetArrayLength() > 0)
            {
                var app = apps[0];
                if (app.TryGetProperty("sessionId", out var sid)) return sid.GetString();
                if (app.TryGetProperty("appId", out var aid) && aid.GetString() == DefaultMediaReceiverAppId)
                    return app.TryGetProperty("sessionId", out var s2) ? s2.GetString() : null;
            }
        }
        catch { }
        return null;
    }

    private async Task SendAsync(string ns, string dest, string json, CancellationToken ct = default)
    {
        var ssl = _ssl ?? throw new InvalidOperationException("not connected");
        var msg = EncodeCastMessage(ns, dest, json);
        var frame = new byte[4 + msg.Length];
        frame[0] = (byte)(msg.Length >> 24); frame[1] = (byte)(msg.Length >> 16);
        frame[2] = (byte)(msg.Length >> 8); frame[3] = (byte)msg.Length;
        msg.CopyTo(frame, 4);
        await _writeLock.WaitAsync(ct);
        try { await ssl.WriteAsync(frame, ct); }
        finally { _writeLock.Release(); }
    }

    private byte[] EncodeCastMessage(string ns, string dest, string json)
    {
        var ms = new MemoryStream();
        // protobuf: field1 varint(0), field2 src, field3 dest, field4 ns,
        // field5 varint(0=STRING), field6 payload
        WriteVarint(ms, (0 << 3) | 0); // field1 wire0 value0 -> tag byte 0x08
        // fix: write tag+value properly below
        ms.SetLength(0);
        WriteTag(ms, 1, 0); WriteVarint(ms, 0);
        WriteTag(ms, 2, 2); WriteLenDelim(ms, Encoding.UTF8.GetBytes(_senderId));
        WriteTag(ms, 3, 2); WriteLenDelim(ms, Encoding.UTF8.GetBytes(dest));
        WriteTag(ms, 4, 2); WriteLenDelim(ms, Encoding.UTF8.GetBytes(ns));
        WriteTag(ms, 5, 0); WriteVarint(ms, 0);
        WriteTag(ms, 6, 2); WriteLenDelim(ms, Encoding.UTF8.GetBytes(json));
        return ms.ToArray();
    }

    private static void WriteTag(Stream s, int field, int wire) =>
        WriteVarint(s, (ulong)((field << 3) | wire));

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static void WriteLenDelim(Stream s, byte[] data)
    {
        WriteVarint(s, (ulong)data.Length);
        s.Write(data, 0, data.Length);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var ssl = _ssl!;
        var lenBuf = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(ssl, lenBuf, ct);
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (len <= 0 || len > 4 * 1024 * 1024) break;
                var payload = new byte[len];
                await ReadExactAsync(ssl, payload, ct);
                var (ns, payloadJson) = DecodeCastMessage(payload);
                if (payloadJson is null) continue;

                // auto-PONG heartbeats
                if (ns == NsHeartbeat && payloadJson.Contains("\"PING\""))
                    _ = SendAsync(NsHeartbeat, "receiver-0", "{\"type\":\"PONG\"}");
                else if (ns == NsReceiver && payloadJson.Contains("RECEIVER_STATUS"))
                    _lastReceiverStatus = payloadJson;
            }
        }
        catch { /* connection closed */ }
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off), ct);
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
    }

    private static (string? ns, string? json) DecodeCastMessage(byte[] data)
    {
        int pos = 0;
        string? ns = null, json = null;
        while (pos < data.Length)
        {
            ulong tag = ReadVarint(data, ref pos);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: ReadVarint(data, ref pos); break;
                case 2:
                    {
                        ulong len = ReadVarint(data, ref pos);
                        var bytes = new byte[len];
                        Array.Copy(data, pos, bytes, 0, (int)len);
                        pos += (int)len;
                        if (field == 4) ns = Encoding.UTF8.GetString(bytes);
                        else if (field == 6) json = Encoding.UTF8.GetString(bytes);
                        break;
                    }
                case 5: pos += 4; break;
                case 1: pos += 8; break;
                default: return (ns, json);
            }
        }
        return (ns, json);
    }

    private static ulong ReadVarint(byte[] d, ref int pos)
    {
        ulong v = 0; int shift = 0;
        while (pos < d.Length)
        {
            byte b = d[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _heartbeat?.Dispose(); } catch { }
        try { if (_ssl is not null) await _ssl.DisposeAsync(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }
}
