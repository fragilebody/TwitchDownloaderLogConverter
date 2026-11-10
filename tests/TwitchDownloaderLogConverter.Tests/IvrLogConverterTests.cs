using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore;
using TwitchDownloaderLogConverter.Core;
using TwitchDownloaderLogConverter.Core.Models;

namespace TwitchDownloaderLogConverter.Tests
{
    // ReSharper disable StringLiteralTypo
    public class IvrLogConverterTests
    {
        private static readonly DateTime VideoStart = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

        // Messages in the logs.ivr.fi format (names anonymized)
        private const string PRIVMSG_JSON = """
            {"text":"<daily","displayName":"chatviewer","timestamp":"2026-11-10T00:00:00.901Z","id":"51e14b14-1afc-4004-a8f6-dd33697fedb6","tags":{"id":"51e14b14-1afc-4004-a8f6-dd33697fedb6","display-name":"chatviewer","tmi-sent-ts":"1794268800901","badges":"subscriber/108,glhf-pledge/1","subscriber":"1","room-id":"20000001","badge-info":"subscriber/110","emotes":"","color":"#FF00FF","user-id":"10000001"},"username":"chatviewer","channel":"examplechannel","raw":"@tmi-sent-ts=1794268800901 :chatviewer!chatviewer@chatviewer.tmi.twitch.tv PRIVMSG #examplechannel :<daily","type":1}
            """;

        private const string CLEARCHAT_JSON = """
            {"text":"timedoutviewer has been timed out for 11340 seconds","displayName":"timedoutviewer","timestamp":"2026-11-10T00:01:15.700Z","id":"","tags":{"target-user-id":"10000002","room-id":"20000001","ban-duration":"11340","user-id":"10000002"},"username":"timedoutviewer","channel":"examplechannel","raw":"@room-id=20000001 :tmi.twitch.tv CLEARCHAT #examplechannel :timedoutviewer","type":2}
            """;

        private const string RESUB_JSON = """
            {"text":"ResubViewer subscribed at Tier 1. They've subscribed for 44 months! 44 big ones :)","displayName":"ResubViewer","timestamp":"2026-11-10T00:10:00.000Z","id":"aaaa-resub","tags":{"login":"resubviewer","display-name":"ResubViewer","user-id":"1234","room-id":"20000001","emotes":"499:12-13","msg-id":"resub","system-msg":"ResubViewer subscribed at Tier 1. They've subscribed for 44 months!","badges":"subscriber/36","color":""},"username":"resubviewer","channel":"examplechannel","raw":"@login=resubviewer;msg-id=resub;system-msg=ResubViewer\\ssubscribed :tmi.twitch.tv USERNOTICE #examplechannel :44 big ones :)","type":4}
            """;

        private const string SUB_NO_MESSAGE_JSON = """
            {"text":"PrimeViewer subscribed with Prime. They've subscribed for 60 months!","displayName":"PrimeViewer","timestamp":"2026-11-10T00:20:00.000Z","id":"bbbb-sub","tags":{"login":"primeviewer","display-name":"PrimeViewer","user-id":"10000003","room-id":"20000001","emotes":"","msg-id":"resub","system-msg":"PrimeViewer subscribed with Prime. They've subscribed for 60 months!"},"username":"primeviewer","channel":"examplechannel","raw":"@login=primeviewer;msg-id=resub :tmi.twitch.tv USERNOTICE #examplechannel","type":4}
            """;

        private static IvrLogMessage Parse(string json) => IvrLogConverter.ParseLogText(json).Single();

        [Fact]
        public void ConvertsPrivMsgFields()
        {
            var comment = IvrLogConverter.ConvertMessage(Parse(PRIVMSG_JSON), VideoStart, 0, "123");

            Assert.NotNull(comment);
            Assert.Equal("51e14b14-1afc-4004-a8f6-dd33697fedb6", comment._id);
            Assert.Equal("20000001", comment.channel_id);
            Assert.Equal("video", comment.content_type);
            Assert.Equal("123", comment.content_id);
            Assert.Equal(0.901, comment.content_offset_seconds, 3);
            Assert.Equal(DateTimeKind.Utc, comment.created_at.Kind);
            Assert.Equal("chatviewer", comment.commenter.display_name);
            Assert.Equal("10000001", comment.commenter._id);
            Assert.Equal("chatviewer", comment.commenter.name);
            Assert.Equal("<daily", comment.message.body);
            Assert.Equal("#FF00FF", comment.message.user_color);
            Assert.Equal(0, comment.message.bits_spent);
            Assert.Null(comment.message.user_notice_params);
            Assert.Empty(comment.message.emoticons);

            var fragment = Assert.Single(comment.message.fragments);
            Assert.Equal("<daily", fragment.text);
            Assert.Null(fragment.emoticon);

            Assert.Collection(comment.message.user_badges,
                b => { Assert.Equal("subscriber", b._id); Assert.Equal("108", b.version); },
                b => { Assert.Equal("glhf-pledge", b._id); Assert.Equal("1", b.version); });
        }

        [Fact]
        public void DropsClearChat()
        {
            Assert.Null(IvrLogConverter.ConvertMessage(Parse(CLEARCHAT_JSON), VideoStart, 0, ""));
        }

        [Fact]
        public void ConvertsResubLikeVodChat()
        {
            var comment = IvrLogConverter.ConvertMessage(Parse(RESUB_JSON), VideoStart, 0, "");

            Assert.NotNull(comment);
            const string SYSTEM_MSG = "ResubViewer subscribed at Tier 1. They've subscribed for 44 months! ";
            Assert.Equal(SYSTEM_MSG + "44 big ones :)", comment.message.body);
            Assert.Equal("resubviewer", comment.commenter.name);
            Assert.Null(comment.message.user_color);
            Assert.Null(comment.message.user_notice_params);

            Assert.Collection(comment.message.fragments,
                f => { Assert.Equal(SYSTEM_MSG + "44 big ones ", f.text); Assert.Null(f.emoticon); },
                f => { Assert.Equal(":)", f.text); Assert.Equal("499", f.emoticon.emoticon_id); });

            var emoticon = Assert.Single(comment.message.emoticons);
            Assert.Equal(SYSTEM_MSG.Length + 12, emoticon.begin);

            Assert.Equal(HighlightType.SubscribedTier, HighlightIcons.GetHighlightType(comment));
        }

        [Fact]
        public void SubWithoutMessageKeepsTrailingSpace()
        {
            var comment = IvrLogConverter.ConvertMessage(Parse(SUB_NO_MESSAGE_JSON), VideoStart, 0, "");

            Assert.NotNull(comment);
            Assert.Equal("PrimeViewer subscribed with Prime. They've subscribed for 60 months! ", comment.message.body);
            Assert.Single(comment.message.fragments);
            Assert.Equal(HighlightType.SubscribedPrime, HighlightIcons.GetHighlightType(comment));
        }

        [Fact]
        public void SplitsMultipleEmotes()
        {
            var (fragments, emoticons) = IvrLogConverter.BuildFragments("", "xqcC FBBlock xqcC", "301614364:0-3,13-16/1441276:5-11");

            Assert.Collection(fragments,
                f => { Assert.Equal("xqcC", f.text); Assert.Equal("301614364", f.emoticon.emoticon_id); },
                f => { Assert.Equal(" ", f.text); Assert.Null(f.emoticon); },
                f => { Assert.Equal("FBBlock", f.text); Assert.Equal("1441276", f.emoticon.emoticon_id); },
                f => { Assert.Equal(" ", f.text); Assert.Null(f.emoticon); },
                f => { Assert.Equal("xqcC", f.text); Assert.Equal("301614364", f.emoticon.emoticon_id); });

            Assert.Equal([0, 5, 13], emoticons.Select(e => e.begin));
            // Same convention as ChatDownloader: end = begin + length + 1
            Assert.Equal([5, 13, 18], emoticons.Select(e => e.end));
        }

        [Fact]
        public void EmoteIndicesAreCodePoints()
        {
            // 😂 is 2 UTF-16 chars but 1 code point, so Kappa is at code points 2-6
            var (fragments, _) = IvrLogConverter.BuildFragments("", "😂 Kappa hi", "25:2-6");

            Assert.Collection(fragments,
                f => Assert.Equal("😂 ", f.text),
                f => { Assert.Equal("Kappa", f.text); Assert.Equal("25", f.emoticon.emoticon_id); },
                f => Assert.Equal(" hi", f.text));
        }

        [Fact]
        public void IgnoresOutOfRangeEmotes()
        {
            var (fragments, emoticons) = IvrLogConverter.BuildFragments("", "Kappa", "25:0-4,10-14");

            Assert.Single(fragments);
            Assert.Single(emoticons);
        }

        [Fact]
        public void FiltersByRangeAndAppliesDelay()
        {
            var messages = IvrLogConverter.ParseLogText($$"""{"messages":[{{PRIVMSG_JSON}},{{CLEARCHAT_JSON}},{{RESUB_JSON}},{{SUB_NO_MESSAGE_JSON}}]}""");
            Assert.Equal(4, messages.Count);

            // Start 1s after the first message, 15 minutes long, shift chat 0.05s later
            var comments = IvrLogConverter.ConvertMessages(messages, VideoStart.AddSeconds(1), 15 * 60, 0.05, "");

            // PRIVMSG lands at -0.049 (dropped), CLEARCHAT dropped, resub at 599.05, sub at 1199.05 (past the end)
            var comment = Assert.Single(comments);
            Assert.Equal("aaaa-resub", comment._id);
            Assert.Equal(599.05, comment.content_offset_seconds, 3);

            // A positive delay pushes the first message back into range
            comments = IvrLogConverter.ConvertMessages(messages, VideoStart.AddSeconds(1), null, 2, "");
            Assert.Equal(["51e14b14-1afc-4004-a8f6-dd33697fedb6", "aaaa-resub", "bbbb-sub"], comments.Select(c => c._id));
            Assert.Equal(1.901, comments[0].content_offset_seconds, 3);
        }

        [Fact]
        public void ParsesNdjson()
        {
            var messages = IvrLogConverter.ParseLogText(PRIVMSG_JSON.Trim() + "\n" + CLEARCHAT_JSON.Trim() + "\n");
            Assert.Equal(2, messages.Count);
            Assert.Equal((int)IvrLogMessageType.ClearChat, messages[1].type);
        }

        [Fact]
        public void ParsesVideoIdFromUrl()
        {
            Assert.Equal("1880788415", IvrLogConverter.ParseVideoId("https://www.twitch.tv/videos/1880788415"));
            Assert.Equal("1880788415", IvrLogConverter.ParseVideoId("1880788415"));
            Assert.Null(IvrLogConverter.ParseVideoId("  "));
            Assert.Throws<FormatException>(() => IvrLogConverter.ParseVideoId("not a vod"));
        }
    }
}
