using SkiaSharp;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderLogConverter.Core;

namespace TwitchDownloaderLogConverter.Tests
{
    public class EmoteOverridesTests
    {
        private static byte[] CreatePng(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(SKColors.Orange);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private static Comment CreateComment(params Fragment[] fragments) => new()
        {
            _id = Guid.NewGuid().ToString(),
            commenter = new Commenter { display_name = "viewer", name = "viewer", _id = "1" },
            message = new Message
            {
                body = string.Concat(fragments.Select(f => f.text)),
                fragments = fragments.ToList(),
                emoticons = fragments.Where(f => f.emoticon is not null)
                    .Select(f => new Emoticon2 { _id = f.emoticon.emoticon_id }).ToList(),
                user_badges = []
            }
        };

        private static Fragment Emote(string text, string id) => new() { text = text, emoticon = new Emoticon { emoticon_id = id } };
        private static Fragment Text(string text) => new() { text = text };

        [Theory]
        [InlineData(28, 1)]
        [InlineData(56, 2)]
        [InlineData(112, 4)]
        [InlineData(500, 4)]
        [InlineData(10, 1)]
        public void PicksScaleFromImageHeight(int height, int expectedScale)
        {
            var emote = EmoteOverrides.FromImage("PogChamp", CreatePng(height, height));

            Assert.Equal("PogChamp", emote.name);
            Assert.Equal("override-PogChamp", emote.id);
            Assert.Equal(expectedScale, emote.imageScale);
            Assert.Equal(height / expectedScale, emote.height);
            Assert.Equal(height / expectedScale, emote.width);
        }

        [Fact]
        public void LoadsOnlyValidImagesFromFolder()
        {
            var folder = Directory.CreateTempSubdirectory("emote-overrides-").FullName;
            try
            {
                File.WriteAllBytes(Path.Combine(folder, "PogChamp.png"), CreatePng(112, 112));
                File.WriteAllBytes(Path.Combine(folder, "two words.png"), CreatePng(28, 28));
                File.WriteAllBytes(Path.Combine(folder, "Broken.png"), [1, 2, 3, 4]);
                File.WriteAllText(Path.Combine(folder, "notes.txt"), "not an image");

                var overrides = EmoteOverrides.LoadFolder(folder, null);

                var emote = Assert.Single(overrides);
                Assert.Equal("PogChamp", emote.name);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void ReplacesOnlyOverriddenTwitchEmotes()
        {
            var comment = CreateComment(Emote("PogChamp", "305954156"), Text(" hi "), Emote("Kappa", "25"), Text(" "), Emote("PogChamp", "305954156"));

            var replaced = EmoteOverrides.ApplyToComments([comment], new HashSet<string> { "PogChamp" });

            Assert.Equal(2, replaced);
            Assert.Collection(comment.message.fragments,
                f => { Assert.Equal("PogChamp hi ", f.text); Assert.Null(f.emoticon); },
                f => { Assert.Equal("Kappa", f.text); Assert.Equal("25", f.emoticon.emoticon_id); },
                f => { Assert.Equal(" PogChamp", f.text); Assert.Null(f.emoticon); });
            Assert.Equal("25", Assert.Single(comment.message.emoticons)._id);
            Assert.Equal("PogChamp hi Kappa PogChamp", string.Concat(comment.message.fragments.Select(f => f.text)));
        }

        [Fact]
        public void EmbedReplacesSameNamedEmotes()
        {
            var chatRoot = new ChatRoot
            {
                embeddedData = new EmbeddedData
                {
                    thirdParty = [new EmbedEmoteData { id = "bttv1", name = "gachiGASM" }, new EmbedEmoteData { id = "bttv2", name = "OMEGALUL" }]
                }
            };

            EmoteOverrides.Embed(chatRoot, [new EmbedEmoteData { id = "override-gachiGASM", name = "gachiGASM" }]);

            Assert.Collection(chatRoot.embeddedData.thirdParty,
                e => Assert.Equal("override-gachiGASM", e.id),
                e => Assert.Equal("bttv2", e.id));
        }

        [Fact]
        public void EmbedCreatesEmbeddedDataWhenMissing()
        {
            var chatRoot = new ChatRoot();

            EmoteOverrides.Embed(chatRoot, [new EmbedEmoteData { id = "override-PogChamp", name = "PogChamp" }]);

            Assert.Equal("PogChamp", Assert.Single(chatRoot.embeddedData.thirdParty).name);
        }
    }
}
