using TwitchDownloaderCore.Models;

namespace TwitchDownloaderLogConverter.Core
{
    public class IvrLogConvertOptions
    {
        /// <summary>
        /// Twitch VOD id. When set, the stream start, length, title, streamer and chapters come from Twitch,
        /// and (unless <see cref="InputFile"/> is set) the matching time range is fetched from logs.ivr.fi.
        /// </summary>
        public string VideoId { get; set; }

        /// <summary>
        /// Channel login to fetch logs for when no <see cref="VideoId"/> is given (e.g. for a deleted VOD).
        /// Requires <see cref="StartTime"/> and <see cref="Length"/>.
        /// </summary>
        public string Channel { get; set; }

        /// <summary>
        /// A local logs.ivr.fi JSON (<c>?json=1</c>) or NDJSON (<c>?ndjson=1</c>) file. When set, no logs are fetched.
        /// </summary>
        public string InputFile { get; set; }

        /// <summary>
        /// The moment that maps to offset 0 of the output. Overrides the VOD's start time when both are given.
        /// When converting a local file without a VOD, defaults to the first message.
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// Length in seconds. Overrides the VOD's length when both are given.
        /// When converting a local file without a VOD, defaults to the last message.
        /// </summary>
        public double? Length { get; set; }

        /// <summary>
        /// Seconds added to every message offset. Positive values make chat appear later.
        /// </summary>
        public double DelayOffsetSeconds { get; set; }

        /// <summary>
        /// Old logs (imported into logs.ivr.fi from other sources) have no emote data.
        /// When enabled, Twitch global and channel emotes are matched by name in those messages.
        /// </summary>
        public bool InferLegacyEmotes { get; set; } = true;

        /// <summary>
        /// A folder of images to use as emotes. Each file's name (without extension) is the emote name, e.g. <c>PogChamp.png</c>.
        /// They replace Twitch, BTTV, FFZ and 7TV emotes with the same name and are always embedded in the output.
        /// </summary>
        public string EmoteOverrideFolder { get; set; }

        public string OutputFile { get; set; }
        public ChatCompression Compression { get; set; } = ChatCompression.None;
        public bool BackfillCommenterInfo { get; set; } = true;
        public bool EmbedData { get; set; }
        public bool BttvEmotes { get; set; } = true;
        public bool FfzEmotes { get; set; } = true;
        public bool StvEmotes { get; set; } = true;
        public string TempFolder { get; set; }
        public Func<FileInfo, FileInfo> FileCollisionCallback { get; set; } = info => info;
    }
}
