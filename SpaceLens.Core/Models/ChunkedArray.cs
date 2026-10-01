namespace SpaceLens.Core.Models;

/// <summary>
/// Append-only array of structs stored in fixed-size chunks. Appends are lock-free except when a new
/// chunk has to be allocated, and existing elements never move, so references returned by the
/// indexer stay valid while other threads append. This avoids the copy-on-grow cost (and the
/// large-object-heap churn) of <see cref="List{T}"/> when holding millions of records.
/// </summary>
internal sealed class ChunkedArray<T> where T : struct
{
    private const int ChunkShift = 14;
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;
    private const int MaxChunks = 1 << 15; // ~536 million elements

    private readonly T[]?[] _chunks = new T[]?[MaxChunks];
    private readonly Lock _growLock = new();
    private int _count;

    /// <summary>Number of allocated slots. A slot may still be under construction by its writer.</summary>
    public int Count => Volatile.Read(ref _count);

    public ref T this[int index] => ref Volatile.Read(ref _chunks[index >> ChunkShift])![index & ChunkMask];

    /// <summary>Returns true when the slot's chunk exists (always true for indices obtained from <see cref="Allocate"/>).</summary>
    public bool IsMaterialized(int index) => Volatile.Read(ref _chunks[index >> ChunkShift]) is not null;

    public int Allocate()
    {
        int index = Interlocked.Increment(ref _count) - 1;
        int chunk = index >> ChunkShift;
        if (chunk >= MaxChunks)
        {
            throw new InvalidOperationException("Scan tree capacity exceeded.");
        }

        if (Volatile.Read(ref _chunks[chunk]) is null)
        {
            lock (_growLock)
            {
                if (_chunks[chunk] is null)
                {
                    Volatile.Write(ref _chunks[chunk], new T[ChunkSize]);
                }
            }
        }

        return index;
    }

    public long ApproximateBytes(int elementSize)
    {
        long chunks = (Count + ChunkSize - 1) / ChunkSize;
        return chunks * ChunkSize * elementSize;
    }
}
