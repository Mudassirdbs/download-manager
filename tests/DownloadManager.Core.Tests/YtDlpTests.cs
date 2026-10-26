using System.Text.Json;
using Xunit;

namespace DownloadManager.Core.Tests;

public sealed class YtDlpTests
{
    // Shape of `yt-dlp -J` output, reduced to the fields the mapping reads.
    const string Info = """
        {
          "title": "A test video",
          "extractor_key": "Youtube",
          "duration": 212,
          "formats": [
            { "format_id": "sb0", "protocol": "mhtml", "vcodec": "none", "acodec": "none" },
            { "format_id": "139", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.5", "tbr": 48, "filesize": 1300000 },
            { "format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "tbr": 129, "filesize": 3400000 },
            { "format_id": "18", "ext": "mp4", "vcodec": "avc1", "acodec": "mp4a.40.2", "height": 360, "tbr": 600, "filesize": 15000000 },
            { "format_id": "137", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "height": 1080, "fps": 30, "tbr": 4000, "filesize": 90000000 },
            { "format_id": "299", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "height": 1080, "fps": 60, "tbr": 6000, "filesize_approx": 140000000 },
            { "format_id": "251", "ext": "webm", "vcodec": "none", "acodec": "opus", "tbr": 160, "filesize": 3900000 },
            { "format_id": "248", "ext": "webm", "vcodec": "vp9", "acodec": "none", "height": 1080, "fps": 60, "tbr": 7000, "filesize": 160000000 },
            { "format_id": "313", "ext": "webm", "vcodec": "vp09.00.51.08", "acodec": "none", "height": 2160, "fps": 30, "tbr": 15000, "filesize": 400000000 },
            { "format_id": "drm", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "height": 4320, "has_drm": true }
          ]
        }
        """;

    [Fact]
    public void Prefers_h264_and_aac_per_resolution_and_names_other_codecs()
    {
        using var json = JsonDocument.Parse(Info);
        var info = YtDlp.Describe(json.RootElement);

        Assert.Equal("A test video", info.Title);
        // 2160p exists only as VP9; 1080p prefers H.264 even though VP9 has a higher bitrate. DRM 4320p is dropped.
        Assert.Equal(["Best quality (2160p · VP9)", "2160p · VP9", "1080p60", "360p", "Audio only"], info.Choices.Select(c => c.Label));
        Assert.Equal(info.Choices[1].Selector, info.Choices[0].Selector);
        Assert.Equal(400000000 + 3400000, info.Choices[1].Size); // video-only + AAC audio (not the larger Opus)
        Assert.Equal(140000000 + 3400000, info.Choices[2].Size); // H.264 60 fps, not VP9
        Assert.Equal(15000000, info.Choices[3].Size); // already has audio
        Assert.True(info.Choices[4].AudioOnly);
        Assert.Equal("m4a", info.Choices[4].Ext);
        Assert.Equal("ba[acodec^=mp4a]/ba/b", info.Choices[4].Selector);
    }

    [Fact]
    public void Selector_pins_h264_to_the_exact_height_before_falling_back()
    {
        using var json = JsonDocument.Parse(Info);
        var selector = YtDlp.Describe(json.RootElement).Choices.Single(c => c.Label == "1080p60").Selector;

        var alternatives = selector.Split("/");
        Assert.Equal("bv*[height=1080][vcodec^=avc1]+ba[acodec^=mp4a]", alternatives[0]);
        Assert.Equal("bv*[height<=1080]+ba", alternatives[^2]);
        Assert.Equal("b[height<=1080]", alternatives[^1]);
    }

    [Fact]
    public void Live_pages_are_refused()
    {
        using var json = JsonDocument.Parse("""{ "title": "live", "is_live": true, "formats": [] }""");
        Assert.Throws<NotSupportedException>(() => YtDlp.Describe(json.RootElement));
    }

    [Fact]
    public void Cookie_file_uses_netscape_format_with_scope()
    {
        var ctx = new RequestContext
        {
            CookieJar =
            [
                new BrowserCookie { Name = "SID", Value = "abc", Domain = ".youtube.com", Secure = true, ExpirationDate = 1900000000.5 },
                new BrowserCookie { Name = "pref", Value = "x=1", Domain = "www.youtube.com", HostOnly = true },
            ],
        };
        using var file = CookieFile.Create("https://www.youtube.com/watch?v=1", ctx)!;
        var lines = File.ReadAllLines(file.Path);

        Assert.Equal("# Netscape HTTP Cookie File", lines[0]);
        Assert.Equal(".youtube.com\tTRUE\t/\tTRUE\t1900000000\tSID\tabc", lines[1]);
        Assert.Equal("www.youtube.com\tFALSE\t/\tFALSE\t0\tpref\tx=1", lines[2]);
        var path = file.Path;
        file.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Cookie_header_alone_is_scoped_to_the_host()
    {
        using var file = CookieFile.Create("https://vimeo.com/1", new RequestContext { Cookies = "a=1; b=2" })!;
        var lines = File.ReadAllLines(file.Path);
        Assert.Equal("vimeo.com\tFALSE\t/\tTRUE\t0\ta\t1", lines[1]);
        Assert.Equal(3, lines.Length);
    }
}
