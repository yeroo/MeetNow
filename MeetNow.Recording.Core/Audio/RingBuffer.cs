namespace MeetNow.Recording.Core.Audio;

/// <summary>
/// Thread-safe circular buffer of PCM samples (16-bit signed).
/// Used as a rolling pre-buffer so speech onset is never clipped.
/// </summary>
public class RingBuffer
{
    private readonly short[] _buffer;
    private readonly int _capacity;
    private int _writePos;
    private int _count;
    private readonly object _lock = new();

    public RingBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new short[capacity];
    }

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Write(ReadOnlySpan<short> data)
    {
        lock (_lock)
        {
            var source = data;
            if (source.Length > _capacity)
            {
                source = source[^_capacity..];
                _writePos = 0;
                _count = 0;
            }

            foreach (var sample in source)
            {
                _buffer[_writePos] = sample;
                _writePos = (_writePos + 1) % _capacity;
                if (_count < _capacity)
                    _count++;
            }
        }
    }

    /// <summary>
    /// Returns all buffered samples in chronological order and resets the buffer.
    /// </summary>
    public short[] Drain()
    {
        lock (_lock)
        {
            if (_count == 0)
                return [];

            var result = new short[_count];
            var readPos = (_writePos - _count + _capacity) % _capacity;

            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(readPos + i) % _capacity];
            }

            _count = 0;
            _writePos = 0;

            return result;
        }
    }
}
