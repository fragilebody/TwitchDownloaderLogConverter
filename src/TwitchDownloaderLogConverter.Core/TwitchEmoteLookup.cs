using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TwitchDownloaderCore.Interfaces;

namespace TwitchDownloaderLogConverter.Core
{
    /// <summary>
    /// Looks up the current Twitch global emotes and a channel's subscriber emotes by name.
    /// </summary>
    public static class TwitchEmoteLookup
    {
        // The public web client id, same as TwitchDownloaderCore uses
        private const string CLIENT_ID = "kimne78kx3ncx6brgo4mv6wki5h1ko";
        private const string GLOBAL_EMOTE_SET_ID = "0";

        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        /// <returns>Emote id by emote name (case-sensitive). Global emotes take priority over channel emotes with the same name.</returns>
        public static async Task<IReadOnlyDictionary<string, string>> GetEmoteIdsByNameAsync(int channelId, string channelLogin, ITaskLogger logger, CancellationToken cancellationToken)
        {
            var emotes = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                var global = await QueryAsync("query{emoteSet(id:\"" + GLOBAL_EMOTE_SET_ID + "\"){emotes{id token}}}", cancellationToken);
                AddEmotes(emotes, global.GetProperty("data").GetProperty("emoteSet").GetProperty("emotes"));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogWarning($"Unable to fetch Twitch global emotes: {e.Message}");
            }

            var userSelector = channelId > 0
                ? $"id:\"{channelId}\""
                : !string.IsNullOrWhiteSpace(channelLogin) ? $"login:{JsonSerializer.Serialize(channelLogin)}" : null;

            if (userSelector is not null)
            {
                try
                {
                    var channel = await QueryAsync($"query{{user({userSelector}){{subscriptionProducts{{emotes{{id token}}}}}}}}", cancellationToken);
                    if (channel.GetProperty("data").GetProperty("user") is { ValueKind: JsonValueKind.Object } user
                        && user.GetProperty("subscriptionProducts") is { ValueKind: JsonValueKind.Array } products)
                    {
                        foreach (var product in products.EnumerateArray())
                        {
                            if (product.TryGetProperty("emotes", out var productEmotes))
                                AddEmotes(emotes, productEmotes);
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger?.LogWarning($"Unable to fetch channel emotes: {e.Message}");
                }
            }

            return emotes;
        }

        private static void AddEmotes(Dictionary<string, string> emotes, JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array)
                return;

            foreach (var emote in array.EnumerateArray())
            {
                var id = emote.GetProperty("id").GetString();
                var token = emote.GetProperty("token").GetString();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(token))
                    emotes.TryAdd(token, id);
            }
        }

        private static async Task<JsonElement> QueryAsync(string query, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["query"] = query }), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Client-ID", CLIENT_ID);

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return doc.RootElement.Clone();
        }
    }
}
