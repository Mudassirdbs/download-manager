using System.Globalization;
using System.Text.RegularExpressions;

namespace DownloadManager.Core.Streams;

/// <summary>Byte range within a resource (EXT-X-BYTERANGE / EXT-X-MAP BYTERANGE).</summary>
public sealed record ByteRange(long Length, long Offset);

public sealed record HlsKey(string Method, Uri? Uri, byte[]? Iv, string? KeyFormat)
{
    public bool IsNone => Method == "NONE";
    /// <summary>Plain AES-128-CBC with an identity key — the only encryption downloadable without a CDM.</summary>
    public bool IsClearKey => Method == "AES-128" && (KeyFormat is null or "identity");
}

public sealed record HlsSegment(int Index, Uri Uri, double Duration, ByteRange? Range, HlsKey? Key, long Sequence, HlsMap? Map);

public sealed record HlsMap(Uri Uri, ByteRange? Range);

public sealed record HlsVariant(Uri Uri, long Bandwidth, int? Width, int? Height, string? Codecs, double? FrameRate, string? AudioGroup)
{
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (Height is { } h) parts.Add($"{h}p");
            if (FrameRate is > 30) parts.Add($"{Math.Round(FrameRate.Value)}fps");
            parts.Add($"{Bandwidth / 1000.0:0} kbps");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record HlsRendition(string Type, string GroupId, string? Name, string? Language, Uri? Uri, bool IsDefault);

public sealed class HlsMasterPlaylist
{
    public List<HlsVariant> Variants { get; } = [];
    public List<HlsRendition> Renditions { get; } = [];
    public bool HasSessionKeyDrm { get; set; }

    /// <summary>Audio playlist to mux with a variant: the group's default, else its first entry with a URI.</summary>
    public HlsRendition? AudioFor(HlsVariant variant) =>
        variant.AudioGroup is null
            ? null
            : Renditions.Where(r => r.Type == "AUDIO" && r.GroupId == variant.AudioGroup && r.Uri is not null)
                .OrderByDescending(r => r.IsDefault).FirstOrDefault();
}

public sealed class HlsMediaPlaylist
{
    public List<HlsSegment> Segments { get; } = [];
    public bool EndList { get; set; }
    public double TargetDuration { get; set; }
    public double TotalDuration => Segments.Sum(s => s.Duration);
}

/// <summary>Parser for RFC 8216 playlists. Unknown tags are ignored; URIs resolve against the playlist URL.</summary>
public static class HlsParser
{
    static readonly Regex Attribute = new(@"([A-Z0-9-]+)=(""[^""]*""|[^,]*)", RegexOptions.Compiled);

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static HlsMasterPlaylist ParseMaster(string text, Uri baseUri)
    {
        EnsureHeader(text);
        var master = new HlsMasterPlaylist();
        Dictionary<string, string>? pending = null;
        foreach (var raw in Lines(text))
        {
            if (raw.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pending = Attributes(raw);
            }
            else if (raw.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = Attributes(raw);
                master.Renditions.Add(new HlsRendition(
                    a.GetValueOrDefault("TYPE", ""), a.GetValueOrDefault("GROUP-ID", ""), a.GetValueOrDefault("NAME"),
                    a.GetValueOrDefault("LANGUAGE"), a.TryGetValue("URI", out var u) ? new Uri(baseUri, u) : null,
                    a.GetValueOrDefault("DEFAULT") == "YES"));
            }
            else if (raw.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
            {
                var a = Attributes(raw);
                if (a.GetValueOrDefault("METHOD") is "SAMPLE-AES" or "SAMPLE-AES-CTR" || a.ContainsKey("KEYFORMAT") && a["KEYFORMAT"] != "identity")
                    master.HasSessionKeyDrm = true;
            }
            else if (!raw.StartsWith('#') && pending is not null)
            {
                int? width = null, height = null;
                if (pending.TryGetValue("RESOLUTION", out var res) && res.Split('x') is [var w, var h])
                {
                    width = int.Parse(w, CultureInfo.InvariantCulture);
                    height = int.Parse(h, CultureInfo.InvariantCulture);
                }
                master.Variants.Add(new HlsVariant(
                    new Uri(baseUri, raw),
                    long.TryParse(pending.GetValueOrDefault("BANDWIDTH"), CultureInfo.InvariantCulture, out var bw) ? bw : 0,
                    width, height, pending.GetValueOrDefault("CODECS"),
                    double.TryParse(pending.GetValueOrDefault("FRAME-RATE"), CultureInfo.InvariantCulture, out var fr) ? fr : null,
                    pending.GetValueOrDefault("AUDIO")));
                pending = null;
            }
        }
        master.Variants.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
        return master;
    }

    public static HlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        EnsureHeader(text);
        var playlist = new HlsMediaPlaylist();
        long sequence = 0;
        double duration = 0;
        ByteRange? range = null;
        long nextOffset = 0; // a BYTERANGE without @offset continues where the previous sub-range ended
        HlsKey? key = null;
        HlsMap? map = null;
        foreach (var raw in Lines(text))
        {
            if (raw.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                sequence = long.Parse(raw[22..], CultureInfo.InvariantCulture);
            else if (raw.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                playlist.TargetDuration = double.Parse(raw[22..], CultureInfo.InvariantCulture);
            else if (raw.StartsWith("#EXTINF:", StringComparison.Ordinal))
                duration = double.Parse(raw[8..].Split(',')[0], CultureInfo.InvariantCulture);
            else if (raw.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                range = ParseRange(raw[17..], nextOffset);
            else if (raw.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                key = ParseKey(Attributes(raw), baseUri);
            else if (raw.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var a = Attributes(raw);
                map = new HlsMap(new Uri(baseUri, a["URI"]), a.TryGetValue("BYTERANGE", out var br) ? ParseRange(br, 0) : null);
            }
            else if (raw == "#EXT-X-ENDLIST")
                playlist.EndList = true;
            else if (!raw.StartsWith('#'))
            {
                var uri = new Uri(baseUri, raw);
                playlist.Segments.Add(new HlsSegment(playlist.Segments.Count, uri, duration, range, key is { IsNone: true } ? null : key, sequence, map));
                nextOffset = range is null ? 0 : range.Offset + range.Length;
                sequence++;
                duration = 0;
                range = null;
            }
        }
        return playlist;
    }

    /// <summary>Default IV: the media sequence number as a 128-bit big-endian integer (RFC 8216 §5.2).</summary>
    public static byte[] DefaultIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    static HlsKey ParseKey(Dictionary<string, string> a, Uri baseUri)
    {
        byte[]? iv = null;
        if (a.TryGetValue("IV", out var hex))
            iv = Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
        return new HlsKey(a.GetValueOrDefault("METHOD", "NONE"), a.TryGetValue("URI", out var u) ? new Uri(baseUri, u) : null,
            iv, a.GetValueOrDefault("KEYFORMAT"));
    }

    static ByteRange ParseRange(string value, long continueAt)
    {
        var parts = value.Split('@');
        var length = long.Parse(parts[0], CultureInfo.InvariantCulture);
        var offset = parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : continueAt;
        return new ByteRange(length, offset);
    }

    static Dictionary<string, string> Attributes(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Attribute.Matches(line[(line.IndexOf(':') + 1)..]))
            result[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return result;
    }

    static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    static void EnsureHeader(string text)
    {
        if (!text.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new InvalidDataException("Not an HLS playlist (missing #EXTM3U)");
    }
}
