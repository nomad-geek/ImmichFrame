using ImmichFrame.Core.Api;

namespace ImmichFrame.Core.Logic.Pool;

/// <summary>A pool that can list every asset it selects and load assets by ID, so they can be dealt from a deck.</summary>
public interface IDealableAssetPool : IAssetPool
{
    /// <summary>
    /// The full current ID set. Returns the same list instance until the underlying cache refreshes,
    /// so callers can tell a refreshed list apart by reference.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAllAssetIds(CancellationToken ct = default);

    /// <summary>Loads these assets in the given order; missing or trashed ones are left out.</summary>
    Task<IReadOnlyList<AssetResponseDto>> LoadAssetsByIds(IReadOnlyList<Guid> ids, CancellationToken ct = default);
}
