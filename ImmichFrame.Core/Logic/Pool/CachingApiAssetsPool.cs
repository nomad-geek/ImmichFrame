using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public abstract class CachingApiAssetsPool(IApiCache apiCache) : IDealableAssetPool
{
    private readonly Random _random = new();
    private readonly object _indexGate = new();
    private AssetIndex? _index;

    // IDs and a lookup built from one instance of the cached list, so the ID list instance stays
    // the same until the cache reloads.
    private sealed record AssetIndex(IEnumerable<AssetResponseDto> Source, IReadOnlyList<Guid> Ids, IReadOnlyDictionary<Guid, AssetResponseDto> ById);

    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        return (await AllAssets(ct)).Count();
    }

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, string clientId = "", CancellationToken ct = default)
    {
        return (await AllAssets(ct)).OrderBy(_ => _random.Next()).Take(requested);
    }

    public async Task<IReadOnlyList<Guid>> GetAllAssetIds(CancellationToken ct = default)
    {
        return (await GetIndex(ct)).Ids;
    }

    public async Task<IReadOnlyList<AssetResponseDto>> LoadAssetsByIds(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var index = await GetIndex(ct);
        return ids.Where(index.ById.ContainsKey).Select(id => index.ById[id]).ToList();
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

                _index = new AssetIndex(all, byId.Keys.ToList(), byId);
            }

            return _index;
        }
    }

    private async Task<IEnumerable<AssetResponseDto>> AllAssets(CancellationToken ct = default)
    {
        return await apiCache.GetOrAddAsync(GetType().FullName!, () => LoadAssets(ct));
    }

    protected abstract Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default);
}