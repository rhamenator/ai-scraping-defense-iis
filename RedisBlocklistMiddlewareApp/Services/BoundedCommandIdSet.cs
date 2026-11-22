namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Remembers the most recent command identifiers so a command applied twice in a short window is skipped, without growing
/// without bound: once <see cref="Capacity"/> identifiers are held the oldest is forgotten first.
/// </summary>
/// <remarks>
/// Every consensus mutation carries a fresh identifier, so an unbounded set only ever grew. Re-applying a block or an unblock
/// is idempotent for the materialized blocklist, so forgetting an old identifier cannot change the outcome; the window only
/// avoids needless work for near-term duplicates.
/// </remarks>
internal sealed class BoundedCommandIdSet
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, LinkedListNode<Guid>> _members;
    private readonly LinkedList<Guid> _order = new();

    public BoundedCommandIdSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _members = new Dictionary<Guid, LinkedListNode<Guid>>(Math.Min(capacity, 1024));
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _members.Count;
            }
        }
    }

    /// <summary>The number of entries in the eviction order; always equal to <see cref="Count"/>.</summary>
    internal int OrderLength
    {
        get
        {
            lock (_gate)
            {
                return _order.Count;
            }
        }
    }

    /// <summary>Records the identifier; false if it is already in the window.</summary>
    public bool TryAdd(Guid commandId)
    {
        lock (_gate)
        {
            if (_members.ContainsKey(commandId))
            {
                return false;
            }

            _members[commandId] = _order.AddLast(commandId);
            while (_members.Count > Capacity)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _members.Remove(oldest.Value);
            }

            return true;
        }
    }

    /// <summary>Forgets the identifier (used when applying the command failed, so a retry is not skipped).</summary>
    public void Remove(Guid commandId)
    {
        lock (_gate)
        {
            if (_members.Remove(commandId, out var node))
            {
                _order.Remove(node);
            }
        }
    }
}
