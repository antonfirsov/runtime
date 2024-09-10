// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;

namespace WebSocketStress;

internal class InputProcessor
{
    private const byte Separator = (byte)'\n';
    private readonly WebSocket _webSocket;

    private readonly Log _log;

    public InputProcessor(WebSocket webSocket, Log log)
    {
        _webSocket = webSocket;
        _log = log;
    }

    // Adapted from https://devblogs.microsoft.com/dotnet/system-io-pipelines-high-performance-io-in-net/
    public async Task RunAsync(Func<ReadOnlySequence<byte>, Task> callback, CancellationToken token = default)
    {
        bool completed = false;
        var pipe = new Pipe();
        try
        {
            await Utils.WhenAllThrowOnFirstException(token, FillPipeAsync, ReadPipeAsync);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }

        async Task FillPipeAsync(CancellationToken token)
        {
            try
            {
                await CopyToPipeWriterAsync(_webSocket, pipe.Writer, token);
                Volatile.Write(ref completed, true);
            }
            catch (Exception e)
            {
                _log.WriteLine($"CopyToPipeWriterAsync thrown: {e}");
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
                    position = buffer.PositionOf(Separator);

                    if (position != null)
                    {
                        if (result.IsCompleted)
                        {

                        }

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
    }

    private async ValueTask CopyToPipeWriterAsync(WebSocket ws, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Memory<byte> buffer = writer.GetMemory();
            ValueWebSocketReceiveResult wsResult = await ws.ReceiveAsync(buffer, cancellationToken);

            if (wsResult.MessageType == WebSocketMessageType.Close)
            {
                _log.WriteLine($"detected WebSocket closure.");
                return;
            }

            writer.Advance(wsResult.Count);

            FlushResult flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (flushResult.IsCanceled)
            {
                throw new OperationCanceledException("Flush cancelled.");
            }

            if (flushResult.IsCompleted)
            {
                return;
            }
        }
    }
}
