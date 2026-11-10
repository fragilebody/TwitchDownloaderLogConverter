using TwitchDownloaderCore;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderLogConverter.Core
{
    /// <summary>
    /// The commenter backfill and image embedding steps of TwitchDownloaderCore's ChatDownloader,
    /// rebuilt on its public APIs so the output matches a normal chat download.
    /// </summary>
    public static class ChatEnrichment
    {
        public static async Task BackfillCommenterInfoAsync(ChatRoot chatRoot, ITaskProgress progress)
        {
            // Best effort, same as ChatDownloader
            progress.SetTemplateStatus("Backfilling Commenter Info {0}%", 0);

            var userIds = chatRoot.comments.Select(x => x.commenter._id).Distinct().ToArray();
            var userInfo = new Dictionary<string, User>(userIds.Length);

            var failedInfo = false;
            const int BATCH_SIZE = 100;
            for (var i = 0; i < userIds.Length; i += BATCH_SIZE)
            {
                try
                {
                    var response = await TwitchHelper.GetUserInfo(userIds.Skip(i).Take(BATCH_SIZE));
                    foreach (var user in response.data.users)
                    {
                        if (user is not null)
                            userInfo[user.id] = user;
                    }

                    progress.ReportProgress((int)(Math.Min(i + BATCH_SIZE, userIds.Length) * 100f / userIds.Length));
                }
                catch (Exception e)
                {
                    progress.LogVerbose($"An error occurred while backfilling commenters {i}-{Math.Min(i + BATCH_SIZE, userIds.Length - 1)}: {e.Message}");
                    failedInfo = true;
                }
            }

            progress.ReportProgress(100);

            if (failedInfo)
                progress.LogInfo("Failed to backfill some commenter info");

            foreach (var comment in chatRoot.comments)
            {
                if (userInfo.TryGetValue(comment.commenter._id, out var user))
                {
                    comment.commenter.updated_at = user.updatedAt;
                    comment.commenter.created_at = user.createdAt;
                    comment.commenter.bio = user.description;
                    comment.commenter.logo = user.profileImageURL;
                }
            }
        }

        public static async Task EmbedImagesAsync(ChatRoot chatRoot, bool bttv, bool ffz, bool stv, string cacheDir, ITaskProgress progress, CancellationToken cancellationToken)
        {
            progress.SetTemplateStatus("Downloading Embed Images {0}%", 0);
            chatRoot.embeddedData = new EmbeddedData();

            var thirdPartyEmotes = await TwitchHelper.GetThirdPartyEmotes(chatRoot.comments, chatRoot.streamer.id, cacheDir, progress, bttv: bttv, ffz: ffz, stv: stv, cancellationToken: cancellationToken);
            progress.ReportProgress(25);
            var firstPartyEmotes = await TwitchHelper.GetEmotes(chatRoot.comments, cacheDir, progress, cancellationToken: cancellationToken);
            progress.ReportProgress(50);
            var twitchBadges = await TwitchHelper.GetChatBadges(chatRoot.comments, chatRoot.streamer.id, cacheDir, progress, cancellationToken: cancellationToken);
            progress.ReportProgress(75);
            var twitchBits = await TwitchHelper.GetBits(chatRoot.comments, cacheDir, chatRoot.streamer.id.ToString(), progress, cancellationToken: cancellationToken);
            progress.ReportProgress(100);

            cancellationToken.ThrowIfCancellationRequested();
            progress.SetStatus("Embedding Images");

            foreach (var emote in thirdPartyEmotes)
            {
                chatRoot.embeddedData.thirdParty.Add(new EmbedEmoteData
                {
                    id = emote.Id,
                    imageScale = emote.ImageScale,
                    data = emote.ImageData,
                    name = emote.Name,
                    width = emote.Width / emote.ImageScale,
                    height = emote.Height / emote.ImageScale,
                    isZeroWidth = emote.IsZeroWidth,
                });
            }

            foreach (var emote in firstPartyEmotes)
            {
                chatRoot.embeddedData.firstParty.Add(new EmbedEmoteData
                {
                    id = emote.Id,
                    imageScale = emote.ImageScale,
                    data = emote.ImageData,
                    width = emote.Width / emote.ImageScale,
                    height = emote.Height / emote.ImageScale,
                });
            }

            foreach (var badge in twitchBadges)
            {
                chatRoot.embeddedData.twitchBadges.Add(new EmbedChatBadge
                {
                    name = badge.Name,
                    versions = badge.VersionsData
                });
            }

            foreach (var bit in twitchBits)
            {
                var newBit = new EmbedCheerEmote
                {
                    prefix = bit.prefix,
                    tierList = new Dictionary<int, EmbedEmoteData>()
                };

                foreach (var (tier, emote) in bit.tierList)
                {
                    newBit.tierList.Add(tier, new EmbedEmoteData
                    {
                        id = emote.Id,
                        imageScale = emote.ImageScale,
                        data = emote.ImageData,
                        name = emote.Name,
                        width = emote.Width / emote.ImageScale,
                        height = emote.Height / emote.ImageScale
                    });
                }

                chatRoot.embeddedData.twitchBits.Add(newBit);
            }
        }
    }
}
