using System.Collections.Concurrent;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichFrame.Core.Logic.Pool;

public class AccountSearchPool : IDealableAssetPool, IDisposable
{
    /// <summary>
    /// The shortest time an ID listing is kept. Listing pages through the whole selection, so even
    /// with <c>RefreshAlbumPeopleInterval: 0</c> it runs at most this often.
    /// </summary>
    public static readonly TimeSpan MinIdListLifetime = TimeSpan.FromMinutes(5);

    // The largest page the metadata search allows.
    private const int IdPageSize = 1000;
    private const int MaxParallelLoads = 4;

    private readonly IApiCache _apiCache;
    private readonly ImmichApi _immichApi;
    private readonly IAccountSettings _accountSettings;
    private readonly ILogger _logger;
    private readonly RefreshingValue<AssetIdList> _ids;

    public AccountSearchPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings,
        TimeSpan? idListLifetime = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        _apiCache = apiCache;
        _immichApi = immichApi;
        _accountSettings = accountSettings;
        _logger = logger ?? NullLogger.Instance;
        var lifetime = idListLifetime is { } requested && requested > MinIdListLifetime ? requested : MinIdListLifetime;
        _ids = new RefreshingValue<AssetIdList>(ListAssetIds, lifetime, timeProvider);
    }

    /// <summary>How long an ID listing is kept before it is refreshed.</summary>
    public TimeSpan IdListLifetime => _ids.Lifetime;

    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (!SelectsAnything(tagIds))
        {
            return 0;
        }

        var filter = SearchFilters.ForAccount(_accountSettings, tagIds);
        return await _apiCache.GetOrAddAsync("stats", async () =>
        {
            var stats = await _immichApi.SearchAssetStatisticsAsync(new StatisticsSearchDto
            {
                Filter = filter
            }, ct);
            return stats.Total;
        });
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, string clientId = "", CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (!SelectsAnything(tagIds))
        {
            return [];
        }

        return await _immichApi.SearchRandomAsync(new RandomSearchDto
        {
            Size = requested,
            WithExif = true,
            WithPeople = true,
            Filter = SearchFilters.ForAccount(_accountSettings, tagIds)
        }, ct);
    }

    public async Task<AssetIdList> GetAllAssetIds(CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (!SelectsAnything(tagIds))
        {
            return new AssetIdList([]);
        }

        return await _ids.GetAsync(ct);
    }

    // One listing at a time per pool, shared by every caller; ct is the pool's own token.
    private async Task<AssetIdList> ListAssetIds(CancellationToken ct)
    {
        var filter = SearchFilters.ForAccount(_accountSettings, await ResolveTagIds(ct));
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var response = await _immichApi.SearchAssetsAsync(null, null, new MetadataSearchDto
            {
                Filter = filter,
                Size = IdPageSize,
                Cursor = cursor,
                WithExif = false,
                WithPeople = false
            }, ct);

            ids.AddRange(response.Assets.Items.Select(a => a.Id));
            cursor = response.Assets.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));

        return new AssetIdList(ids);
    }

    /// <summary>
    /// Loads each asset through the account's filter plus an ID filter, so an asset that has left
    /// the selection since the listing (removed from the album, unfavourited, archived, retagged,
    /// trashed) is left out rather than shown.
    /// </summary>
    public async Task<AssetLoadResult> LoadAssetsByIds(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (!SelectsAnything(tagIds))
        {
            return AssetLoadResult.Of([]);
        }

        var loaded = new AssetResponseDto?[ids.Count];
        var retry = new ConcurrentBag<int>();
        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLoads, CancellationToken = ct },
            async (i, token) =>
            {
                try
                {
                    loaded[i] = await LoadSelected(ids[i], tagIds, token);
                }
                catch (ApiException e) when (e.StatusCode is 400 or 403 or 404)
                {
                    // Deleted, locked or no longer shared since the listing.
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    _logger.LogWarning("Could not load asset {assetId}; it will be dealt again later", ids[i]);
                    retry.Add(i);
                }
            });

        return new AssetLoadResult(
            loaded.OfType<AssetResponseDto>().ToList(),
            retry.Order().Select(i => ids[i]).ToList());
    }

    private async Task<AssetResponseDto?> LoadSelected(Guid id, IReadOnlyCollection<Guid> tagIds, CancellationToken ct)
    {
        var filter = SearchFilters.ForAccount(_accountSettings, tagIds);
        filter.Id = new IdFilter { Eq = id };
        var response = await _immichApi.SearchAssetsAsync(null, null, new MetadataSearchDto
        {
            Filter = filter,
            Size = 1,
            WithExif = true,
            WithPeople = true
        }, ct);

        var asset = response.Assets.Items.FirstOrDefault(a => a.Id == id);
        return asset is { IsTrashed: not true } ? asset : null;
    }

    private bool SelectsAnything(IReadOnlyCollection<Guid> tagIds) =>
        !SearchFilters.HasConfiguredSource(_accountSettings) || SearchFilters.HasMatchingBranch(_accountSettings, tagIds);

    private async Task<IReadOnlyList<Guid>> ResolveTagIds(CancellationToken ct)
    {
        if (_accountSettings.Tags is not { Count: > 0 } values)
        {
            return [];
        }

        var allTags = await _apiCache.GetOrAddAsync($"allTags_{_accountSettings.ImmichServerUrl}",
            () => _immichApi.GetAllTagsAsync(ct));
        var tagValueToTag = allTags.ToDictionary(t => t.Value);
        return values
            .Where(tagValueToTag.ContainsKey)
            .Select(value => tagValueToTag[value].Id)
            .ToList();
    }

    public void Dispose() => _ids.Dispose();
}
