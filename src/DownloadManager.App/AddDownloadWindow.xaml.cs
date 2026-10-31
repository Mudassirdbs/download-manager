using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DownloadManager.Core;
using DownloadManager.Core.Streams;
using Microsoft.Win32;

namespace DownloadManager.App;

public partial class AddDownloadWindow : Window
{
    static readonly string[] CategoryNames = ["General", "Compressed", "Documents", "Music", "Programs", "Video"];

    static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/x-matroska"] = ".mkv", ["video/quicktime"] = ".mov",
        ["video/x-flv"] = ".flv", ["video/mp2t"] = ".ts", ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a",
        ["audio/webm"] = ".weba", ["audio/ogg"] = ".ogg", ["audio/wav"] = ".wav",
    };

    /// <summary>A row of the Quality list: a direct file, an HLS variant or a yt-dlp format.</summary>
    sealed record QualityOption(string Display, DownloadKind Kind, Uri? StreamUrl = null, Uri? AudioUrl = null, FormatChoice? Format = null)
    {
        public override string ToString() => Display; // what screen readers announce for the selected item
    }

    readonly BridgeMessage? _source;
    readonly Tools _tools;
    readonly CancellationTokenSource _closing = new();
    bool _folderEdited;
    string? _title;

    public DownloadItem? Result { get; private set; }
    public bool StartNow { get; private set; }

    public AddDownloadWindow(BridgeMessage? source, Tools tools)
    {
        InitializeComponent();
        _source = source;
        _tools = tools;
        _title = source?.Title;
        CategoryBox.ItemsSource = CategoryNames;
        FolderBox.TextChanged += (_, _) => _folderEdited = FolderBox.IsKeyboardFocusWithin || _folderEdited;
        Closed += (_, _) => _closing.Cancel();
        FindFormatsButton.IsEnabled = tools.YtDlp is not null;
        if (tools.YtDlp is null) FindFormatsButton.ToolTip = "Install yt-dlp from Tools in the main window";

        if (source is null)
        {
            if (Clipboard.ContainsText() && Uri.TryCreate(Clipboard.GetText().Trim(), UriKind.Absolute, out var clip)
                && clip.Scheme is "http" or "https")
                UrlBox.Text = clip.ToString();
            SetOptions([new QualityOption("Direct download (file at this address)", DownloadKind.File)]);
            CategoryBox.SelectedItem = "General";
            Loaded += (_, _) => UrlBox.Focus();
            return;
        }

        UrlBox.Text = source.Type == "format" ? source.PageUrl ?? source.Url : source.Url;
        NameBox.Text = SuggestName(source) ?? "";
        CategoryBox.SelectedItem = FileNames.CategoryOf(NameBox.Text);
        var info = new List<string>();
        if (source.Size is > 0) info.Add($"Size: {Format.Bytes(source.Size)}");
        if (!string.IsNullOrEmpty(source.Mime)) info.Add($"Type: {source.Mime}");
        if (!string.IsNullOrEmpty(source.PageUrl)) info.Add($"From: {source.PageUrl}");
        InfoText.Text = string.Join("   ", info);

        switch (source)
        {
            case { Type: "media", Kind: "hls" }:
                Loaded += async (_, _) => await LoadHlsAsync(new Uri(source.Url!));
                break;
            case { Type: "media", Kind: "dash" }:
                SetOptions([]);
                Loaded += async (_, _) => await FindFormatsAsync(source.Url!);
                break;
            case { Type: "format", FormatSelector: { } selector }:
                SetOptions([new QualityOption(source.Quality ?? selector, DownloadKind.YtDlp,
                    Format: new FormatChoice(selector, source.Quality ?? selector, AudioOnly(selector) ? "m4a" : "mp4", source.Size, AudioOnly(selector)))]);
                break;
            default:
                SetOptions([new QualityOption("Direct download", DownloadKind.File)]);
                break;
        }
        Loaded += (_, _) => { Activate(); NameBox.Focus(); NameBox.SelectAll(); };
    }

    static bool AudioOnly(string selector) => selector.StartsWith("ba", StringComparison.Ordinal);

    void SetOptions(List<QualityOption> options)
    {
        QualityBox.ItemsSource = options;
        QualityBox.SelectedIndex = options.Count > 0 ? 0 : -1;
    }

    QualityOption? Selected => QualityBox.SelectedItem as QualityOption;

    async Task LoadHlsAsync(Uri url)
    {
        SetBusy("Reading stream qualities…");
        try
        {
            var ctx = _source is null ? new RequestContext() : App.ContextOf(_source);
            var text = await Net.GetStringAsync(url, ctx, _closing.Token);
            if (!HlsParser.IsMaster(text))
            {
                var media = HlsParser.ParseMedia(text, url);
                SetOptions([new QualityOption($"Stream ({TimeSpan.FromSeconds(media.TotalDuration):hh\\:mm\\:ss})", DownloadKind.Hls, url)]);
            }
            else
            {
                var master = HlsParser.ParseMaster(text, url);
                if (master.HasSessionKeyDrm) throw new NotSupportedException("This stream is DRM-protected and cannot be downloaded.");
                SetOptions(master.Variants.Select(v =>
                {
                    var audio = master.AudioFor(v);
                    var display = v.Label + (audio is not null ? $" · audio: {audio.Name ?? audio.Language ?? "default"}" : "");
                    return new QualityOption(display, DownloadKind.Hls, v.Uri, audio?.Uri);
                }).ToList());
            }
            SetBusy(null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetBusy(null);
            ShowError(ex.Message);
        }
    }

    async void OnFindFormats(object sender, RoutedEventArgs e) => await FindFormatsAsync(UrlBox.Text.Trim());

    async Task FindFormatsAsync(string url)
    {
        if (_tools.YtDlp is null)
        {
            ShowError("yt-dlp is not installed. Install it from Tools in the main window.");
            return;
        }
        SetBusy("Asking yt-dlp for formats…");
        try
        {
            var ctx = _source is null ? new RequestContext() : App.ContextOf(_source);
            var info = await Task.Run(() => YtDlp.ResolveAsync(_tools, url, ctx, _closing.Token));
            _title = info.Title;
            SetOptions(info.Choices.Select(c => new QualityOption(
                c.Label + (c.Size is { } s ? $" · ~{Format.Bytes(s)}" : ""), DownloadKind.YtDlp, Format: c)).ToList());
            NameBox.Text = FileNames.Sanitize(info.Title) + "." + (Selected?.Format?.Ext ?? "mp4");
            SetBusy(null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetBusy(null);
            ShowError(ex.Message);
        }
    }

    void SetBusy(string? text)
    {
        InfoText.Text = text ?? InfoText.Text;
        if (text is null && InfoText.Text.EndsWith('…')) InfoText.Text = "";
        StartButton.IsEnabled = text is null;
        FindFormatsButton.IsEnabled = text is null && _tools.YtDlp is not null;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Browser file name first; for media, the page title plus an extension from the URL or MIME type.</summary>
    static string? SuggestName(BridgeMessage m)
    {
        if (!string.IsNullOrWhiteSpace(m.FileName)) return FileNames.Sanitize(Path.GetFileName(m.FileName));
        var fromUrl = Uri.TryCreate(m.Url, UriKind.Absolute, out var u) ? FileNames.FromUrl(u) : null;
        var isStream = m.Kind is "hls" or "dash" || m.Type == "format";
        if (m.Type is not ("media" or "format") || string.IsNullOrWhiteSpace(m.Title))
            return fromUrl is null ? null : FileNames.Sanitize(isStream ? Path.ChangeExtension(fromUrl, ".mp4") : fromUrl);
        string? ext;
        if (isStream) ext = m.FormatSelector is { } f && AudioOnly(f) ? ".m4a" : ".mp4";
        else
        {
            ext = Path.GetExtension(fromUrl ?? "");
            if (ext.Length is < 2 or > 5 && m.Mime is not null) MimeExtensions.TryGetValue(m.Mime.Split(';')[0].Trim(), out ext);
        }
        return FileNames.Sanitize(m.Title.Trim() + (ext ?? ""));
    }

    static string FolderFor(string category) =>
        category == "General" ? FileNames.DefaultRoot : Path.Combine(FileNames.DefaultRoot, category);

    void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_source is not null || !Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var u)) return;
        NameBox.Text = FileNames.Sanitize(FileNames.FromUrl(u));
        if (Selected?.Kind != DownloadKind.File) SetOptions([new QualityOption("Direct download (file at this address)", DownloadKind.File)]);
    }

    void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected?.Format is { } f && NameBox.Text.Length > 0)
            NameBox.Text = Path.ChangeExtension(NameBox.Text, "." + f.Ext);
    }

    void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_folderEdited) CategoryBox.SelectedItem = FileNames.CategoryOf(NameBox.Text);
    }

    void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is string category && !_folderEdited) FolderBox.Text = FolderFor(category);
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : FileNames.DefaultRoot };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            _folderEdited = true;
        }
    }

    void OnStart(object sender, RoutedEventArgs e) => Accept(start: true);

    void OnLater(object sender, RoutedEventArgs e) => Accept(start: false);

    void Accept(bool start)
    {
        if (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            ShowError("Enter an http or https address.");
            return;
        }
        if (Selected is not { } option)
        {
            ShowError("Choose a quality first.");
            return;
        }
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            ShowError("Choose a full folder path.");
            return;
        }
        var name = NameBox.Text.Trim();
        if (option.Kind != DownloadKind.File && name.Length == 0) name = FileNames.Sanitize(_title ?? "video") + ".mp4";
        Directory.CreateDirectory(folder);

        var request = _source is null ? new RequestContext() : App.ContextOf(_source);
        Result = new DownloadItem
        {
            Url = option.StreamUrl?.ToString() ?? url.ToString(),
            Kind = option.Kind,
            AudioUrl = option.AudioUrl?.ToString(),
            FormatSelector = option.Format?.Selector,
            Quality = option.Kind == DownloadKind.File ? null : option.Format?.Label ?? option.Display,
            Folder = folder,
            FileName = name.Length == 0 ? null : FileNames.Unique(folder, FileNames.Sanitize(name)),
            TotalBytes = option.Format?.Size ?? (_source?.Size is > 0 && option.Kind == DownloadKind.File ? _source.Size : null),
            TotalIsEstimate = option.Format?.Size is not null,
            Request = request,
        };
        StartNow = start;
        DialogResult = true;
    }

    void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
