using ImmichFrame.Core.Api;

namespace ImmichFrame.Core.Logic.Pool;

/// <summary>
/// Deals a source's assets from a shuffled deck, so every asset is shown once before any repeats.
/// Each client gets its own deck; callers without a client ID share one.
/// </summary>
public class ShuffleDeckPool(IDealableAssetPool source, TimeProvider? timeProvider = null) : IAssetPool
{
    // The client ID comes from the caller, so the number and size of decks are bounded.
    public const int MaxDecks = 32;
    public const int MaxClientIdLength = 64;
    public static readonly TimeSpan IdleExpiry = TimeSpan.FromHours(24);

    // Rounds of extra dealing when some dealt assets could not be loaded (deleted or trashed).
    private const int MaxTopUpRounds = 3;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeckEntry> _decks = new(StringComparer.Ordinal);

    private sealed class DeckEntry(ShuffleDeck deck, DateTimeOffset lastUsed)
    {
        public ShuffleDeck Deck { get; } = deck;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }

    public Task<long> GetAssetCount(CancellationToken ct = default) => source.GetAssetCount(ct);

    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, string clientId = "", CancellationToken ct = default)
    {
        if (requested <= 0)
        {
            return [];
        }

        var key = NormalizeClientId(clientId);
        var ids = await source.GetAllAssetIds(ct);

        var assets = new List<AssetResponseDto>(requested);
        var seen = new HashSet<Guid>();
        for (var round = 0; round <= MaxTopUpRounds && assets.Count < requested; round++)
        {
            IReadOnlyList<Guid> dealt;
            lock (_gate)
            {
                var deck = GetOrCreateDeck(key);
                if (!ReferenceEquals(deck.SyncedSource, ids))
                {
                    deck.Sync(ids);
                    deck.SyncedSource = ids;
                }

                dealt = deck.Deal(requested - assets.Count).Where(seen.Add).ToList();
            }

            if (dealt.Count == 0)
            {
                break;
            }

            assets.AddRange(await source.LoadAssetsByIds(dealt, ct));
        }

        return assets;
    }

    internal int DeckCount
    {
        get
        {
            lock (_gate)
            {
                return _decks.Count;
            }
        }
    }

    internal static string NormalizeClientId(string? clientId)
    {
        var key = clientId?.Trim() ?? string.Empty;
        return key.Length > MaxClientIdLength ? key[..MaxClientIdLength] : key;
    }

    private ShuffleDeck GetOrCreateDeck(string key)
    {
        var now = _time.GetUtcNow();

        foreach (var expired in _decks.Where(d => now - d.Value.LastUsed > IdleExpiry).Select(d => d.Key).ToList())
        {
            _decks.Remove(expired);
        }

        if (_decks.TryGetValue(key, out var entry))
        {
            entry.LastUsed = now;
            return entry.Deck;
        }

        while (_decks.Count >= MaxDecks)
        {
            var leastRecent = _decks.MinBy(d => d.Value.LastUsed).Key;
            _decks.Remove(leastRecent);
        }

        entry = new DeckEntry(new ShuffleDeck(new Random()), now);
        _decks[key] = entry;
        return entry.Deck;
    }
}
