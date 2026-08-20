using System.Collections;

namespace Monitor.Core.Collections;

/// <summary>
/// 固定容量のリングバッファ。容量を超えて Add すると最古の要素が押し出される。
/// スレッド安全ではない。複数スレッドから使う場合は呼び出し側が <see cref="SyncRoot"/> でロックすること。
/// </summary>
public sealed class RingBuffer<T> : IEnumerable<T>
{
    private readonly T[] _items;
    private int _start; // インデックス 0 (最古) が位置する物理スロット
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        Capacity = capacity;
        _items = new T[capacity];
        _start = 0;
        _count = 0;
    }

    public int Capacity { get; }

    public int Count => _count;

    /// <summary>
    /// このバッファへのアクセスを外部から直列化するためのロック対象オブジェクト。
    /// </summary>
    public object SyncRoot { get; } = new object();

    /// <summary>
    /// 0 が最古の要素を指すインデクサ。
    /// </summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index was out of range.");
            }

            return _items[PhysicalIndex(index)];
        }
    }

    public void Add(T item)
    {
        int writeIndex;
        if (_count < Capacity)
        {
            writeIndex = PhysicalIndex(_count);
            _count++;
        }
        else
        {
            writeIndex = _start;
            _start = Advance(_start, 1);
        }

        _items[writeIndex] = item;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        _count = 0;
    }

    public void CopyTo(Span<T> destination)
    {
        if (destination.Length < _count)
        {
            throw new ArgumentException("Destination span is too short.", nameof(destination));
        }

        for (int i = 0; i < _count; i++)
        {
            destination[i] = _items[PhysicalIndex(i)];
        }
    }

    private int PhysicalIndex(int logicalIndex) => Advance(_start, logicalIndex);

    private int Advance(int index, int by)
    {
        int result = index + by;
        if (result >= Capacity)
        {
            result -= Capacity;
        }

        return result;
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// アロケーションフリーな列挙子。スパークライン描画など毎フレーム列挙する用途向け。
    /// </summary>
    public struct Enumerator : IEnumerator<T>
    {
        private readonly RingBuffer<T> _buffer;
        private int _index;

        internal Enumerator(RingBuffer<T> buffer)
        {
            _buffer = buffer;
            _index = -1;
        }

        public readonly T Current => _buffer[_index];

        readonly object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            int next = _index + 1;
            if (next >= _buffer._count)
            {
                return false;
            }

            _index = next;
            return true;
        }

        public void Reset() => _index = -1;

        public readonly void Dispose()
        {
        }
    }
}
