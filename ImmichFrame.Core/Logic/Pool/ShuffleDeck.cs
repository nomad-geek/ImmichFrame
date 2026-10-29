namespace ImmichFrame.Core.Logic.Pool;

/// <summary>
/// A shuffled deck of asset IDs: every member is dealt once per cycle before any is dealt again.
/// Members can be added or removed mid-cycle without restarting it. Not thread-safe; the owner locks.
/// </summary>
public sealed class ShuffleDeck(Random random)
{
    /// <summary>How many of the most recently dealt cards are kept out of the start of the next cycle.</summary>
    public const int RecentWindow = 25;

    private HashSet<Guid> _members = [];

    // The rest of the current cycle, dealt from the end.
    private readonly List<Guid> _undealt = [];

    // The last cards dealt, oldest first.
    private readonly List<Guid> _recent = [];
    private int _recentCapacity = RecentWindow;

    /// <summary>The ID list instance this deck was last synced with.</summary>
    public object? SyncedSource { get; set; }

    public int Count => _members.Count;

    public void Sync(IReadOnlyCollection<Guid> ids)
    {
        var next = new HashSet<Guid>(ids);

        if (_members.Count > 0)
        {
            var removed = new HashSet<Guid>(_members);
            removed.ExceptWith(next);
            if (removed.Count > 0)
            {
                _undealt.RemoveAll(removed.Contains);
                _recent.RemoveAll(removed.Contains);
            }
        }

        // Newcomers join the rest of the current cycle at a uniformly random position. With nothing
        // left in the cycle (including the first sync) they wait for the next one, which shuffles
        // every member.
        var cycleInProgress = _undealt.Count > 0;
        foreach (var id in ids)
        {
            if (_members.Add(id) && cycleInProgress)
            {
                _undealt.Insert(random.Next(_undealt.Count + 1), id);
            }
        }

        _members = next;
    }

    public IReadOnlyList<Guid> Deal(int n)
    {
        if (n <= 0 || _members.Count == 0)
        {
            return [];
        }

        // A batch never holds the same card twice.
        n = Math.Min(n, _members.Count);
        _recentCapacity = Math.Max(_recentCapacity, n);

        var dealt = new List<Guid>(n);
        while (dealt.Count < n)
        {
            if (_undealt.Count == 0)
            {
                Refill(need: n - dealt.Count, inBatch: dealt.Count);
            }

            var card = _undealt[^1];
            _undealt.RemoveAt(_undealt.Count - 1);
            dealt.Add(card);
            Remember(card);
        }

        return dealt;
    }

    private void Remember(Guid card)
    {
        _recent.Add(card);
        if (_recent.Count > _recentCapacity)
        {
            _recent.RemoveRange(0, _recent.Count - _recentCapacity);
        }
    }

    // Starts a new cycle. The most recently dealt cards are kept out of its start, so a batch that
    // spans the join never repeats a card and the end of one cycle doesn't reappear at the start of the next.
    private void Refill(int need, int inBatch)
    {
        var n = _members.Count;
        var avoidCount = Math.Min(Math.Max(inBatch, Math.Min(RecentWindow, n / 2)), n - need);

        var avoid = new List<Guid>(avoidCount);
        var avoidSet = new HashSet<Guid>();
        for (var i = _recent.Count - 1; i >= 0 && avoid.Count < avoidCount; i--)
        {
            var card = _recent[i];
            if (_members.Contains(card) && avoidSet.Add(card))
            {
                avoid.Add(card);
            }
        }

        var order = _members.Where(id => !avoidSet.Contains(id)).ToList();
        Shuffle(order);
        var guard = Math.Min(order.Count, Math.Max(RecentWindow, need));

        Shuffle(avoid);
        foreach (var card in avoid)
        {
            order.Insert(random.Next(guard, order.Count + 1), card);
        }

        if (n >= 2 && _recent.Count > 0 && order[0] == _recent[^1])
        {
            var other = random.Next(1, order.Count);
            (order[0], order[other]) = (order[other], order[0]);
        }

        order.Reverse();
        _undealt.AddRange(order);
    }

    private void Shuffle(List<Guid> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
