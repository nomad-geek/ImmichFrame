using ImmichFrame.Core.Api;
using ImmichFrame.Core.Helpers;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public class AccountSearchPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : IDealableAssetPool
{
    // The largest page the metadata search allows.
    private const int IdPageSize = 1000;
    private const int MaxParallelLoads = 4;

    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (SearchFilters.HasConfiguredSource(accountSettings) && !SearchFilters.HasMatchingBranch(accountSettings, tagIds))
        {
            return 0;
        }

        var filter = SearchFilters.ForAccount(accountSettings, tagIds);
        return await apiCache.GetOrAddAsync("stats", async () =>
        {
            var stats = await immichApi.SearchAssetStatisticsAsync(new StatisticsSearchDto
            {
                Filter = filter
            }, ct);
            return stats.Total;
        });
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, string clientId = "", CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (SearchFilters.HasConfiguredSource(accountSettings) && !SearchFilters.HasMatchingBranch(accountSettings, tagIds))
        {
            return [];
        }

        return await immichApi.SearchRandomAsync(new RandomSearchDto
        {
            Size = requested,
            WithExif = true,
            WithPeople = true,
            Filter = SearchFilters.ForAccount(accountSettings, tagIds)
        }, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetAllAssetIds(CancellationToken ct = default)
    {
        var tagIds = await ResolveTagIds(ct);
        if (SearchFilters.HasConfiguredSource(accountSettings) && !SearchFilters.HasMatchingBranch(accountSettings, tagIds))
        {
            return [];
        }

        var filter = SearchFilters.ForAccount(accountSettings, tagIds);
        return await apiCache.GetOrAddAsync<IReadOnlyList<Guid>>("assetIds", async () =>
        {
            var ids = new List<Guid>();
            string? cursor = null;
            do
            {
                var response = await immichApi.SearchAssetsAsync(null, null, new MetadataSearchDto
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

            return ids;
        });
    }

    public async Task<IReadOnlyList<AssetResponseDto>> LoadAssetsByIds(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var loaded = new AssetResponseDto?[ids.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLoads, CancellationToken = ct },
            async (i, token) =>
            {
                try
                {
                    var asset = await immichApi.GetAssetInfoAsync(ids[i], null, null, token);
                    loaded[i] = asset.IsTrashed == true ? null : asset;
                }
                catch (ApiException e) when (e.StatusCode == 404)
                {
                    // Deleted since the ID list was built.
                }
            });

        return loaded.OfType<AssetResponseDto>().ToList();
    }

    private async Task<IReadOnlyList<Guid>> ResolveTagIds(CancellationToken ct)
    {
        if (accountSettings.Tags is not { Count: > 0 } values)
        {
            return [];
        }

        var allTags = await apiCache.GetOrAddAsync($"allTags_{accountSettings.ImmichServerUrl}",
            () => immichApi.GetAllTagsAsync(ct));
        var tagValueToTag = allTags.ToDictionary(t => t.Value);
        return values
            .Where(tagValueToTag.ContainsKey)
            .Select(value => tagValueToTag[value].Id)
            .ToList();
    }
}
