using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderLogConverter.Core.Models;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderLogConverter.Core
{
    /// <summary>
    /// Converts chat logs from logs.ivr.fi (rustlog) into a TwitchDownloader chat JSON (<see cref="ChatRoot"/>),
    /// matching what <see cref="ChatDownloader"/> produces for a VOD as closely as possible.
    /// </summary>
    public sealed class IvrLogConverter
    {
        public const string LOGS_BASE_URL = "https://logs.ivr.fi";

        /// <summary>The user id of the "twitch" account</summary>
        private const string TWITCH_SYSTEM_USER_ID = "12826";

        private static readonly TimeSpan FetchChunkLength = TimeSpan.FromHours(1);

        private static readonly HttpClient HttpClient = CreateHttpClient();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowTrailingCommas = true
        };

        private readonly IvrLogConvertOptions _options;
        private readonly ITaskProgress _progress;
        private readonly string _cacheDir;

        public IvrLogConverter(IvrLogConvertOptions options, ITaskProgress progress)
        {
            _options = options;
            _progress = progress;
            _cacheDir = CacheDirectoryService.GetCacheDirectory(options.TempFolder);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TwitchDownloader-LogConverter");
            return client;
        }

        public async Task ConvertAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_options.OutputFile))
                throw new ArgumentException("No output file was given.");

            var chatRoot = await BuildChatRootAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (chatRoot.comments.Count == 0)
                _progress.LogWarning("No chat messages fell inside the selected time range. The output will have no comments.");

            // Load overrides first so replaced Twitch emotes aren't downloaded for nothing
            List<EmbedEmoteData> emoteOverrides = null;
            if (!string.IsNullOrWhiteSpace(_options.EmoteOverrideFolder))
            {
                _progress.SetStatus("Loading emote overrides");
                emoteOverrides = EmoteOverrides.LoadFolder(_options.EmoteOverrideFolder, _progress);
                var replaced = EmoteOverrides.ApplyToComments(chatRoot.comments, emoteOverrides.Select(o => o.name).ToHashSet(StringComparer.Ordinal));
                _progress.LogInfo($"Loaded {emoteOverrides.Count} emote override(s): {string.Join(", ", emoteOverrides.Select(o => o.name))}. Replaced {replaced:N0} Twitch emote use(s).");
            }

            if (_options.BackfillCommenterInfo && chatRoot.comments.Count > 0)
                await ChatEnrichment.BackfillCommenterInfoAsync(chatRoot, _progress);

            if (_options.EmbedData)
                await ChatEnrichment.EmbedImagesAsync(chatRoot, _options.BttvEmotes, _options.FfzEmotes, _options.StvEmotes, _cacheDir, _progress, cancellationToken);

            if (emoteOverrides is { Count: > 0 })
                EmoteOverrides.Embed(chatRoot, emoteOverrides);

            cancellationToken.ThrowIfCancellationRequested();

            var outputFileInfo = TwitchHelper.ClaimFile(_options.OutputFile, _options.FileCollisionCallback, _progress);
            _options.OutputFile = outputFileInfo.FullName;

            await using var outputFs = outputFileInfo.Open(FileMode.Create, FileAccess.Write, FileShare.Read);
            try
            {
                _progress.SetStatus("Writing Output File");
                Stream outputStream = _options.Compression switch
                {
                    ChatCompression.None => outputFs,
                    ChatCompression.Gzip => new GZipStream(outputFs, CompressionLevel.SmallestSize),
                    _ => throw new NotSupportedException($"{_options.Compression} is not a supported chat compression.")
                };

                try
                {
                    await ChatJson.SerializeAsync(outputStream, chatRoot, cancellationToken);
                }
                finally
                {
                    if (outputStream is GZipStream gzipStream)
                        await gzipStream.DisposeAsync();
                }
            }
            catch
            {
                await Task.Delay(100, CancellationToken.None);
                TwitchHelper.CleanUpClaimedFile(outputFileInfo, outputFs, _progress);
                throw;
            }

            _progress.LogInfo($"Wrote {chatRoot.comments.Count:N0} comments to {outputFileInfo.FullName}");
        }

        /// <summary>
        /// Resolves the VOD/time range, loads or fetches the logs and builds the <see cref="ChatRoot"/> (without embeds or commenter backfill).
        /// </summary>
        public async Task<ChatRoot> BuildChatRootAsync(CancellationToken cancellationToken)
        {
            var chatRoot = new ChatRoot
            {
                FileInfo = new ChatRootInfo { Version = ChatRootVersion.CurrentVersion, CreatedAt = DateTime.Now },
                streamer = new Streamer(),
                video = new Video(),
                comments = new List<Comment>()
            };

            var videoId = ParseVideoId(_options.VideoId);
            VideoInfo videoInfo = null;
            if (videoId is not null)
            {
                _progress.SetStatus("Fetching VOD info");
                videoInfo = (await TwitchHelper.GetVideoInfo(long.Parse(videoId))).data.video
                            ?? throw new NullReferenceException("Invalid VOD, deleted/expired VOD possibly? Use a channel and time range instead.");

                chatRoot.streamer.name = videoInfo.owner?.displayName;
                chatRoot.streamer.login = videoInfo.owner?.login;
                chatRoot.streamer.id = int.Parse(videoInfo.owner?.id ?? "0");
                chatRoot.video.id = videoId;
                chatRoot.video.title = videoInfo.title;
                chatRoot.video.description = videoInfo.description?.Replace("  \n", "\n").Replace("\n\n", "\n").TrimEnd();
                chatRoot.video.viewCount = videoInfo.viewCount;
                chatRoot.video.game = videoInfo.game?.displayName ?? "Unknown";
            }

            DateTime? startTime = ToUtc(_options.StartTime) ?? videoInfo?.createdAt.ToUniversalTime();
            double? length = _options.Length ?? videoInfo?.lengthSeconds;

            // Load or fetch the raw messages
            List<IvrLogMessage> messages;
            if (!string.IsNullOrWhiteSpace(_options.InputFile))
            {
                _progress.SetStatus("Reading log file");
                messages = await ReadLogFileAsync(_options.InputFile, cancellationToken);
                _progress.LogInfo($"Read {messages.Count:N0} log entries from {Path.GetFileName(_options.InputFile)}");
            }
            else
            {
                if (startTime is null || length is null)
                    throw new ArgumentException("A start time and length are required to fetch logs without a VOD.");

                string channelPath;
                if (videoInfo?.owner?.id is { Length: > 0 } ownerId)
                    channelPath = $"channelid/{Uri.EscapeDataString(ownerId)}";
                else if (!string.IsNullOrWhiteSpace(_options.Channel))
                    channelPath = $"channel/{Uri.EscapeDataString(_options.Channel.Trim().TrimStart('#').ToLowerInvariant())}";
                else
                    throw new ArgumentException("A VOD or channel name is required to fetch logs.");

                // Fetch the window that ends up at [0, length] after the delay shift
                var from = startTime.Value.AddSeconds(-_options.DelayOffsetSeconds);
                var to = from.AddSeconds(length.Value + 1);
                messages = await FetchLogsAsync(channelPath, from, to, _progress, cancellationToken);
                _progress.LogInfo($"Fetched {messages.Count:N0} log entries from logs.ivr.fi");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Fill in what Twitch couldn't tell us
            var firstMessage = messages.FirstOrDefault(m => m.tags?.ContainsKey("room-id") == true);
            if (videoInfo is null)
            {
                var channelLogin = firstMessage?.channel ?? _options.Channel?.Trim().TrimStart('#').ToLowerInvariant();
                chatRoot.streamer.login = channelLogin;
                chatRoot.streamer.name = channelLogin;
                chatRoot.streamer.id = int.TryParse(firstMessage?.tags["room-id"], out var roomId) ? roomId : 0;
                if (chatRoot.streamer.id != 0)
                {
                    try
                    {
                        var user = (await TwitchHelper.GetUserInfo([chatRoot.streamer.id.ToString()])).data.users.FirstOrDefault();
                        if (user is not null)
                        {
                            chatRoot.streamer.name = user.displayName;
                            chatRoot.streamer.login = user.login;
                        }
                    }
                    catch (Exception e)
                    {
                        _progress.LogVerbose($"Unable to fetch streamer info: {e.Message}");
                    }
                }

                startTime ??= messages.Count > 0 ? ToUtc(messages.Min(m => m.timestamp)) : DateTime.UtcNow;
                chatRoot.video.id = videoId ?? "";
                chatRoot.video.title = $"{chatRoot.streamer.name ?? "Unknown"} chat (logs.ivr.fi)";
                chatRoot.video.game = "Unknown";
            }

            chatRoot.video.created_at = startTime!.Value;

            // Old logs imported from other sources have no emote positions, so find Twitch emotes by name instead
            IReadOnlyDictionary<string, string> legacyEmotes = null;
            if (_options.InferLegacyEmotes && messages.Any(IsLegacyMessage))
            {
                _progress.SetStatus("Fetching Twitch emotes for old logs");
                legacyEmotes = await TwitchEmoteLookup.GetEmoteIdsByNameAsync(chatRoot.streamer.id, chatRoot.streamer.login, _progress, cancellationToken);
                _progress.LogInfo($"Some messages come from old logs without emote data. Matching Twitch emotes by name ({legacyEmotes.Count} global and channel emotes).");
            }

            _progress.SetStatus("Converting messages");
            chatRoot.comments = ConvertMessages(messages, startTime.Value, length, _options.DelayOffsetSeconds, videoId ?? "", legacyEmotes);

            length ??= chatRoot.comments.LastOrDefault()?.content_offset_seconds ?? 0;
            chatRoot.video.start = 0;
            chatRoot.video.end = length.Value;
            chatRoot.video.length = length.Value;

            if (videoInfo is not null)
            {
                await AddChaptersAsync(chatRoot, long.Parse(videoId!), videoInfo);
            }

            return chatRoot;
        }

        private async Task AddChaptersAsync(ChatRoot chatRoot, long videoId, VideoInfo videoInfo)
        {
            try
            {
                var chapterResponse = await TwitchHelper.GetOrGenerateVideoChapters(videoId, videoInfo, _progress);
                foreach (var responseChapter in chapterResponse.data.video.moments.edges)
                {
                    chatRoot.video.chapters.Add(new VideoChapter
                    {
                        id = responseChapter.node.id,
                        startMilliseconds = responseChapter.node.positionMilliseconds,
                        lengthMilliseconds = responseChapter.node.durationMilliseconds,
                        _type = responseChapter.node._type,
                        description = responseChapter.node.description,
                        subDescription = responseChapter.node.subDescription,
                        thumbnailUrl = responseChapter.node.thumbnailURL,
                        gameId = responseChapter.node.details.game?.id,
                        gameDisplayName = responseChapter.node.details.game?.displayName,
                        gameBoxArtUrl = responseChapter.node.details.game?.boxArtURL
                    });
                }
            }
            catch (Exception e)
            {
                _progress.LogWarning($"Unable to fetch VOD chapters: {e.Message}");
            }
        }

        /// <returns>The numeric VOD id parsed from an id or URL, or <see langword="null"/> if <paramref name="text"/> is empty.</returns>
        public static string ParseVideoId(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var match = IdParse.MatchVideoId(text);
            if (match is not { Success: true })
                throw new FormatException($"Unable to parse a VOD id from \"{text.Trim()}\".");

            return match.Value;
        }

        /// <summary>
        /// Fetches <c>[from, to)</c> from logs.ivr.fi in one hour chunks.
        /// </summary>
        /// <param name="channelPath"><c>channel/{login}</c> or <c>channelid/{id}</c></param>
        public static async Task<List<IvrLogMessage>> FetchLogsAsync(string channelPath, DateTime from, DateTime to, ITaskProgress progress, CancellationToken cancellationToken)
        {
            from = ToUtc(from);
            to = ToUtc(to);

            var messages = new List<IvrLogMessage>();
            var seenIds = new HashSet<string>();
            var totalTicks = Math.Max(1, (to - from).Ticks);

            progress?.SetTemplateStatus("Fetching logs from logs.ivr.fi {0}%", 0);
            for (var chunkStart = from; chunkStart < to; chunkStart += FetchChunkLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkEnd = chunkStart + FetchChunkLength < to ? chunkStart + FetchChunkLength : to;
                var url = $"{LOGS_BASE_URL}/{channelPath}?from={FormatRfc3339(chunkStart)}&to={FormatRfc3339(chunkEnd)}&json=1";

                using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (messages.Count == 0 && chunkStart == from && body.Contains("not logged", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"logs.ivr.fi does not log this channel ({channelPath}).");

                    progress?.LogVerbose($"No logs for {chunkStart:u} - {chunkEnd:u}: {body.Trim()}");
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"logs.ivr.fi returned {(int)response.StatusCode} {response.ReasonPhrase}: {body.Trim()}", null, response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var logs = await JsonSerializer.DeserializeAsync<IvrLogsResponse>(stream, JsonOptions, cancellationToken);
                foreach (var message in logs?.messages ?? [])
                {
                    // Chunk boundaries may overlap
                    if (!string.IsNullOrEmpty(message.id) && !seenIds.Add(message.id))
                        continue;

                    messages.Add(message);
                }

                progress?.ReportProgress((int)((chunkEnd - from).Ticks * 100 / totalTicks));
            }

            progress?.ReportProgress(100);
            return messages;
        }

        /// <summary>
        /// Reads a logs.ivr.fi <c>?json=1</c> response (<c>{"messages":[...]}</c>), a bare message array, or an <c>?ndjson=1</c> file. Gzip is supported.
        /// </summary>
        public static async Task<List<IvrLogMessage>> ReadLogFileAsync(string path, CancellationToken cancellationToken)
        {
            await using var fs = File.OpenRead(path);
            Stream stream = fs;

            var magic = new byte[2];
            var read = await fs.ReadAsync(magic, cancellationToken);
            fs.Seek(0, SeekOrigin.Begin);
            if (read == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
                stream = new GZipStream(fs, CompressionMode.Decompress);

            await using var _ = stream == fs ? null : stream;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync(cancellationToken);
            return ParseLogText(content);
        }

        public static List<IvrLogMessage> ParseLogText(string content)
        {
            var trimmed = content.AsSpan().TrimStart();
            if (trimmed.IsEmpty)
                return new List<IvrLogMessage>();

            if (trimmed[0] == '[')
                return JsonSerializer.Deserialize<List<IvrLogMessage>>(content, JsonOptions) ?? new List<IvrLogMessage>();

            try
            {
                var response = JsonSerializer.Deserialize<IvrLogsResponse>(content, JsonOptions);
                if (response?.messages is { Count: > 0 })
                    return response.messages;
            }
            catch (JsonException)
            {
                // Probably NDJSON
            }

            var messages = new List<IvrLogMessage>();
            foreach (var line in content.Split('\n'))
            {
                var lineTrimmed = line.Trim();
                if (lineTrimmed.Length == 0)
                    continue;

                var message = JsonSerializer.Deserialize<IvrLogMessage>(lineTrimmed, JsonOptions);
                if (message is not null)
                    messages.Add(message);
            }

            return messages;
        }

        /// <summary>
        /// Converts raw log messages to comments, keeping only what Twitch VOD chat would contain.
        /// </summary>
        /// <param name="videoStart">The moment that maps to offset 0</param>
        /// <param name="length">Comments past this offset are dropped. <see langword="null"/> keeps everything after <paramref name="videoStart"/>.</param>
        /// <param name="delayOffset">Seconds added to every offset</param>
        /// <param name="contentId">The VOD id, or an empty string</param>
        /// <param name="legacyEmotes">Twitch emote ids by name, used to find emotes in old messages that have no emote data</param>
        public static List<Comment> ConvertMessages(IEnumerable<IvrLogMessage> messages, DateTime videoStart, double? length, double delayOffset, string contentId,
            IReadOnlyDictionary<string, string> legacyEmotes = null)
        {
            videoStart = ToUtc(videoStart);
            var seenIds = new HashSet<string>();
            var comments = new List<Comment>();

            foreach (var message in messages)
            {
                var comment = ConvertMessage(message, videoStart, delayOffset, contentId, legacyEmotes);
                if (comment is null)
                    continue;

                if (comment.content_offset_seconds < 0 || comment.content_offset_seconds > length)
                    continue;

                if (!seenIds.Add(comment._id))
                    continue;

                comments.Add(comment);
            }

            // Stable sort, logs are already chronological
            return comments.OrderBy(c => c.content_offset_seconds).ToList();
        }

        /// <returns>The converted comment, or <see langword="null"/> if the message is not something Twitch keeps in VOD chat.</returns>
        public static Comment ConvertMessage(IvrLogMessage message, DateTime videoStart, double delayOffset, string contentId,
            IReadOnlyDictionary<string, string> legacyEmotes = null)
        {
            if (message?.tags is null)
                return null;

            var tags = message.tags;
            var userId = GetTag(tags, "user-id");
            if (string.IsNullOrEmpty(userId))
                return null;

            // Old imported logs record timeouts/bans as chat messages from the "twitch" account. VOD chat has no such messages.
            if (userId == TWITCH_SYSTEM_USER_ID && IsLegacyMessage(message))
                return null;

            string systemText = null;
            string userText;
            switch ((IvrLogMessageType)message.type)
            {
                case IvrLogMessageType.PrivMsg:
                    userText = StripAction(message.text ?? "");
                    break;
                case IvrLogMessageType.UserNotice:
                {
                    var msgId = GetTag(tags, "msg-id");
                    // Raids are not part of VOD chat
                    if (msgId is "raid" or "unraid")
                        return null;

                    userText = GetUserNoticeUserText(message);
                    // Announcements and first-time-chatter rituals show up in VOD chat as plain messages
                    if (msgId is not ("announcement" or "ritual"))
                        systemText = GetTag(tags, "system-msg")?.Trim();
                    break;
                }
                default:
                    // CLEARCHAT, CLEARMSG, NOTICE, etc. are not part of VOD chat
                    return null;
            }

            // Twitch VOD chat formats user notices as "<system message> <user message>", keeping the space even when there is no user message
            var prefix = string.IsNullOrEmpty(systemText) ? "" : systemText + " ";
            var body = prefix + userText;
            if (body.Length == 0)
                return null;

            var createdAt = ToUtc(message.timestamp);
            var login = GetTag(tags, "login") ?? message.username ?? "";
            var displayName = GetTag(tags, "display-name")?.Trim();
            if (string.IsNullOrEmpty(displayName))
                displayName = string.IsNullOrWhiteSpace(message.displayName) ? login : message.displayName.Trim();

            var emotesTag = GetTag(tags, "emotes");
            if (string.IsNullOrEmpty(emotesTag) && legacyEmotes is { Count: > 0 } && IsLegacyMessage(message))
                emotesTag = InferEmotesTag(userText, legacyEmotes);

            var (fragments, emoticons) = BuildFragments(prefix, userText, emotesTag);

            var newMessage = new Message
            {
                body = body,
                bits_spent = GetBits(tags, body),
                fragments = fragments,
                user_badges = ParseBadges(GetTag(tags, "badges")),
                user_color = string.IsNullOrEmpty(GetTag(tags, "color")) ? null : GetTag(tags, "color"),
                emoticons = emoticons
            };

            return new Comment
            {
                _id = string.IsNullOrEmpty(message.id) ? GetTag(tags, "id") ?? Guid.NewGuid().ToString() : message.id,
                created_at = createdAt,
                channel_id = GetTag(tags, "room-id") ?? "",
                content_type = "video",
                content_id = contentId ?? "",
                content_offset_seconds = Math.Round((createdAt - videoStart).TotalSeconds + delayOffset, 3),
                commenter = new Commenter
                {
                    display_name = displayName,
                    _id = userId,
                    name = login
                },
                message = newMessage
            };
        }

        /// <summary>
        /// Splits a message into text and emote fragments. IRC emote indices are Unicode code point indices into <paramref name="userText"/>.
        /// </summary>
        public static (List<Fragment> fragments, List<Emoticon2> emoticons) BuildFragments(string prefix, string userText, string emotesTag)
        {
            var fragments = new List<Fragment>();
            var emoticons = new List<Emoticon2>();

            AddTextFragment(fragments, prefix);

            var codePointToUtf16 = GetCodePointIndexMap(userText);
            var codePointCount = codePointToUtf16.Length - 1;
            var prefixCodePoints = CountCodePoints(prefix);

            var utf16Pos = 0;
            foreach (var (emoteId, start, end) in ParseEmotesTag(emotesTag))
            {
                // Out of range (e.g. a trimmed message) or overlapping
                if (start < 0 || end < start || end >= codePointCount)
                    continue;

                var utf16Start = codePointToUtf16[start];
                var utf16End = codePointToUtf16[end + 1];
                if (utf16Start < utf16Pos)
                    continue;

                AddTextFragment(fragments, userText[utf16Pos..utf16Start]);

                var emoteText = userText[utf16Start..utf16End];
                fragments.Add(new Fragment { text = emoteText, emoticon = new Emoticon { emoticon_id = emoteId } });

                var begin = prefixCodePoints + start;
                emoticons.Add(new Emoticon2 { _id = emoteId, begin = begin, end = begin + emoteText.Length + 1 });

                utf16Pos = utf16End;
            }

            AddTextFragment(fragments, userText[utf16Pos..]);

            return (fragments, emoticons);
        }

        private static void AddTextFragment(List<Fragment> fragments, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (fragments.Count > 0 && fragments[^1].emoticon is null)
            {
                fragments[^1].text += text;
                return;
            }

            fragments.Add(new Fragment { text = text, emoticon = null });
        }

        /// <returns>(id, start, end) tuples sorted by start, where start and end are inclusive code point indices</returns>
        public static List<(string id, int start, int end)> ParseEmotesTag(string emotesTag)
        {
            var result = new List<(string, int, int)>();
            if (string.IsNullOrWhiteSpace(emotesTag))
                return result;

            // Format: id:start-end,start-end/id2:start-end
            foreach (var emote in emotesTag.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIndex = emote.LastIndexOf(':');
                if (colonIndex <= 0)
                    continue;

                var id = emote[..colonIndex];
                foreach (var range in emote[(colonIndex + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var dashIndex = range.IndexOf('-');
                    if (dashIndex <= 0
                        || !int.TryParse(range.AsSpan(0, dashIndex), NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                        || !int.TryParse(range.AsSpan(dashIndex + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var end))
                        continue;

                    result.Add((id, start, end));
                }
            }

            result.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return result;
        }

        /// <summary>
        /// Messages imported into logs.ivr.fi from older logging sources have no message id and no emote positions.
        /// Every message Twitch sends today has an <c>id</c> tag.
        /// </summary>
        public static bool IsLegacyMessage(IvrLogMessage message)
            => message is not null
               && string.IsNullOrEmpty(message.id)
               && (message.tags is null || !message.tags.ContainsKey("id"))
               && (IvrLogMessageType)message.type is IvrLogMessageType.PrivMsg or IvrLogMessageType.UserNotice;

        /// <summary>
        /// Builds an IRC <c>emotes</c> tag by matching space separated words against known Twitch emote names, the same way Twitch chat matches them.
        /// </summary>
        /// <returns>The tag (<c>id:start-end,start-end/id2:...</c>, code point indices), or an empty string if nothing matched</returns>
        public static string InferEmotesTag(string text, IReadOnlyDictionary<string, string> emoteIdsByName)
        {
            if (string.IsNullOrEmpty(text) || emoteIdsByName is not { Count: > 0 })
                return "";

            var ranges = new Dictionary<string, List<string>>();
            var order = new List<string>();
            var codePoint = 0;
            var wordStartCodePoint = 0;
            var wordStart = 0;

            for (var i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == ' ')
                {
                    if (i > wordStart && emoteIdsByName.TryGetValue(text[wordStart..i], out var id))
                    {
                        if (!ranges.TryGetValue(id, out var list))
                        {
                            ranges[id] = list = new List<string>();
                            order.Add(id);
                        }

                        list.Add($"{wordStartCodePoint}-{codePoint - 1}");
                    }

                    wordStart = i + 1;
                    wordStartCodePoint = codePoint + 1;
                    codePoint++;
                    continue;
                }

                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;

                codePoint++;
            }

            return string.Join('/', order.Select(id => $"{id}:{string.Join(',', ranges[id])}"));
        }

        public static List<UserBadge> ParseBadges(string badgesTag)
        {
            var badges = new List<UserBadge>();
            if (string.IsNullOrWhiteSpace(badgesTag))
                return badges;

            foreach (var badge in badgesTag.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var slashIndex = badge.IndexOf('/');
                var setId = slashIndex < 0 ? badge : badge[..slashIndex];
                var version = slashIndex < 0 ? "" : badge[(slashIndex + 1)..];
                if (string.IsNullOrEmpty(setId) && string.IsNullOrEmpty(version))
                    continue;

                badges.Add(new UserBadge { _id = setId, version = version });
            }

            return badges;
        }

        private static int GetBits(Dictionary<string, string> tags, string body)
        {
            if (int.TryParse(GetTag(tags, "bits"), NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
                return bits;

            // Same fallback as ChatDownloader
            var bitMatch = TwitchRegex.BitsRegex.Match(body);
            return bitMatch.Success && int.TryParse(bitMatch.ValueSpan, out var result) ? result : 0;
        }

        /// <summary>
        /// Gets the user-typed part of a USERNOTICE. rustlog's <c>text</c> is "system-msg user-message", the raw IRC line has only the user message.
        /// </summary>
        private static string GetUserNoticeUserText(IvrLogMessage message)
        {
            var raw = message.raw;
            if (!string.IsNullOrEmpty(raw))
            {
                // @tags :tmi.twitch.tv USERNOTICE #channel :message
                var tagsEnd = raw.StartsWith('@') ? raw.IndexOf(' ') : 0;
                if (tagsEnd >= 0)
                {
                    var commandIndex = raw.IndexOf(" USERNOTICE ", tagsEnd, StringComparison.Ordinal);
                    if (commandIndex >= 0)
                    {
                        var trailingIndex = raw.IndexOf(" :", commandIndex + 12, StringComparison.Ordinal);
                        return trailingIndex < 0 ? "" : raw[(trailingIndex + 2)..].TrimEnd('\r', '\n');
                    }
                }
            }

            var text = message.text ?? "";
            var systemMsg = GetTag(message.tags, "system-msg");
            if (string.IsNullOrEmpty(systemMsg))
                return text;

            if (text == systemMsg)
                return "";

            return text.StartsWith(systemMsg + " ", StringComparison.Ordinal)
                ? text[(systemMsg.Length + 1)..]
                : text;
        }

        private static string StripAction(string text)
        {
            const string ACTION_START = "\u0001ACTION ";
            if (text.StartsWith(ACTION_START, StringComparison.Ordinal))
            {
                text = text[ACTION_START.Length..];
                if (text.EndsWith('\u0001'))
                    text = text[..^1];
            }

            return text;
        }

        private static string GetTag(Dictionary<string, string> tags, string key)
            => tags is not null && tags.TryGetValue(key, out var value) ? value : null;

        /// <returns>An array where index i is the UTF-16 index of code point i, with one extra entry for the end of the string.</returns>
        private static int[] GetCodePointIndexMap(string text)
        {
            var map = new List<int>(text.Length + 1);
            for (var i = 0; i < text.Length; i++)
            {
                map.Add(i);
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
            }

            map.Add(text.Length);
            return map.ToArray();
        }

        private static int CountCodePoints(string text) => GetCodePointIndexMap(text).Length - 1;

        private static DateTime ToUtc(DateTime dateTime) => dateTime.Kind switch
        {
            DateTimeKind.Utc => dateTime,
            DateTimeKind.Local => dateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
        };

        private static DateTime? ToUtc(DateTime? dateTime) => dateTime is null ? null : ToUtc(dateTime.Value);

        private static string FormatRfc3339(DateTime dateTime) => ToUtc(dateTime).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
