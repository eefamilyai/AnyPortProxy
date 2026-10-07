using System.Text;

namespace AnyPortProxy.Proxy;

public enum SniffStatus { NeedMore, Found, NotFound }

/// <summary>
/// Extracts the requested hostname from the first bytes a client sends:
/// TLS ClientHello (SNI), HTTP/1.x (Host header) or Minecraft Java handshake.
/// </summary>
public static class HostnameSniffer
{
    /// <summary>One full TLS record (16 KiB payload + 5 byte header).</summary>
    public const int MaxPeekBytes = 16384 + 5;

    public static SniffStatus Sniff(ReadOnlySpan<byte> d, out string? host, out string protocol)
    {
        host = null;
        protocol = "unknown";
        if (d.Length == 0) return SniffStatus.NeedMore;

        if (d[0] == 0x16)
        {
            protocol = "tls";
            return Tls(d, out host);
        }

        if (d.Length < 2) return SniffStatus.NeedMore;

        // Minecraft: VarInt length then packet id 0x00 (or a multi-byte VarInt length).
        if ((d[0] & 0x80) != 0 || d[1] == 0x00)
        {
            protocol = "minecraft";
            return Minecraft(d, out host);
        }

        if (d[0] is >= (byte)'A' and <= (byte)'Z')
        {
            protocol = "http";
            return Http(d, out host);
        }

        return SniffStatus.NotFound;
    }

    private static SniffStatus Tls(ReadOnlySpan<byte> d, out string? host)
    {
        host = null;
        if (d.Length < 5) return SniffStatus.NeedMore;
        int recLen = (d[3] << 8) | d[4];
        if (d[1] != 0x03 || recLen is < 4 or > 16384 + 256) return SniffStatus.NotFound; // not a sane TLS record: don't wait for more
        if (d.Length < 5 + recLen) return d.Length >= MaxPeekBytes ? SniffStatus.NotFound : SniffStatus.NeedMore;

        var r = d.Slice(5, recLen);
        int pos = 0;
        if (r.Length < 1 || r[0] != 0x01) return SniffStatus.NotFound; // ClientHello
        pos += 1 + 3;      // type + length
        pos += 2 + 32;     // legacy_version + random
        if (!Skip8(r, ref pos)) return SniffStatus.NotFound;   // session id
        if (!Skip16(r, ref pos)) return SniffStatus.NotFound;  // cipher suites
        if (!Skip8(r, ref pos)) return SniffStatus.NotFound;   // compression methods
        if (pos + 2 > r.Length) return SniffStatus.NotFound;
        int extEnd = Math.Min(pos + 2 + ((r[pos] << 8) | r[pos + 1]), r.Length);
        pos += 2;

        while (pos + 4 <= extEnd)
        {
            int type = (r[pos] << 8) | r[pos + 1];
            int len = (r[pos + 2] << 8) | r[pos + 3];
            pos += 4;
            if (pos + len > extEnd) return SniffStatus.NotFound;

            if (type == 0x0000) // server_name
            {
                var ext = r.Slice(pos, len);
                int p = 2; // skip list length
                while (p + 3 <= ext.Length)
                {
                    byte nameType = ext[p];
                    int nameLen = (ext[p + 1] << 8) | ext[p + 2];
                    p += 3;
                    if (p + nameLen > ext.Length) return SniffStatus.NotFound;
                    if (nameType == 0)
                        return Accept(Encoding.ASCII.GetString(ext.Slice(p, nameLen)), out host);
                    p += nameLen;
                }
                return SniffStatus.NotFound;
            }
            pos += len;
        }
        return SniffStatus.NotFound;
    }

    private static SniffStatus Http(ReadOnlySpan<byte> d, out string? host)
    {
        host = null;
        int end = d.IndexOf("\r\n\r\n"u8);
        if (end < 0)
        {
            if (d.Length < 8192) return SniffStatus.NeedMore;
            end = d.Length;
        }

        var lines = Encoding.ASCII.GetString(d[..end]).Split("\r\n");
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("host:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[5..].Trim();
            if (value.StartsWith('['))
            {
                int close = value.IndexOf(']');
                value = close > 0 ? value[1..close] : value;
            }
            else
            {
                int colon = value.IndexOf(':');
                if (colon >= 0) value = value[..colon];
            }
            return Accept(value, out host);
        }
        return SniffStatus.NotFound;
    }

    private static SniffStatus Minecraft(ReadOnlySpan<byte> d, out string? host)
    {
        host = null;
        int pos = 0;
        var s = VarInt(d, ref pos, out int pktLen);
        if (s != SniffStatus.Found) return s;
        if (pktLen <= 0 || pktLen > 2048) return SniffStatus.NotFound;
        if (d.Length < pos + pktLen) return SniffStatus.NeedMore;

        var p = d.Slice(pos, pktLen);
        int q = 0;
        if (VarInt(p, ref q, out int id) != SniffStatus.Found || id != 0) return SniffStatus.NotFound;
        if (VarInt(p, ref q, out _) != SniffStatus.Found) return SniffStatus.NotFound; // protocol version
        if (VarInt(p, ref q, out int strLen) != SniffStatus.Found || strLen <= 0 || q + strLen > p.Length)
            return SniffStatus.NotFound;

        var addr = Encoding.UTF8.GetString(p.Slice(q, strLen));
        int nul = addr.IndexOf('\0'); // Forge appends "\0FML\0"
        if (nul >= 0) addr = addr[..nul];
        return Accept(addr, out host);
    }

    private static SniffStatus VarInt(ReadOnlySpan<byte> d, ref int pos, out int value)
    {
        value = 0;
        for (int i = 0; i < 5; i++)
        {
            if (pos >= d.Length) return SniffStatus.NeedMore;
            byte b = d[pos++];
            value |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return SniffStatus.Found;
        }
        return SniffStatus.NotFound;
    }

    private static bool Skip8(ReadOnlySpan<byte> r, ref int pos)
    {
        if (pos + 1 > r.Length) return false;
        pos += 1 + r[pos];
        return pos <= r.Length;
    }

    private static bool Skip16(ReadOnlySpan<byte> r, ref int pos)
    {
        if (pos + 2 > r.Length) return false;
        pos += 2 + ((r[pos] << 8) | r[pos + 1]);
        return pos <= r.Length;
    }

    private static SniffStatus Accept(string candidate, out string? host)
    {
        candidate = candidate.Trim().TrimEnd('.');
        host = null;
        if (candidate.Length is 0 or > 253) return SniffStatus.NotFound;
        foreach (char c in candidate)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or ':')) return SniffStatus.NotFound;
        }
        host = candidate.ToLowerInvariant();
        return SniffStatus.Found;
    }
}
