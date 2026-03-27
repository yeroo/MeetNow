using MeetNow.Recording.Core.Audio;
using Xunit;

namespace MeetNow.Recording.Core.Tests.Audio;

public class RingBufferTests
{
    [Fact]
    public void Write_ThenDrain_ReturnsWrittenSamples()
    {
        var buffer = new RingBuffer(capacity: 10);
        short[] data = [1, 2, 3, 4, 5];
        buffer.Write(data);
        var result = buffer.Drain();
        Assert.Equal(data, result);
    }

    [Fact]
    public void Write_OverCapacity_OverwritesOldestSamples()
    {
        var buffer = new RingBuffer(capacity: 4);
        buffer.Write([1, 2, 3]);
        buffer.Write([4, 5, 6]);
        var result = buffer.Drain();
        Assert.Equal([3, 4, 5, 6], result);
    }

    [Fact]
    public void Drain_EmptyBuffer_ReturnsEmptyArray()
    {
        var buffer = new RingBuffer(capacity: 10);
        var result = buffer.Drain();
        Assert.Empty(result);
    }

    [Fact]
    public void Drain_ClearsBuffer()
    {
        var buffer = new RingBuffer(capacity: 10);
        buffer.Write([1, 2, 3]);
        buffer.Drain();
        var result = buffer.Drain();
        Assert.Empty(result);
    }

    [Fact]
    public void Write_ExactCapacity_ReturnsAll()
    {
        var buffer = new RingBuffer(capacity: 5);
        buffer.Write([1, 2, 3, 4, 5]);
        var result = buffer.Drain();
        Assert.Equal([1, 2, 3, 4, 5], result);
    }

    [Fact]
    public void Write_WrapAround_MaintainsOrder()
    {
        var buffer = new RingBuffer(capacity: 5);
        buffer.Write([1, 2, 3, 4, 5]);
        buffer.Write([6, 7]);
        var result = buffer.Drain();
        Assert.Equal([3, 4, 5, 6, 7], result);
    }

    [Fact]
    public void Count_ReflectsWrittenSamples()
    {
        var buffer = new RingBuffer(capacity: 10);
        Assert.Equal(0, buffer.Count);
        buffer.Write([1, 2, 3]);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Count_CapsAtCapacity()
    {
        var buffer = new RingBuffer(capacity: 4);
        buffer.Write([1, 2, 3, 4, 5, 6]);
        Assert.Equal(4, buffer.Count);
    }

    [Fact]
    public void Write_LargerThanCapacity_KeepsLastCapacitySamples()
    {
        var buffer = new RingBuffer(capacity: 3);
        buffer.Write([1, 2, 3, 4, 5, 6, 7]);
        var result = buffer.Drain();
        Assert.Equal([5, 6, 7], result);
    }

    [Fact]
    public void MultipleSmallWrites_ThenDrain()
    {
        var buffer = new RingBuffer(capacity: 6);
        buffer.Write([1, 2]);
        buffer.Write([3, 4]);
        buffer.Write([5, 6]);
        var result = buffer.Drain();
        Assert.Equal([1, 2, 3, 4, 5, 6], result);
    }
}
