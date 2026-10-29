using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public abstract class CachingApiAssetsPool(IApiCache apiCache) : IDealableAssetPool
{
    private readonly Random _random = new();
    private readonly object _indexGate = new();
    private AssetIndex? _index;

    private readonly object _loadGate = new();
    private Task<IEnumerable<AssetResponseDto>>? _loading;

    // IDs and a lookup built from one instance of the cached list, so the listing stays the same
    // instance (and version) until the cache reloads.
    private sealed record AssetIndex(IEnumerable<AssetResponseDto> Source, AssetIdList Ids, IReadOnlyDictionary<Guid, AssetResponseDto> ById);

    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        return (await AllAssets(ct)).Count();
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, string clientId = "", CancellationToken ct = default)
    {
        return (await AllAssets(ct)).OrderBy(_ => _random.Next()).Take(requested);
    }

    public async Task<AssetIdList> GetAllAssetIds(CancellationToken ct = default)
    {
        return (await GetIndex(ct)).Ids;
    }

    public async Task<AssetLoadResult> LoadAssetsByIds(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var index = await GetIndex(ct);
        return AssetLoadResult.Of(ids.Where(index.ById.ContainsKey).Select(id => index.ById[id]).ToList());
    }

    private async Task<AssetIndex> GetIndex(CancellationToken ct)
    {
        var all = await AllAssets(ct);
        lock (_indexGate)
        {
            if (_index == null || !ReferenceEquals(_index.Source, all))
            {
                var byId = new Dictionary<Guid, AssetResponseDto>();
                foreach (var asset in all)
                {
                    byId.TryAdd(asset.Id, asset);
                }

                _index = new AssetIndex(all, new AssetIdList(byId.Keys), byId);
            }

            return _index;
        }
    }

    private async Task<IEnumerable<AssetResponseDto>> AllAssets(CancellationToken ct = default)
    {
        return await apiCache.GetOrAddAsync(GetType().FullName!, () => LoadOnce().WaitAsync(ct));
    }

    // Callers that miss the cache together share one load, which no single caller can cancel.
    private Task<IEnumerable<AssetResponseDto>> LoadOnce()
    {
        lock (_loadGate)
        {
            return _loading ??= Task.Run(async () =>
            {
                try
                {
                    return await LoadAssets(CancellationToken.None);
                }
                finally
                {
                    lock (_loadGate)
                    {
                        _loading = null;
                    }
                }
            });
        }
    }

    protected abstract Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default);
}
