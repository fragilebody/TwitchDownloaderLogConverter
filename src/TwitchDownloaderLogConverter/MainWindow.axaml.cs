using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

        private CancellationTokenSource? _cancellation;

        public MainWindow()
        {
            InitializeComponent();

            LookupButton.Click += LookupButton_Click;
            BrowseInputButton.Click += BrowseInputButton_Click;
            BrowseOutputButton.Click += BrowseOutputButton_Click;
            ConvertButton.Click += ConvertButton_Click;
            CancelButton.Click += (_, _) => _cancellation?.Cancel();
            EmbedBox.IsCheckedChanged += (_, _) => ThirdPartyPanel.IsEnabled = EmbedBox.IsChecked == true;
            GzipBox.IsCheckedChanged += (_, _) => FixOutputExtension();
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
            }

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
