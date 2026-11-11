using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Models;
using TwitchDownloaderLogConverter.Core;

namespace TwitchDownloaderLogConverter
{
    public partial class MainWindow : Window
    {
        private const int TAB_VOD = 0;
        private const int TAB_RANGE = 1;
        private const int TAB_FILE = 2;
        private const int TAB_TRACKER = 3;

        private CancellationTokenSource? _cancellation;
        private TwitchTrackerStream? _trackerStream;
        private string? _autoOutputPath;

        public MainWindow()
        {
            InitializeComponent();

            LookupButton.Click += LookupButton_Click;
            BrowseInputButton.Click += BrowseInputButton_Click;
            BrowseOutputButton.Click += BrowseOutputButton_Click;
            BrowseOverrideButton.Click += BrowseOverrideButton_Click;
            ConvertButton.Click += ConvertButton_Click;
            CancelButton.Click += (_, _) => _cancellation?.Cancel();
            EmbedBox.IsCheckedChanged += (_, _) => ThirdPartyPanel.IsEnabled = EmbedBox.IsChecked == true;
            GzipBox.IsCheckedChanged += (_, _) => FixOutputExtension();

            CopyBookmarkletButton.Click += CopyBookmarkletButton_Click;
            PasteTrackerButton.Click += PasteTrackerButton_Click;
            SegmentList.ItemTemplate = new FuncDataTemplate<StreamSegment>((segment, _) =>
                new TextBlock { Text = segment?.ToString(), TextWrapping = TextWrapping.Wrap });
            SegmentList.SelectionChanged += (_, _) => ApplySelectedSegments();
        }

        private async void CopyBookmarkletButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard is null)
                    throw new InvalidOperationException("No clipboard is available.");

                await Clipboard.SetTextAsync(TwitchTrackerTimeline.Bookmarklet);
                TrackerInfoText.Text = "Bookmarklet copied. Add a bookmark and paste it as the bookmark's URL.";
            }
            catch (Exception ex)
            {
                TrackerInfoText.Text = $"Couldn't copy the bookmarklet: {ex.Message}";
            }
        }

        private async void PasteTrackerButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var text = Clipboard is null ? null : await Clipboard.TryGetTextAsync();
                var stream = TwitchTrackerTimeline.Parse(text ?? "");
                var segments = TwitchTrackerTimeline.BuildSegments(stream);

                _trackerStream = stream;
                SegmentList.ItemsSource = segments;
                TrackerStartBox.Text = "";
                TrackerLengthBox.Text = "";

                var end = stream.End is { } streamEnd ? streamEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "?";
                TrackerInfoText.Text =
                    $"{stream.ChannelLogin ?? stream.ChannelId} · {stream.Start:yyyy-MM-dd HH:mm:ss} → {end} UTC · {segments.Count} segment{(segments.Count == 1 ? "" : "s")}. Select the ones you want.";
            }
            catch (Exception ex)
            {
                TrackerInfoText.Text = ex.Message;
            }
        }

        private void ApplySelectedSegments()
        {
            var selected = SegmentList.SelectedItems?.OfType<StreamSegment>().ToList() ?? [];
            if (_trackerStream is null || selected.Count == 0)
                return;

            var (start, length) = TwitchTrackerTimeline.GetRange(selected);
            TrackerStartBox.Text = start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            TrackerLengthBox.Text = length is { } seconds ? FormatLength(seconds) : "";

            // Suggest a file name, but don't replace one the user chose
            var current = OutputBox.Text?.Trim();
            if (string.IsNullOrEmpty(current) || current == _autoOutputPath)
            {
                var game = selected.OrderBy(s => s.Start).First().Game;
                var name = $"{_trackerStream.ChannelLogin ?? _trackerStream.ChannelId}_{start:yyyy-MM-dd_HHmm}_{Slugify(game)}_logs.json";
                OutputBox.Text = Path.Combine(GetDefaultFolder(), name);
                FixOutputExtension();
                _autoOutputPath = OutputBox.Text;
            }
        }

        private static string Slugify(string? text)
        {
            var slug = new string((text ?? "").ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
            while (slug.Contains("--"))
                slug = slug.Replace("--", "-");
            slug = slug.Trim('-');
            if (slug.Length > 40)
                slug = slug[..40].TrimEnd('-');
            return slug.Length > 0 ? slug : "stream";
        }

        private async void LookupButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var videoId = IvrLogConverter.ParseVideoId(VodBox.Text);
                if (videoId is null)
                {
                    VodInfoText.Text = "Enter a VOD URL or ID first.";
                    return;
                }

                LookupButton.IsEnabled = false;
                VodInfoText.Text = "Looking up VOD…";

                var video = (await TwitchHelper.GetVideoInfo(long.Parse(videoId))).data.video;
                if (video is null)
                {
                    VodInfoText.Text = "VOD not found. It may be deleted or expired. Use \"Channel + time range\" instead.";
                    return;
                }

                VodInfoText.Text =
                    $"{video.owner?.displayName} — {video.title}\n" +
                    $"Started {video.createdAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC · Length {FormatLength(video.lengthSeconds)} · {video.game?.displayName ?? "Unknown"}";

                if (string.IsNullOrWhiteSpace(OutputBox.Text))
                    OutputBox.Text = Path.Combine(GetDefaultFolder(), $"{video.owner?.login ?? "chat"}_{videoId}_logs.json");
                FixOutputExtension();
            }
            catch (Exception ex)
            {
                VodInfoText.Text = $"Lookup failed: {ex.Message}";
            }
            finally
            {
                LookupButton.IsEnabled = true;
            }
        }

        private async void BrowseInputButton_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open a logs.ivr.fi log",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("logs.ivr.fi logs") { Patterns = ["*.json", "*.ndjson", "*.gz", "*.txt"] },
                    FilePickerFileTypes.All
                ]
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null)
                return;

            InputFileBox.Text = path;
            if (string.IsNullOrWhiteSpace(OutputBox.Text))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                OutputBox.Text = Path.Combine(Path.GetDirectoryName(path) ?? GetDefaultFolder(), $"{name}_twitchdownloader.json");
                FixOutputExtension();
            }
        }

        private async void BrowseOverrideButton_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder of emote images (file name = emote name)",
                AllowMultiple = false
            });

            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
                OverrideFolderBox.Text = path;
        }

        private async void BrowseOutputButton_Click(object? sender, RoutedEventArgs e)
        {
            var current = OutputBox.Text;
            var startFolder = await StorageProvider.TryGetFolderFromPathAsync(
                !string.IsNullOrWhiteSpace(current) && Path.GetDirectoryName(current) is { Length: > 0 } dir && Directory.Exists(dir) ? dir : GetDefaultFolder());

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save chat JSON",
                SuggestedFileName = string.IsNullOrWhiteSpace(current) ? "chat.json" : Path.GetFileName(current),
                SuggestedStartLocation = startFolder,
                DefaultExtension = GzipBox.IsChecked == true ? "gz" : "json",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("Chat JSON") { Patterns = ["*.json", "*.json.gz"] }
                ]
            });

            if (file?.TryGetLocalPath() is { } path)
            {
                OutputBox.Text = path;
                FixOutputExtension();
            }
        }

        private async void ConvertButton_Click(object? sender, RoutedEventArgs e)
        {
            IvrLogConvertOptions options;
            try
            {
                options = BuildOptions();
            }
            catch (Exception ex)
            {
                AppendLog($"[error] {ex.Message}");
                StatusText.Text = ex.Message;
                return;
            }

            _cancellation = new CancellationTokenSource();
            SetBusy(true);
            LogBox.Text = "";
            ProgressBar.Value = 0;

            var progress = new UiTaskProgress(
                percent => ProgressBar.Value = percent,
                status => StatusText.Text = status,
                AppendLog,
                VerboseBox.IsChecked == true);

            try
            {
                var converter = new IvrLogConverter(options, progress);
                var token = _cancellation.Token;
                await Task.Run(() => converter.ConvertAsync(token), token);

                ProgressBar.Value = 100;
                StatusText.Text = $"Done: {options.OutputFile}";
                OutputBox.Text = options.OutputFile;
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Canceled";
                AppendLog("Canceled.");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed: {ex.Message}";
                AppendLog($"[error] {ex.Message}");
                if (VerboseBox.IsChecked == true)
                    AppendLog(ex.ToString());
            }
            finally
            {
                SetBusy(false);
                _cancellation.Dispose();
                _cancellation = null;
            }
        }

        private IvrLogConvertOptions BuildOptions()
        {
            var options = new IvrLogConvertOptions
            {
                DelayOffsetSeconds = (double)(DelayBox.Value ?? 0),
                BackfillCommenterInfo = BackfillBox.IsChecked == true,
                InferLegacyEmotes = LegacyEmotesBox.IsChecked == true,
                EmoteOverrideFolder = string.IsNullOrWhiteSpace(OverrideFolderBox.Text) ? null : OverrideFolderBox.Text.Trim(),
                EmbedData = EmbedBox.IsChecked == true,
                BttvEmotes = BttvBox.IsChecked == true,
                FfzEmotes = FfzBox.IsChecked == true,
                StvEmotes = StvBox.IsChecked == true,
                Compression = GzipBox.IsChecked == true ? ChatCompression.Gzip : ChatCompression.None,
            };

            switch (SourceTabs.SelectedIndex)
            {
                case TAB_VOD:
                    options.VideoId = IvrLogConverter.ParseVideoId(VodBox.Text) ?? throw new ArgumentException("Enter a VOD URL or ID.");
                    break;
                case TAB_RANGE:
                    if (string.IsNullOrWhiteSpace(ChannelBox.Text))
                        throw new ArgumentException("Enter a channel name.");
                    options.Channel = ChannelBox.Text.Trim();
                    options.StartTime = ParseUtc(RangeStartBox.Text) ?? throw new ArgumentException("Enter a start time (UTC).");
                    options.Length = ParseLength(RangeLengthBox.Text) ?? throw new ArgumentException("Enter a length.");
                    break;
                case TAB_FILE:
                    if (string.IsNullOrWhiteSpace(InputFileBox.Text) || !File.Exists(InputFileBox.Text.Trim()))
                        throw new ArgumentException("Choose a log file that exists.");
                    options.InputFile = InputFileBox.Text.Trim();
                    options.VideoId = IvrLogConverter.ParseVideoId(FileVodBox.Text);
                    options.StartTime = ParseUtc(FileStartBox.Text);
                    options.Length = ParseLength(FileLengthBox.Text);
                    break;
                case TAB_TRACKER:
                    if (_trackerStream is null)
                        throw new ArgumentException("Paste stream data from TwitchTracker first.");
                    options.Channel = _trackerStream.ChannelLogin;
                    options.ChannelId = _trackerStream.ChannelId;
                    options.StartTime = ParseUtc(TrackerStartBox.Text) ?? throw new ArgumentException("Select a segment or enter a start time (UTC).");
                    options.Length = ParseLength(TrackerLengthBox.Text) ?? throw new ArgumentException("Enter a length.");
                    var segmentAtStart = TwitchTrackerTimeline.BuildSegments(_trackerStream)
                        .LastOrDefault(s => s.Start <= options.StartTime.Value);
                    options.VideoTitle = segmentAtStart?.Title ?? segmentAtStart?.Game;
                    options.VideoGame = segmentAtStart?.Game;
                    break;
            }

            if (options.EmoteOverrideFolder is not null && !Directory.Exists(options.EmoteOverrideFolder))
                throw new ArgumentException("The emote override folder doesn't exist.");

            if (string.IsNullOrWhiteSpace(OutputBox.Text))
                throw new ArgumentException("Choose an output file.");

            FixOutputExtension();
            options.OutputFile = Path.GetFullPath(Environment.ExpandEnvironmentVariables(OutputBox.Text!.Trim().Replace("~/", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/")));
            return options;
        }

        private void SetBusy(bool busy)
        {
            ConvertButton.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
            SourceTabs.IsEnabled = !busy;
            LookupButton.IsEnabled = !busy;
            BrowseOutputButton.IsEnabled = !busy;
            BrowseOverrideButton.IsEnabled = !busy;
            OutputBox.IsEnabled = !busy;
        }

        private void AppendLog(string line)
        {
            LogBox.Text += line + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text.Length;
        }

        /// <summary>Keeps the output extension in sync with the gzip option.</summary>
        private void FixOutputExtension()
        {
            var path = OutputBox.Text?.Trim();
            if (string.IsNullOrEmpty(path))
                return;

            var gzip = GzipBox.IsChecked == true;
            var basePath = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
            if (!basePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                basePath += ".json";

            OutputBox.Text = gzip ? basePath + ".gz" : basePath;
        }

        private static string GetDefaultFolder()
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        internal static DateTime? ParseUtc(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result))
                return result;

            throw new FormatException($"\"{text.Trim()}\" is not a valid date/time. Use e.g. 2026-11-10 18:00:00 (UTC).");
        }

        /// <summary>Parses "h:mm:ss", "mm:ss" or a number of seconds.</summary>
        internal static double? ParseLength(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            text = text.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                return seconds;

            var parts = text.Split(':');
            if (parts.Length is 2 or 3 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var total = parts.Aggregate(0d, (acc, p) => acc * 60 + double.Parse(p, CultureInfo.InvariantCulture));
                if (total > 0)
                    return total;
            }

            throw new FormatException($"\"{text}\" is not a valid length. Use h:mm:ss, e.g. 5:30:00.");
        }

        private static string FormatLength(double seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
        }
    }
}
