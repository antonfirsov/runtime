// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;

namespace WebSocketStress;

internal static class Utils
{
    public static Random NextRandom(this Random random) => new Random(Seed: random.Next());

    public static bool NextBoolean(this Random random, double probability = 0.5)
    {
        if (probability < 0 || probability > 1)
            throw new ArgumentOutOfRangeException(nameof(probability));

        return random.NextDouble() < probability;
    }

    // Adapted from https://devblogs.microsoft.com/dotnet/system-io-pipelines-high-performance-io-in-net/
    public static async Task ReadLinesUsingPipesAsync(this WsStream stream, Func<ReadOnlySequence<byte>, Task> callback, CancellationToken token = default, char separator = '\n')
    {
        var pipe = new Pipe();

        try
        {
            await WhenAllThrowOnFirstException(token, FillPipeAsync, ReadPipeAsync);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {

        }

        async Task FillPipeAsync(CancellationToken token)
        {
            try
            {
                //await stream.CopyToAsync(pipe.Writer, token);
                await CopyToPipeWriterAsync(stream, pipe.Writer, token);
            }
            catch (Exception e)
            {
                pipe.Writer.Complete(e);
                throw;
            }

            pipe.Writer.Complete();
        }

        async Task ReadPipeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                ReadResult result = await pipe.Reader.ReadAsync(token);
                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition? position;

                do
                {
                    position = buffer.PositionOf((byte)separator);

                    if (position != null)
                    {
                        await callback(buffer.Slice(0, position.Value));
                        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                    }
                }
                while (position != null);

                pipe.Reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }

        static async Task CopyToPipeWriterAsync(Stream source, PipeWriter writer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Memory<byte> buffer = writer.GetMemory();
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                writer.Advance(read);

                FlushResult result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (result.IsCanceled)
                {
                    throw new OperationCanceledException("Flush cancelled.");
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
    }

    public static async Task WhenAllThrowOnFirstException(CancellationToken token, params Func<CancellationToken, Task>[] tasks)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? firstException = null;

        await Task.WhenAll(tasks.Select(RunOne));

        if (firstException != null)
        {
            ExceptionDispatchInfo.Capture(firstException).Throw();
        }

        async Task RunOne(Func<CancellationToken, Task> task)
        {
            try
            {
                await Task.Run(() => task(cts.Token));
            }
            catch (Exception e)
            {
                if (Interlocked.CompareExchange(ref firstException, e, null) == null)
                {
                    cts.Cancel();
                }
            }
        }
    }
}


internal sealed class WsStream : Stream
{
    public WebSocket WebSocket { get; }

    public bool EndOfMessageReceived { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public WsStream(WebSocket webSocket) => WebSocket = webSocket;

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => WebSocket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: false, cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, bool endOfMessage, CancellationToken cancellationToken = default)
        => WebSocket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage, cancellationToken);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ValueWebSocketReceiveResult res = await WebSocket.ReceiveAsync(buffer, cancellationToken);
        EndOfMessageReceived = res.EndOfMessage;
        if (res.MessageType == WebSocketMessageType.Close)
        {
            return 0;
        }
        return res.Count;
    }

    public override void Flush() => throw new NotImplementedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotImplementedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
    public override void SetLength(long value) => throw new NotImplementedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
}

public struct DataSegment
{
    private byte[] _buffer;

    public DataSegment(int length)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(length);
        Length = length;
    }

    public int Length { get; }
    public Memory<byte> AsMemory() => new Memory<byte>(_buffer, 0, Length);
    public Span<byte> AsSpan() => new Span<byte>(_buffer, 0, Length);

    public ulong Checksum => CRC.CalculateCRC(AsSpan());
    public void Return()
    {
        byte[] toReturn = _buffer;
        _buffer = null;
        ArrayPool<byte>.Shared.Return(toReturn);
    }

    /// Create and populate a segment with random data
    public static DataSegment CreateRandom(Random random, int maxLength)
    {
        int size = random.Next(0, maxLength);
        var chunk = new DataSegment(size);
        foreach (ref byte b in chunk.AsSpan())
        {
            b = s_bytePool[random.Next(255)];
        }

        return chunk;
    }

    private static readonly byte[] s_bytePool =
        Enumerable
            .Range(0, 256)
            .Select(i => (byte)i)
            .Where(b => b != (byte)'\n')
            .ToArray();
}

public class DataMismatchException : Exception
{
    public DataMismatchException(string message) : base(message) { }
}

// Serializes data segment using the following format: <length>,<checksum>,<data>
public class DataSegmentSerializer
{
    private static readonly Encoding s_encoding = Encoding.ASCII;

    private readonly byte[] _buffer = new byte[32];
    private readonly char[] _charBuffer = new char[32];
    private static readonly byte[] s_comma = [(byte)','];

    public async Task SerializeAsync(Stream stream, DataSegment segment, Random? random = null, CancellationToken token = default)
    {
        // length
        int numsize = s_encoding.GetBytes(segment.Length.ToString(), _buffer);
        await stream.WriteAsync(_buffer.AsMemory(0, numsize), token);
        await stream.WriteAsync(s_comma, token);
        // checksum
        numsize = s_encoding.GetBytes(segment.Checksum.ToString(), _buffer);
        await stream.WriteAsync(_buffer.AsMemory(0, numsize), token);
        await stream.WriteAsync(s_comma, token);
        // payload
        Memory<byte> source = segment.AsMemory();
        // write the entire segment outright if not given random instance
        if (random == null)
        {
            await stream.WriteAsync(source, token);
            return;
        }
        // randomize chunking otherwise
        while (source.Length > 0)
        {
            if (random.NextBoolean(probability: 0.05))
            {
                stream.WriteByte(source.Span[0]);
                source = source.Slice(1);
            }
            else
            {
                // TODO consider non-uniform distribution for chunk sizes
                int chunkSize = random.Next(source.Length);
                Memory<byte> chunk = source.Slice(0, chunkSize);
                source = source.Slice(chunkSize);

                if (random.NextBoolean(probability: 0.9))
                {
                    await stream.WriteAsync(chunk, token);
                }
                else
                {
                    stream.Write(chunk.Span);
                }
            }

            if (random.NextBoolean(probability: 0.3))
            {
                await stream.FlushAsync(token);
            }

            // randomized delay
            if (random.NextBoolean(probability: 0.05))
            {
                if (random.NextBoolean(probability: 0.7))
                {
                    await Task.Delay(random.Next(60));
                }
                else
                {
                    Thread.SpinWait(random.Next(1000));
                }
            }
        }
    }

    public DataSegment Deserialize(ReadOnlySequence<byte> buffer)
    {
        // length
        SequencePosition? pos = buffer.PositionOf((byte)',');
        if (pos == null)
        {
            throw new DataMismatchException("should contain comma-separated values");
        }

        ReadOnlySequence<byte> lengthBytes = buffer.Slice(0, pos.Value);
        int numSize = s_encoding.GetChars(lengthBytes.ToArray(), _charBuffer);
        int length = int.Parse(_charBuffer.AsSpan(0, numSize));
        buffer = buffer.Slice(buffer.GetPosition(1, pos.Value));

        // checksum
        pos = buffer.PositionOf((byte)',');
        if (pos == null)
        {
            throw new DataMismatchException("should contain comma-separated values");
        }

        ReadOnlySequence<byte> checksumBytes = buffer.Slice(0, pos.Value);
        numSize = s_encoding.GetChars(checksumBytes.ToArray(), _charBuffer);
        ulong checksum = ulong.Parse(_charBuffer.AsSpan(0, numSize));
        buffer = buffer.Slice(buffer.GetPosition(1, pos.Value));

        // payload
        if (length != (int)buffer.Length)
        {
            throw new DataMismatchException("declared length does not match payload length");
        }

        var chunk = new DataSegment((int)buffer.Length);
        buffer.CopyTo(chunk.AsSpan());

        if (checksum != chunk.Checksum)
        {
            chunk.Return();
            throw new DataMismatchException("declared checksum doesn't match payload checksum");
        }

        return chunk;
    }
}
