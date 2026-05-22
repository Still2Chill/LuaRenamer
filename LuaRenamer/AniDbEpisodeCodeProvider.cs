using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Shoko.Abstractions.Metadata.Anidb;

namespace LuaRenamer;

public interface IAniDbEpisodeCodeProvider
{
    string? GetEpisodeCode(IAnidbEpisode episode, LuaRenamerSettings settings, CancellationToken cancellationToken);
}

public class AniDbEpisodeCodeProvider : IAniDbEpisodeCodeProvider
{
    private sealed record CacheEntry(DateTimeOffset CachedAt, IReadOnlyDictionary<int, string> EpisodeCodes);

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly ConcurrentDictionary<int, CacheEntry> AnimeCache = new();
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(2);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public string? GetEpisodeCode(IAnidbEpisode episode, LuaRenamerSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.EnableAniDbEpisodeCodeLookup ||
            string.IsNullOrWhiteSpace(settings.AniDbHttpClientName) ||
            settings.AniDbHttpClientVersion < 1)
        {
            return null;
        }

        try
        {
            return GetEpisodeCodeAsync(episode, settings, cancellationToken).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetEpisodeCodeAsync(IAnidbEpisode episode, LuaRenamerSettings settings, CancellationToken cancellationToken)
    {
        var cacheDuration = TimeSpan.FromHours(settings.AniDbEpisodeCodeCacheHours);

        if (TryGetCachedEpisodeCode(episode, cacheDuration, out var cachedCode))
            return cachedCode;

        var episodeCodes = await FetchAnimeEpisodeCodesAsync(episode.SeriesID, settings, cacheDuration, cancellationToken).ConfigureAwait(false);
        return episodeCodes.TryGetValue(episode.ID, out var code) ? code : null;
    }

    private static bool TryGetCachedEpisodeCode(IAnidbEpisode episode, TimeSpan cacheDuration, out string? code)
    {
        code = null;

        if (!AnimeCache.TryGetValue(episode.SeriesID, out var cached) ||
            DateTimeOffset.UtcNow - cached.CachedAt >= cacheDuration)
        {
            return false;
        }

        cached.EpisodeCodes.TryGetValue(episode.ID, out code);
        return true;
    }

    private static async Task<IReadOnlyDictionary<int, string>> FetchAnimeEpisodeCodesAsync(
        int animeId,
        LuaRenamerSettings settings,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (AnimeCache.TryGetValue(animeId, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < cacheDuration)
            {
                return cached.EpisodeCodes;
            }

            var nextAllowedRequestAt = _lastRequestAt + MinimumRequestInterval;
            var delay = nextAllowedRequestAt - DateTimeOffset.UtcNow;

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            _lastRequestAt = DateTimeOffset.UtcNow;

            var clientName = Uri.EscapeDataString(settings.AniDbHttpClientName.Trim().ToLowerInvariant());
            var requestUri =
                $"http://api.anidb.net:9001/httpapi?request=anime&client={clientName}&clientver={settings.AniDbHttpClientVersion}&protover=1&aid={animeId}";

            var xml = await HttpClient.GetStringAsync(requestUri, cancellationToken).ConfigureAwait(false);
            var episodeCodes = ParseEpisodeCodes(xml);
            AnimeCache[animeId] = new CacheEntry(DateTimeOffset.UtcNow, episodeCodes);
            return episodeCodes;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static IReadOnlyDictionary<int, string> ParseEpisodeCodes(string xml)
    {
        var document = XDocument.Parse(xml);

        if (document.Root?.Name.LocalName == "error")
            return new Dictionary<int, string>();

        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "episode")
            .Select(element =>
            {
                var idValue = element.Attribute("id")?.Value;
                var epno = element.Elements().FirstOrDefault(child => child.Name.LocalName == "epno")?.Value.Trim();

                return int.TryParse(idValue, out var id) && !string.IsNullOrWhiteSpace(epno)
                    ? new { id, epno }
                    : null;
            })
            .Where(item => item is not null)
            .ToDictionary(item => item!.id, item => item!.epno);
    }
}
