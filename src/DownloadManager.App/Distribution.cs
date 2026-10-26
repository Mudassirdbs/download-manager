using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DownloadManager.Core;

namespace DownloadManager.App;

/// <summary>
/// Per-distribution facts in <c>distribution.json</c> next to the exe: the extension IDs the native host accepts
/// (the stores assign their own) and where users get the extension. Editing the file needs no rebuild.
/// </summary>
sealed class Distribution
{
    public List<string> ChromiumExtensionIds { get; set; } = [];
    public List<string> FirefoxExtensionIds { get; set; } = [];
    public string? ChromeStoreUrl { get; set; }
    public string? EdgeStoreUrl { get; set; }
    public string? FirefoxStoreUrl { get; set; }

    static Distribution? _current;

    public static Distribution Current => _current ??= Load();

    static Distribution Load()
    {
        var result = new Distribution();
        var path = Path.Combine(AppContext.BaseDirectory, "distribution.json");
        try
        {
            if (File.Exists(path))
                result = JsonSerializer.Deserialize<Distribution>(File.ReadAllText(path), Json.Options) ?? result;
        }
        catch (JsonException ex)
        {
            Log.Error("distribution.json is invalid; using built-in extension IDs", ex);
        }
        // The development build's fixed ID always works, so an unpacked copy can be used alongside store copies.
        result.ChromiumExtensionIds = result.ChromiumExtensionIds.Append(Bridge.ChromeExtensionId).Distinct().ToList();
        result.FirefoxExtensionIds = result.FirefoxExtensionIds.Append(Bridge.FirefoxExtensionId).Distinct().ToList();
        return result;
    }

    /// <summary>The unpacked extension shipped with the app, for manual (Developer mode) installs.</summary>
    public static string BundledExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");
}
