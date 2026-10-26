using DownloadManager.Core.Streams;
using Xunit;

namespace DownloadManager.Core.Tests;

public sealed class HlsParserTests
{
    static readonly Uri Base = new("https://cdn.example.test/show/master.m3u8?token=abc");

    [Fact]
    public void Master_lists_variants_best_first_with_their_audio()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="audio/en.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="Deutsch",LANGUAGE="de",URI="audio/de.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2",AUDIO="aac"
            360/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,FRAME-RATE=59.940,AUDIO="aac"
            https://other.example.test/1080/index.m3u8
            """;

        Assert.True(HlsParser.IsMaster(text));
        var master = HlsParser.ParseMaster(text, Base);

        Assert.Equal(2, master.Variants.Count);
        Assert.Equal(1080, master.Variants[0].Height);
        Assert.Equal("1080p · 60fps · 5000 kbps", master.Variants[0].Label);
        Assert.Equal("https://other.example.test/1080/index.m3u8", master.Variants[0].Uri.ToString());
        Assert.Equal("https://cdn.example.test/show/360/index.m3u8", master.Variants[1].Uri.ToString());
        Assert.Equal("https://cdn.example.test/show/audio/en.m3u8", master.AudioFor(master.Variants[1])!.Uri!.ToString());
        Assert.False(master.HasSessionKeyDrm);
    }

    [Fact]
    public void Media_playlist_resolves_keys_byte_ranges_and_init_section()
    {
        const string text = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:10
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="720@0"
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.test/k1",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:6.0,
            #EXT-X-BYTERANGE:1000@720
            media.mp4
            #EXTINF:5.5,
            #EXT-X-BYTERANGE:2000
            media.mp4
            #EXT-X-KEY:METHOD=AES-128,URI="k2"
            #EXTINF:4,
            seg3.m4s
            #EXT-X-ENDLIST
            """;

        var playlist = HlsParser.ParseMedia(text, new Uri("https://cdn.example.test/v/index.m3u8"));

        Assert.True(playlist.EndList);
        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal(15.5, playlist.TotalDuration);
        Assert.Equal(new ByteRange(1000, 720), playlist.Segments[0].Range);
        Assert.Equal(new ByteRange(2000, 1720), playlist.Segments[1].Range); // continues after the previous range
        Assert.Equal(new ByteRange(720, 0), playlist.Segments[0].Map!.Range);
        Assert.Equal(16, playlist.Segments[0].Key!.Iv!.Length);
        Assert.Null(playlist.Segments[2].Key!.Iv); // derived from the sequence number
        Assert.Equal(12, playlist.Segments[2].Sequence);
        Assert.Equal("https://cdn.example.test/v/k2", playlist.Segments[2].Key!.Uri!.ToString());
        Assert.True(playlist.Segments[2].Key!.IsClearKey);
    }

    [Fact]
    public void Default_iv_is_the_sequence_number_big_endian()
    {
        var iv = HlsParser.DefaultIv(0x0102);
        Assert.Equal(16, iv.Length);
        Assert.Equal(0x01, iv[14]);
        Assert.Equal(0x02, iv[15]);
        Assert.All(iv[..14], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Sample_aes_is_not_a_clear_key()
    {
        const string text = """
            #EXTM3U
            #EXT-X-KEY:METHOD=SAMPLE-AES,URI="skd://key",KEYFORMAT="com.apple.streamingkeydelivery"
            #EXTINF:4,
            a.ts
            #EXT-X-ENDLIST
            """;
        var playlist = HlsParser.ParseMedia(text, Base);
        Assert.False(playlist.Segments[0].Key!.IsClearKey);
    }

    [Fact]
    public void Rejects_non_playlists()
    {
        Assert.Throws<InvalidDataException>(() => HlsParser.ParseMedia("<html></html>", Base));
    }
}
