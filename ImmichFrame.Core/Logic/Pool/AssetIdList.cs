using ImmichFrame.Core.Api;

namespace ImmichFrame.Core.Logic.Pool;

/// <summary>
/// One listing of a source's asset IDs, shared by every deck dealt from it. Decks keep only
/// their own undealt order and look membership up here, so a listing is held once in memory.
/// </summary>
public sealed class AssetIdList
{
    private static long _lastVersion;

    public AssetIdList(IEnumerable<Guid> ids)
    {
        var members = new HashSet<Guid>();
        var ordered = new List<Guid>();
        foreach (var id in ids)
        {
            if (members.Add(id))
            {
                ordered.Add(id);
            }
        }

        Ids = ordered;
        Members = members;
        Version = Interlocked.Increment(ref _lastVersion);
    }

    /// <summary>Grows with every listing made, so a deck can tell a newer listing from an older one.</summary>
    public long Version { get; }

    /// <summary>The distinct IDs, in listing order.</summary>
    public IReadOnlyList<Guid> Ids { get; }

    public IReadOnlySet<Guid> Members { get; }

    public int Count => Ids.Count;
}

/// <summary>The outcome of loading dealt assets by ID.</summary>
/// <param name="Loaded">The assets that can be shown, in the requested order.</param>
/// <param name="Retry">
/// IDs that could not be loaded for a reason that may pass (a server error, a timeout). They go
/// back to the deck to be dealt again later. IDs left out of both lists are gone for good
/// (deleted, trashed, or no longer matching the account's filter).
/// </param>
public sealed record AssetLoadResult(IReadOnlyList<AssetResponseDto> Loaded, IReadOnlyList<Guid> Retry)
{
    public static AssetLoadResult Of(IReadOnlyList<AssetResponseDto> loaded) => new(loaded, []);
}
