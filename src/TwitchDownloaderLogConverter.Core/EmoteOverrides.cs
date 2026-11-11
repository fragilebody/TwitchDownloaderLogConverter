using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderLogConverter.Core
{
    /// <summary>
    /// Lets users supply their own emote images (e.g. the original PogChamp, removed sub emotes, old BTTV emotes).
    /// Each image in a folder is embedded as a named emote, so it is used wherever its file name appears as a word in chat.
    /// </summary>
    public static class EmoteOverrides
    {
        public static readonly string[] SupportedExtensions = [".png", ".gif", ".webp", ".jpg", ".jpeg"];

        /// <summary>Base emote height in pixels at 1x scale, same as Twitch.</summary>
        private const double BASE_EMOTE_HEIGHT = 28;

        public const string ID_PREFIX = "override-";

        /// <summary>
        /// Loads every supported image in <paramref name="folder"/>. The file name without its extension is the emote name (case-sensitive).
        /// </summary>
        public static List<EmbedEmoteData> LoadFolder(string folder, ITaskLogger logger)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Emote override folder not found: {folder}");

            var overrides = new List<EmbedEmoteData>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            var files = Directory.EnumerateFiles(folder)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal);

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name) || name.Contains(' '))
                {
                    logger?.LogWarning($"Skipping emote override \"{Path.GetFileName(file)}\": the file name must be a single word.");
                    continue;
                }

                if (!names.Add(name))
                {
                    logger?.LogWarning($"Skipping emote override \"{Path.GetFileName(file)}\": another image is already named {name}.");
                    continue;
                }

                try
                {
                    overrides.Add(FromImage(name, File.ReadAllBytes(file)));
                }
                catch (Exception e)
                {
                    names.Remove(name);
                    logger?.LogWarning($"Skipping emote override \"{Path.GetFileName(file)}\": not a readable image ({e.Message}).");
                }
            }

            return overrides;
        }

        public static EmbedEmoteData FromImage(string name, byte[] imageData)
        {
            int width, height;
            using (var emote = new TwitchEmote(imageData, null, EmoteProvider.ThirdParty, 1, ID_PREFIX + name, name))
            {
                width = emote.Width;
                height = emote.Height;
            }

            // Images are drawn at height / imageScale, so pick the scale that makes them about as tall as a Twitch emote
            var imageScale = Math.Clamp((int)Math.Round(height / BASE_EMOTE_HEIGHT), 1, 4);

            return new EmbedEmoteData
            {
                id = ID_PREFIX + name,
                imageScale = imageScale,
                data = imageData,
                name = name,
                width = width / imageScale,
                height = height / imageScale
            };
        }

        /// <summary>
        /// Turns Twitch emote fragments whose text is an override name back into plain text, so the override (matched by name) is drawn instead.
        /// </summary>
        /// <returns>How many emote fragments were replaced</returns>
        public static int ApplyToComments(IEnumerable<Comment> comments, IReadOnlySet<string> overrideNames)
        {
            var replaced = 0;
            if (overrideNames is not { Count: > 0 })
                return replaced;

            foreach (var comment in comments)
            {
                var fragments = comment.message?.fragments;
                if (fragments is null || !fragments.Any(f => f.emoticon is not null && overrideNames.Contains(f.text)))
                    continue;

                var replacedIds = new HashSet<string>();
                var newFragments = new List<Fragment>(fragments.Count);
                foreach (var fragment in fragments)
                {
                    var isReplaced = fragment.emoticon is not null && overrideNames.Contains(fragment.text);
                    if (isReplaced)
                    {
                        replacedIds.Add(fragment.emoticon.emoticon_id);
                        replaced++;
                    }

                    if (fragment.emoticon is null || isReplaced)
                    {
                        // Merge with the previous text fragment
                        if (newFragments.Count > 0 && newFragments[^1].emoticon is null)
                        {
                            newFragments[^1].text += fragment.text;
                            continue;
                        }

                        newFragments.Add(new Fragment { text = fragment.text, emoticon = null });
                        continue;
                    }

                    newFragments.Add(fragment);
                }

                comment.message.fragments = newFragments;
                comment.message.emoticons?.RemoveAll(e => replacedIds.Contains(e._id));
            }

            return replaced;
        }

        /// <summary>
        /// Adds the overrides to the chat's embedded third-party emotes, replacing any embedded emote with the same name.
        /// </summary>
        public static void Embed(ChatRoot chatRoot, IReadOnlyCollection<EmbedEmoteData> overrides)
        {
            if (overrides is not { Count: > 0 })
                return;

            chatRoot.embeddedData ??= new EmbeddedData();
            var names = overrides.Select(o => o.name).ToHashSet(StringComparer.Ordinal);
            chatRoot.embeddedData.thirdParty.RemoveAll(e => names.Contains(e.name));
            // Overrides go first so they win any name conflict when the chat is loaded
            chatRoot.embeddedData.thirdParty.InsertRange(0, overrides);
        }
    }
}
