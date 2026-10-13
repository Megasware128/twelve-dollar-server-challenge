using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FeedApi;

/// <summary>
/// A pooled, growable output buffer backed by one rented array. Its owning PooledJson is
/// reused only after the response write completes; growth returns the previous array.
/// </summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
    private int _written;

    public int WrittenCount => _written;
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    void IBufferWriter<byte>.Advance(int count) => _written += count;

    public void Reset() => _written = 0;

    public void Write(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_written));
        _written += value.Length;
    }

    private void EnsureCapacity(int sizeHint)
    {
        var needed = _written + sizeHint;
        if (needed <= _buffer.Length) return;
        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
        Buffer.BlockCopy(_buffer, 0, bigger, 0, _written);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }
}

/// <summary>
/// A Utf8JsonWriter plus its buffer, rented and reset as a unit so steady-state request
/// handling allocates nothing for JSON output.
/// </summary>
internal sealed class PooledJson
{
    public PooledBufferWriter Buffer { get; } = new();

    private Utf8JsonWriter? _writer;
    public Utf8JsonWriter Writer => _writer ??= new Utf8JsonWriter(Buffer, Json.WriterOptions);

    public void Reset()
    {
        Buffer.Reset();
        _writer?.Reset(Buffer);
    }
}

internal static class Json
{
    // Compact JSON with exact key order. Relaxed escaping: the spec makes escaping of
    // <>&/ and non-ASCII optional, so escape only what JSON itself requires.
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
    };

    private static readonly ConcurrentQueue<PooledJson> Pool = new();

    public static PooledJson Rent() => Pool.TryDequeue(out var json) ? json : new PooledJson();

    public static void Return(PooledJson json)
    {
        json.Reset();
        Pool.Enqueue(json);
    }
}
