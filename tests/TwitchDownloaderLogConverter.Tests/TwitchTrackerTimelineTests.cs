using TwitchDownloaderLogConverter.Core;

namespace TwitchDownloaderLogConverter.Tests
{
    public class TwitchTrackerTimelineTests
    {
        // Copied by the bookmarklet from https://twitchtracker.com/forsen/streams/319170079719 on 2026-11-11 while the stream was live.
        // finished_at is the last chart sample, which the bookmarklet uses for live streams.
        private const string FORSEN_STREAM_JSON = """
            {"format":"ttlc-twitchtracker/1","url":"https://twitchtracker.com/forsen/streams/319170079719",
             "channel":{"id":22484632,"name":"forsen"},"id":319170079719,
             "created_at":"2026-11-11T14:59:27.000000Z","finished_at":"2026-11-11 20:40:00",
             "games":[{"date":"2026-11-11 14:59:27","id":509658,"name":"Just Chatting"},
                      {"date":"2026-11-11 16:00:00","id":18122,"name":"World of Warcraft"}],
             "titles":[{"created_at":"2026-11-11T14:59:27.000Z","title":"Games and shit!","relative_at":0},
                       {"created_at":"2026-11-11T16:00:00.000Z","title":"[DROPS] UGANDAN TAKEOVER! SLAYING ALLIANCE DOGS! PVP SERVER","relative_at":60}]}
            """;

        private const string WOW_TITLE = "[DROPS] UGANDAN TAKEOVER! SLAYING ALLIANCE DOGS! PVP SERVER";

        private static DateTime Utc(int hour, int minute, int second = 0) => new(2026, 11, 11, hour, minute, second, DateTimeKind.Utc);

        [Fact]
        public void ParsesStream()
        {
            var stream = TwitchTrackerTimeline.Parse(FORSEN_STREAM_JSON);

            Assert.Equal("forsen", stream.ChannelLogin);
            Assert.Equal("22484632", stream.ChannelId);
            Assert.Equal("319170079719", stream.StreamId);
            Assert.Equal("https://twitchtracker.com/forsen/streams/319170079719", stream.Url);

            Assert.Equal(Utc(14, 59, 27), stream.Start);
            Assert.Equal(DateTimeKind.Utc, stream.Start.Kind);
            Assert.Equal(Utc(20, 40), stream.End);

            Assert.Equal([Utc(14, 59, 27), Utc(16, 0)], stream.Games.Select(g => g.Time));
            Assert.Equal(["Just Chatting", "World of Warcraft"], stream.Games.Select(g => g.Name));
            Assert.Equal("18122", stream.Games[1].Id);
            Assert.All(stream.Games, g => Assert.Equal(DateTimeKind.Utc, g.Time.Kind));

            Assert.Equal([Utc(14, 59, 27), Utc(16, 0)], stream.Titles.Select(t => t.Time));
            Assert.All(stream.Titles, t => Assert.Equal(DateTimeKind.Utc, t.Time.Kind));
        }

        [Fact]
        public void BuildsOneSegmentPerCategory()
        {
            var segments = TwitchTrackerTimeline.BuildSegments(TwitchTrackerTimeline.Parse(FORSEN_STREAM_JSON));

            Assert.Equal(2, segments.Count);
            Assert.Equal(new StreamSegment(Utc(14, 59, 27), Utc(16, 0), "Just Chatting", "Games and shit!"), segments[0]);
            Assert.Equal(new StreamSegment(Utc(16, 0), Utc(20, 40), "World of Warcraft", WOW_TITLE), segments[1]);
            Assert.Equal("16:00:00–20:40:00 (4:40:00) · World of Warcraft · " + WOW_TITLE, segments[1].ToString());
        }

        [Fact]
        public void TitleChangeDoesNotSplitSegment()
        {
            // Segments follow the category only. Each one is labeled with the title at its start.
            var json = FORSEN_STREAM_JSON.Replace(
                "\"relative_at\":60}]",
                "\"relative_at\":60},{\"created_at\":\"2026-11-11T18:00:00.000Z\",\"title\":\"Dungeons\",\"relative_at\":180}]");

            var segments = TwitchTrackerTimeline.BuildSegments(TwitchTrackerTimeline.Parse(json));

            Assert.Equal(2, segments.Count);
            Assert.Equal(new StreamSegment(Utc(16, 0), Utc(20, 40), "World of Warcraft", WOW_TITLE), segments[1]);
        }

        [Fact]
        public void TitleChangedBeforeCategoryLabelsNextSegment()
        {
            // The title was changed ten minutes before the category, so the WoW segment shows it
            var json = FORSEN_STREAM_JSON.Replace("2026-11-11T16:00:00.000Z", "2026-11-11T15:50:00.000Z");

            var segments = TwitchTrackerTimeline.BuildSegments(TwitchTrackerTimeline.Parse(json));

            Assert.Equal(2, segments.Count);
            Assert.Equal(new StreamSegment(Utc(14, 59, 27), Utc(16, 0), "Just Chatting", "Games and shit!"), segments[0]);
            Assert.Equal(new StreamSegment(Utc(16, 0), Utc(20, 40), "World of Warcraft", WOW_TITLE), segments[1]);
        }

        [Fact]
        public void MergesRepeatedCategory()
        {
            // TwitchTracker can list the same category twice in a row
            var json = FORSEN_STREAM_JSON.Replace(
                "\"name\":\"World of Warcraft\"}]",
                "\"name\":\"World of Warcraft\"},{\"date\":\"2026-11-11 17:00:00\",\"id\":18122,\"name\":\"World of Warcraft\"}]");

            var segments = TwitchTrackerTimeline.BuildSegments(TwitchTrackerTimeline.Parse(json));

            Assert.Equal(2, segments.Count);
            Assert.Equal(new StreamSegment(Utc(16, 0), Utc(20, 40), "World of Warcraft", WOW_TITLE), segments[1]);
        }

        [Fact]
        public void RangeOfOneSegment()
        {
            var segments = TwitchTrackerTimeline.BuildSegments(TwitchTrackerTimeline.Parse(FORSEN_STREAM_JSON));

            var (start, length) = TwitchTrackerTimeline.GetRange([segments[1]]);

            Assert.Equal(Utc(16, 0), start);
            Assert.Equal(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(40), TimeSpan.FromSeconds(length!.Value));
        }

        [Fact]
        public void RangeOfSeveralSegments()
        {
            var segments = TwitchTrackerTimeline.BuildSegments(TwitchTrackerTimeline.Parse(FORSEN_STREAM_JSON));

            // Selection order doesn't matter
            var (start, length) = TwitchTrackerTimeline.GetRange([segments[1], segments[0]]);

            Assert.Equal(Utc(14, 59, 27), start);
            Assert.Equal(new TimeSpan(5, 40, 33), TimeSpan.FromSeconds(length!.Value));
        }

        [Theory]
        [InlineData("\"finished_at\":null")]
        [InlineData("\"unused\":0")]
        public void UnknownEndLeavesLastSegmentOpen(string finishedAt)
        {
            var json = FORSEN_STREAM_JSON.Replace("\"finished_at\":\"2026-11-11 20:40:00\"", finishedAt);

            var stream = TwitchTrackerTimeline.Parse(json);
            var segments = TwitchTrackerTimeline.BuildSegments(stream);

            Assert.Null(stream.End);
            Assert.Equal(2, segments.Count);
            Assert.Equal(Utc(16, 0), segments[0].End);
            Assert.Null(segments[1].End);
            Assert.Null(segments[1].LengthSeconds);

            var (start, length) = TwitchTrackerTimeline.GetRange([segments[1]]);
            Assert.Equal(Utc(16, 0), start);
            Assert.Null(length);
            Assert.Equal(new TimeSpan(1, 0, 33), TimeSpan.FromSeconds(TwitchTrackerTimeline.GetRange([segments[0]]).lengthSeconds!.Value));
        }

        [Theory]
        [InlineData("")]
        [InlineData("https://twitchtracker.com/forsen/streams/319170079719")]
        [InlineData("""{"games":[]}""")]
        [InlineData("""{"format":"something-else","created_at":"2026-11-11T14:59:27Z","channel":{"name":"forsen"},"games":[{"date":"2026-11-11 14:59:27","name":"Just Chatting"}]}""")]
        [InlineData("""{"format":"ttlc-twitchtracker/1","created_at":"2026-11-11T14:59:27Z","channel":{"name":"forsen"},"games":[]}""")]
        [InlineData("""{"format":"ttlc-twitchtracker/1","channel":{"name":"forsen"},"games":[{"date":"2026-11-11 14:59:27","name":"Just Chatting"}]}""")]
        public void RejectsInvalidData(string text)
        {
            Assert.Throws<FormatException>(() => TwitchTrackerTimeline.Parse(text));
        }

        [Fact]
        public void BookmarkletIsOneLine()
        {
            Assert.StartsWith("javascript:", TwitchTrackerTimeline.Bookmarklet);
            Assert.DoesNotContain('\n', TwitchTrackerTimeline.Bookmarklet);
            // A % would be URL-decoded when the browser runs the bookmark
            Assert.DoesNotContain('%', TwitchTrackerTimeline.Bookmarklet);
            Assert.Contains(TwitchTrackerTimeline.FORMAT, TwitchTrackerTimeline.Bookmarklet);
        }
    }
}
