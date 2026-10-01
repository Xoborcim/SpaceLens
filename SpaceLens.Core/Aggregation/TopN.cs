namespace SpaceLens.Core.Aggregation;

/// <summary>
/// Keeps the N items with the largest keys using a bounded min-heap: O(log N) per accepted item,
/// O(1) rejection for items smaller than the current minimum. Not thread-safe.
/// </summary>
public sealed class TopN<T>
{
    private readonly (long Key, T Value)[] _heap;
    private int _count;

    public TopN(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _heap = new (long, T)[capacity];
    }

    public int Capacity => _heap.Length;

    public int Count => _count;

    /// <summary>Smallest key currently retained, or <see cref="long.MinValue"/> while not full.</summary>
    public long Threshold => _count < _heap.Length ? long.MinValue : _heap[0].Key;

    public bool Offer(long key, T value)
    {
        if (_count < _heap.Length)
        {
            _heap[_count] = (key, value);
            SiftUp(_count++);
            return true;
        }

        if (key <= _heap[0].Key)
        {
            return false;
        }

        _heap[0] = (key, value);
        SiftDown(0);
        return true;
    }

    public void Clear() => _count = 0;

    /// <summary>Returns retained items ordered by descending key.</summary>
    public List<(long Key, T Value)> ToSortedList()
    {
        var list = new List<(long Key, T Value)>(_count);
        for (int i = 0; i < _count; i++)
        {
            list.Add(_heap[i]);
        }

        list.Sort(static (a, b) => b.Key.CompareTo(a.Key));
        return list;
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (_heap[parent].Key <= _heap[i].Key)
            {
                break;
            }

            (_heap[parent], _heap[i]) = (_heap[i], _heap[parent]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = 2 * i + 1;
            if (left >= _count)
            {
                break;
            }

            int smallest = left;
            int right = left + 1;
            if (right < _count && _heap[right].Key < _heap[left].Key)
            {
                smallest = right;
            }

            if (_heap[i].Key <= _heap[smallest].Key)
            {
                break;
            }

            (_heap[i], _heap[smallest]) = (_heap[smallest], _heap[i]);
            i = smallest;
        }
    }
}

/// <summary>
/// Thread-safe wrapper over <see cref="TopN{T}"/>. Offers below the current threshold are rejected
/// without taking the lock, which is the common case once the heap is full.
/// </summary>
public sealed class ConcurrentTopN<T>
{
    private readonly TopN<T> _inner;
    private readonly Lock _lock = new();
    private long _threshold = long.MinValue;

    public ConcurrentTopN(int capacity) => _inner = new TopN<T>(capacity);

    public void Offer(long key, T value)
    {
        if (key <= Volatile.Read(ref _threshold))
        {
            return;
        }

        lock (_lock)
        {
            if (_inner.Offer(key, value))
            {
                Volatile.Write(ref _threshold, _inner.Threshold);
            }
        }
    }

    public List<(long Key, T Value)> Snapshot()
    {
        lock (_lock)
        {
            return _inner.ToSortedList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _inner.Clear();
            Volatile.Write(ref _threshold, long.MinValue);
        }
    }
}
