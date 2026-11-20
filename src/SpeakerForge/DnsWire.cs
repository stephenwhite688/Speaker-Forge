using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace SpeakerForge;

/// <summary>
/// Minimal DNS-SD/mDNS wire helpers shared by Cast and AirPlay discovery:
/// builds PTR queries and parses PTR/SRV/TXT/A answers (with name compression).
/// </summary>
internal static class DnsWire
{
    public sealed class Answers
    {
        public readonly Dictionary<string, string> Ptrs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (string Target, int Port)> Srvs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Dictionary<string, string>> Txts = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, IPAddress> Addrs = new(StringComparer.OrdinalIgnoreCase);
    }

    public static byte[] BuildQuery(string name, string type)
    {
        var ms = new System.IO.MemoryStream();
        var bw = new System.IO.BinaryWriter(ms);
        ushort id = (ushort)Random.Shared.Next(0, 65535);
        bw.Write(new byte[] { (byte)(id >> 8), (byte)id });
        bw.Write(new byte[] { 0x01, 0x00 }); // flags: RD
        bw.Write(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 }); // qd=1 an=ns=ar=0
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            bw.Write((byte)label.Length);
            bw.Write(Encoding.ASCII.GetBytes(label));
        }
        bw.Write((byte)0);
        ushort t = (ushort)(type == "PTR" ? 12 : type == "SRV" ? 33 : type == "TXT" ? 16 : 1);
        bw.Write(new byte[] { (byte)(t >> 8), (byte)t });
        bw.Write(new byte[] { 0, 1 }); // IN
        return ms.ToArray();
    }

    public static void ParsePacket(byte[] data, Answers a)
    {
        if (data.Length < 12) return;
        int pos = 12;
        int qd = BE16(data, 4), an = BE16(data, 6);
        for (int i = 0; i < qd; i++) { pos = SkipName(data, pos); pos += 4; }
        for (int i = 0; i < an; i++)
        {
            string name = ReadName(data, ref pos);
            int type = BE16(data, pos); pos += 2;
            pos += 2; // class
            pos += 4; // ttl
            int rdlen = BE16(data, pos); pos += 2;
            int rdataStart = pos;

            switch (type)
            {
                case 12:
                    {
                        string target = ReadName(data, ref pos);
                        a.Ptrs[name] = target;
                        break;
                    }
                case 33:
                    {
                        int port = BE16(data, rdataStart + 4);
                        int tp = rdataStart + 6;
                        string target = ReadName(data, ref tp);
                        a.Srvs[name.TrimEnd('.')] = (target, port);
                        break;
                    }
                case 16:
                    {
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        int end = rdataStart + rdlen, p = rdataStart;
                        while (p < end)
                        {
                            int len = data[p++];
                            if (len == 0 || p + len > end) break;
                            var s = Encoding.UTF8.GetString(data, p, len);
                            p += len;
                            var eq = s.IndexOf('=');
                            if (eq > 0) map[s[..eq]] = s[(eq + 1)..];
                            else map[s] = "";
                        }
                        a.Txts[name.TrimEnd('.')] = map;
                        break;
                    }
                case 1:
                    {
                        if (rdlen == 4)
                            a.Addrs[name.TrimEnd('.')] = new IPAddress(new ReadOnlySpan<byte>(data, rdataStart, 4));
                        break;
                    }
            }
            pos = rdataStart + rdlen;
        }
    }

    private static int SkipName(byte[] d, int pos)
    {
        while (pos < d.Length)
        {
            int len = d[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2;
            pos += 1 + len;
        }
        return pos;
    }

    private static string ReadName(byte[] d, ref int pos)
    {
        var labels = new List<string>();
        int guard = 0;
        while (pos < d.Length && guard++ < 128)
        {
            int len = d[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                int ptr = ((len & 0x3F) << 8) | d[pos + 1];
                pos += 2;
                pos = ptr;
                continue;
            }
            pos++;
            labels.Add(Encoding.ASCII.GetString(d, pos, len));
            pos += len;
        }
        return string.Join('.', labels);
    }

    private static int BE16(byte[] d, int pos) => (d[pos] << 8) | d[pos + 1];
}
