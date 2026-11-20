namespace GatewayService.Resilience;

/// <summary>
/// Циклический буфер фиксированной ёмкости на массиве.
/// При переполнении новый элемент затирает самый старый.
/// Класс не потокобезопасен — синхронизация на стороне вызывающего (CircuitBreaker).
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _head; // индекс самого старого элемента
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;
    public bool IsEmpty => _count == 0;
    public bool IsFull => _count == _items.Length;

    public T Oldest => IsEmpty ? throw new InvalidOperationException("Buffer is empty") : _items[_head];

    public T Newest => IsEmpty
        ? throw new InvalidOperationException("Buffer is empty")
        : _items[(_head + _count - 1) % _items.Length];

    public void Add(T item)
    {
        var tail = (_head + _count) % _items.Length;
        _items[tail] = item;

        if (IsFull)
            _head = (_head + 1) % _items.Length; // затёрли самый старый — сдвигаем голову
        else
            _count++;
    }

    public T RemoveOldest()
    {
        var item = Oldest;
        _items[_head] = default!;
        _head = (_head + 1) % _items.Length;
        _count--;
        return item;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
    }

    /// <summary>Элементы от самого старого к самому новому.</summary>
    public IEnumerable<T> Items()
    {
        for (var i = 0; i < _count; i++)
            yield return _items[(_head + i) % _items.Length];
    }
}
