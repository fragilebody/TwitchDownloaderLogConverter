using System.Globalization;
using System.Text.Json;

namespace TwitchDownloaderLogConverter.Core
{
    /// <summary>A category change on a TwitchTracker stream page. TwitchTracker samples every 10 minutes.</summary>
    public sealed record TwitchTrackerGame(DateTime Time, string Id, string Name);

    /// <summary>A title change on a TwitchTracker stream page.</summary>
    public sealed record TwitchTrackerTitle(DateTime Time, string Title);

    /// <summary>The timeline of one stream, as copied from a TwitchTracker stream page by <see cref="TwitchTrackerTimeline.Bookmarklet"/>.</summary>
    public sealed class TwitchTrackerStream
    {
        public string Url { get; init; }
        public string ChannelId { get; init; }
        public string ChannelLogin { get; init; }
        public string StreamId { get; init; }

        /// <summary>Stream start (UTC).</summary>
        public DateTime Start { get; init; }

        /// <summary>Stream end (UTC), or <see langword="null"/> if unknown.</summary>
        public DateTime? End { get; init; }

        public IReadOnlyList<TwitchTrackerGame> Games { get; init; } = [];
        public IReadOnlyList<TwitchTrackerTitle> Titles { get; init; } = [];
    }

    /// <summary>A part of a stream with one category and one title.</summary>
    public sealed record StreamSegment(DateTime Start, DateTime? End, string Game, string Title)
    {
        /// <summary>Length in seconds, or <see langword="null"/> for an open-ended last segment.</summary>
        public double? LengthSeconds => End is { } end ? (end - Start).TotalSeconds : null;

        public override string ToString()
        {
            var end = End is { } e
                ? e.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + (e.Date > Start.Date ? $" (+{(e.Date - Start.Date).Days}d)" : "")
                : "?";
            var length = LengthSeconds is { } seconds ? $" ({FormatLength(seconds)})" : "";
            var title = string.IsNullOrWhiteSpace(Title) ? "" : $" · {Title.Trim()}";
            return $"{Start.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}–{end}{length} · {Game ?? "Unknown"}{title}";
        }

        internal static string FormatLength(double seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
        }
    }

    /// <summary>
    /// Reads stream timelines copied from TwitchTracker stream pages and splits them into segments.
    /// TwitchTracker doesn't allow scraping and doesn't serve this data to non-browser clients,
    /// so the app never fetches it: the user runs <see cref="Bookmarklet"/> on the page and pastes the result.
    /// </summary>
    public static class TwitchTrackerTimeline
    {
        public const string FORMAT = "ttlc-twitchtracker/1";

        /// <summary>
        /// Copies the stream page's timeline (<c>Y</c> and <c>window.channel</c>) to the clipboard as JSON.
        /// For a live stream, the last chart sample is used as the end.
        /// </summary>
        public const string Bookmarklet =
            "javascript:(()=>{" +
            "if(typeof Y==='undefined'||!Y||!Y.games||!window.channel){alert('Open a stream page on twitchtracker.com first, e.g. twitchtracker.com/forsen/streams/...');return}" +
            "const c=Y.chart||[];" +
            "const d=JSON.stringify({format:'" + FORMAT + "',url:location.href,channel:{id:window.channel.id,name:window.channel.name},id:Y.id," +
            "created_at:Y.created_at,finished_at:Y.finished_at||(c.length?c[c.length-1][0]:null),games:Y.games,titles:Y.titles||[]});" +
            "navigator.clipboard.writeText(d).then(()=>alert('Stream data copied. Paste it into TwitchDownloader Log Converter.'),()=>prompt('Copy this stream data:',d))" +
            "})()";

        public static TwitchTrackerStream Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("The clipboard is empty. Click the bookmarklet on a TwitchTracker stream page first.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json.Trim());
            }
            catch (JsonException)
            {
                throw new FormatException("The clipboard doesn't contain TwitchTracker stream data. Click the bookmarklet on a TwitchTracker stream page first.");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || GetString(root, "format") != FORMAT)
                    throw new FormatException("The clipboard doesn't contain TwitchTracker stream data. Click the bookmarklet on a TwitchTracker stream page first.");

                var start = GetDate(root, "created_at") ?? throw new FormatException("The stream data has no start time.");

                var games = new List<TwitchTrackerGame>();
                if (root.TryGetProperty("games", out var gamesElement) && gamesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var game in gamesElement.EnumerateArray())
                    {
                        if (GetDate(game, "date") is { } time)
                            games.Add(new TwitchTrackerGame(time, GetString(game, "id"), GetString(game, "name")));
                    }
                }

                if (games.Count == 0)
                    throw new FormatException("The stream data has no categories.");

                var titles = new List<TwitchTrackerTitle>();
                if (root.TryGetProperty("titles", out var titlesElement) && titlesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var title in titlesElement.EnumerateArray())
                    {
                        if (GetDate(title, "created_at") is { } time)
                            titles.Add(new TwitchTrackerTitle(time, GetString(title, "title")));
                    }
                }

                string channelId = null, channelLogin = null;
                if (root.TryGetProperty("channel", out var channel) && channel.ValueKind == JsonValueKind.Object)
                {
                    channelId = GetString(channel, "id");
                    channelLogin = GetString(channel, "name")?.Trim().ToLowerInvariant();
                }

                if (string.IsNullOrWhiteSpace(channelLogin) && string.IsNullOrWhiteSpace(channelId))
                    throw new FormatException("The stream data has no channel.");

                var end = GetDate(root, "finished_at");
                return new TwitchTrackerStream
                {
                    Url = GetString(root, "url"),
                    ChannelId = channelId,
                    ChannelLogin = channelLogin,
                    StreamId = GetString(root, "id"),
                    Start = start,
                    End = end > start ? end : null,
                    Games = games.OrderBy(g => g.Time).ToList(),
                    Titles = titles.OrderBy(t => t.Time).ToList()
                };
            }
        }

        /// <summary>
        /// Splits the stream at every category change. Adjacent parts with the same category are merged.
        /// Title changes don't split segments (streamers often leave the title as is); each segment is labeled with the title at its start.
        /// </summary>
        public static List<StreamSegment> BuildSegments(TwitchTrackerStream stream)
        {
            var boundaries = stream.Games.Select(g => g.Time)
                .Append(stream.Start)
                .Where(t => t >= stream.Start && (stream.End is null || t < stream.End))
                .Distinct()
                .Order()
                .ToList();

            var segments = new List<StreamSegment>();
            for (var i = 0; i < boundaries.Count; i++)
            {
                var start = boundaries[i];
                var end = i + 1 < boundaries.Count ? boundaries[i + 1] : stream.End;
                var game = stream.Games.LastOrDefault(g => g.Time <= start) ?? stream.Games[0];
                var title = stream.Titles.LastOrDefault(t => t.Time <= start) ?? stream.Titles.FirstOrDefault();

                if (segments.Count > 0 && segments[^1] is var previous && previous.Game == game.Name)
                {
                    segments[^1] = previous with { End = end };
                    continue;
                }

                segments.Add(new StreamSegment(start, end, game.Name, title?.Title));
            }

            return segments;
        }

        /// <returns>The time range covering all <paramref name="selected"/> segments. The length is <see langword="null"/> if the last one is open-ended.</returns>
        public static (DateTime start, double? lengthSeconds) GetRange(IEnumerable<StreamSegment> selected)
        {
            var list = selected.ToList();
            if (list.Count == 0)
                throw new ArgumentException("Select at least one segment.", nameof(selected));

            var start = list.Min(s => s.Start);
            var end = list.Any(s => s.End is null) ? null : list.Max(s => s.End);
            return (start, end is { } e ? (e - start).TotalSeconds : null);
        }

        private static string GetString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }

        private static DateTime? GetDate(JsonElement element, string name)
        {
            var text = GetString(element, name);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Category times are "yyyy-MM-dd HH:mm:ss" in UTC, the others are ISO 8601 with a Z
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result))
                return result;

            throw new FormatException($"\"{text}\" in the stream data is not a valid date.");
        }
    }
}
