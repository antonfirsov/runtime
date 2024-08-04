// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace WebSocketStress;

internal class StressServer
{
    private readonly Configuration _config;
    private readonly Lazy<Task> _serverTask;
    private readonly WebSocketCreationOptions _options;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    public IPEndPoint ServerEndpoint => (IPEndPoint)_listener.LocalEndPoint!;

    private Socket _listener;

    public StressServer(Configuration config)
    {
        _config = config;
        _options = new WebSocketCreationOptions()
        {
            IsServer = true,
            SubProtocol = null,
            KeepAliveInterval = config.KeepAliveInterval
        };

        if (config.KeepAliveTimeout is TimeSpan timeout && typeof(WebSocketCreationOptions).GetProperty("KeepAliveTimeout") is PropertyInfo keepAliveTimeoutProperty)
        {
            keepAliveTimeoutProperty.SetValue(_options, timeout);
        }

        IPEndPoint ep = config.ServerEndpoint;
        _listener = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(ep);
        _serverTask = new Lazy<Task>(Task.Run(StartCore));
    }

    public void Start()
    {
        
    }

    public Task StopAsync() => Task.CompletedTask;

    private async Task StartCore()
    {
        _listener.Listen();

        IEnumerable<Task> workers = Enumerable.Range(1, 2 * _config.MaxConnections).Select(_ => RunSingleWorker());
        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            _listener.Dispose();
        }

        async Task RunSingleWorker()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using Socket handlerSocket = await _listener.AcceptAsync(_cts.Token);
                    using WebSocket serverWebSocket = WebSocket.CreateFromStream(new NetworkStream(handlerSocket, ownsSocket: true), _options);
                    using WsStream wsStream = new WsStream(serverWebSocket);
                    await HandleConnection(serverWebSocket, wsStream, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {

                }
                catch (Exception e)
                {
                    if (_config.LogServer)
                    {
                        lock (Console.Out)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkRed;
                            Console.WriteLine($"Server: unhandled exception: {e}");
                            Console.WriteLine();
                            Console.ResetColor();
                        }
                    }
                }
            }
        }
    }

    private async Task HandleConnection(WebSocket webSocket, WsStream wsStream, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        DateTime lastReadTime = DateTime.Now;

        var serializer = new DataSegmentSerializer();

        _ = Task.Run(Monitor);
        await wsStream.ReadLinesUsingPipesAsync(Callback, cts.Token, separator: '\n');

        async Task Callback(ReadOnlySequence<byte> buffer)
        {
            lastReadTime = DateTime.Now;

            if (buffer.Length == 0)
            {
                // got an empty line, client is closing the connection
                // echo back the empty line and tear down.
                wsStream.WriteByte((byte)'\n');
                await wsStream.FlushAsync(token);
                cts.Cancel();
                return;
            }

            DataSegment? chunk = null;
            try
            {
                chunk = serializer.Deserialize(buffer);
                await serializer.SerializeAsync(sslStream, chunk.Value, token: token);
                sslStream.WriteByte((byte)'\n');
                await sslStream.FlushAsync(token);
            }
            catch (DataMismatchException e)
            {
                if (_config.LogServer)
                {
                    lock (Console.Out)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Server: {e.Message}");
                        Console.ResetColor();
                    }
                }
            }
            finally
            {
                chunk?.Return();
            }
        }

        async Task Monitor()
        {
            do
            {
                await Task.Delay(1000);

                if (DateTime.Now - lastReadTime >= TimeSpan.FromSeconds(10))
                {
                    cts.Cancel();
                }

            } while (!cts.IsCancellationRequested);
        }
    }
}
