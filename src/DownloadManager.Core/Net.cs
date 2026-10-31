using System.Net;

namespace DownloadManager.Core;

/// <summary>The one HTTP client all transfers share, and the browser-context request builder.</summary>
public static class Net
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseCookies = false, // cookies come from the browser per request
        AutomaticDecompression = DecompressionMethods.None, // byte offsets must match the file on disk
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    // Headers the HTTP stack owns or that would corrupt a ranged GET.
    static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "range", "content-length", "content-type", "connection", "accept-encoding",
        "cookie", "referer", "user-agent", "if-range", "if-none-match", "if-modified-since",
    };

    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DownloadManager/1.0";

    public static HttpRequestMessage NewRequest(Uri url, RequestContext ctx)
    {
        var request = new HttpRequestMessage(ctx.Method == "POST" ? HttpMethod.Post : HttpMethod.Get, url);
        if (ctx.Method == "POST" && ctx.Body is not null)
        {
            request.Content = new ByteArrayContent(Convert.FromBase64String(ctx.Body));
            var type = ctx.Headers?.FirstOrDefault(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value;
            request.Content.Headers.TryAddWithoutValidation("Content-Type", type ?? "application/x-www-form-urlencoded");
        }
        if (ctx.Headers is not null)
            foreach (var (name, value) in ctx.Headers)
                if (!SkippedHeaders.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
        if (!string.IsNullOrEmpty(ctx.Referrer) && Uri.TryCreate(ctx.Referrer, UriKind.Absolute, out var referrer))
            request.Headers.Referrer = referrer;
        if (!string.IsNullOrEmpty(ctx.Cookies)) request.Headers.TryAddWithoutValidation("Cookie", ctx.Cookies);
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(ctx.UserAgent) ? DefaultUserAgent : ctx.UserAgent);
        return request;
    }

    public static async Task<string> GetStringAsync(Uri url, RequestContext ctx, CancellationToken token)
    {
        using var response = await Client.SendAsync(NewRequest(url, ctx), HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException(response.StatusCode, response.ReasonPhrase);
        return await response.Content.ReadAsStringAsync(token);
    }
}
