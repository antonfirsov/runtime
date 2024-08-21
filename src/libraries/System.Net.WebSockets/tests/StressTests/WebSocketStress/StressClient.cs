﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Buffers;
using System.Net.Security;
namespace WebSocketStress;

internal class StressClient
{
    private readonly Configuration _config;
    private readonly WebSocketCreationOptions _options;
    private readonly StressResultAggregator _aggregator;
    private readonly Stopwatch _stopwatch = new Stopwatch();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public long TotalErrorCount { get; private set; }

    public StressClient(Configuration config)
    {
        _options = new WebSocketCreationOptions()
        {
            IsServer = false,
            SubProtocol = null,
            KeepAliveInterval = config.KeepAliveInterval
        };

        _aggregator = new StressResultAggregator(config.MaxConnections);
        _config = config;
    }

    public Task Start() => Task.Run(StartCore);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    private Task StartCore()
    {
        _stopwatch.Start();

        // Spin up a thread dedicated to outputting stats for each defined interval
        new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(_config.DisplayInterval);
                lock (Console.Out) { _aggregator.PrintCurrentResults(_stopwatch.Elapsed, showAggregatesOnly: false); }
            }
        })
        { IsBackground = true }.Start();

        IEnumerable<Task> workers = CreateWorkerSeeds().Select(x => RunSingleWorker(x.workerId, x.random));
        return Task.WhenAll(workers);

        async Task RunSingleWorker(int workerId, Random random)
        {
            StreamCounter counter = _aggregator.GetCounters(workerId);

            for (long jobId = 0; !_cts.IsCancellationRequested; jobId++)
            {
                TimeSpan connectionLifetime = _config.MinConnectionLifetime + random.NextDouble() * (_config.MaxConnectionLifetime - _config.MinConnectionLifetime);
                TimeSpan cancellationDelay =
                    (random.NextBoolean(probability: _config.CancellationProbability)) ?
                    connectionLifetime * random.NextDouble() : // cancel in a random interval within the lifetime
                    connectionLifetime + TimeSpan.FromSeconds(10); // otherwise trigger cancellation 10 seconds after expected expiry

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(cancellationDelay);

                bool isTestCompleted = false;
                using CancellationTokenRegistration _ = cts.Token.Register(CheckForStalledConnection);

                try
                {
                    using Socket client = new Socket(_config.ServerEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    await client.ConnectAsync(_config.ServerEndpoint, cts.Token);
                    Stream stream = new CountingStream(new NetworkStream(client, ownsSocket: true), counter);
                    using WebSocket clientWebSocket = WebSocket.CreateFromStream(stream, _options);
                    using WsStream wsStream = new WsStream(clientWebSocket);
                    await HandleConnection(workerId, jobId, wsStream, random, connectionLifetime, cts.Token);
                    //Console.WriteLine("*** Client CONN DONE ***");
                    _aggregator.RecordSuccess(workerId);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    _aggregator.RecordCancellation(workerId);
                }
                catch (Exception e)
                {
                    _aggregator.RecordFailure(workerId, e);
                }
                finally
                {
                    isTestCompleted = true;
                }

                async void CheckForStalledConnection()
                {
                    await Task.Delay(10_000);
                    if (!isTestCompleted)
                    {
                        lock (Console.Out)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Worker #{workerId} test #{jobId} has stalled, terminating the stress app.");
                            Console.WriteLine();
                            Console.ResetColor();
                        }
                        Environment.Exit(1);
                    }
                }
            }
        }

        IEnumerable<(int workerId, Random random)> CreateWorkerSeeds()
        {
            // deterministically generate random instance for each individual worker
            Random random = new Random(_config.RandomSeed);
            for (int workerId = 0; workerId < _config.MaxConnections; workerId++)
            {
                yield return (workerId, random.NextRandom());
            }
        }
    }

    private static readonly byte[] s_endLine = [(byte)'\n'];

    private async Task HandleConnection(int workerId, long jobId, WsStream stream,  Random random, TimeSpan duration, CancellationToken token)
    {
        // token used for signaling cooperative cancellation; do not pass this to SslStream methods
        using var connectionLifetimeToken = new CancellationTokenSource(duration);

        long messagesInFlight = 0;
        DateTime lastWrite = DateTime.Now;
        DateTime lastRead = DateTime.Now;

        await Utils.WhenAllThrowOnFirstException(token, Sender, Receiver, Monitor);
        //await stream.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", token);


        async Task Sender(CancellationToken token)
        {
            var serializer = new DataSegmentSerializer();

            while (!token.IsCancellationRequested && !connectionLifetimeToken.IsCancellationRequested)
            {
                await ApplyBackpressure();

                DataSegment chunk = DataSegment.CreateRandom(random, _config.MaxBufferLength);
                try
                {
                    await serializer.SerializeAsync(stream, chunk, random, token);
                    await stream.WriteAsync(s_endLine, token);
                    //await stream.FlushAsync(token);
                    Interlocked.Increment(ref messagesInFlight);
                    lastWrite = DateTime.Now;
                }
                finally
                {
                    chunk.Return();
                }
            }

            // write an empty line to signal completion to the server
            await stream.WriteAsync(s_endLine, token);
            //await stream.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "l0l", token);
            //await stream.FlushAsync(token);

            /// Polls until number of in-flight messages falls below threshold
            async Task ApplyBackpressure()
            {
                if (Volatile.Read(ref messagesInFlight) > 5000)
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    bool isLogged = false;

                    while (!token.IsCancellationRequested && !connectionLifetimeToken.IsCancellationRequested && Volatile.Read(ref messagesInFlight) > 2000)
                    {
                        // only log if tx has been suspended for a while
                        if (!isLogged && stopwatch.ElapsedMilliseconds >= 1000)
                        {
                            isLogged = true;
                            lock (Console.Out)
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"worker #{workerId}: applying backpressure");
                                Console.WriteLine();
                                Console.ResetColor();
                            }
                        }

                        await Task.Delay(20);
                    }

                    if (isLogged)
                    {
                        Console.WriteLine($"worker #{workerId}: resumed tx after {stopwatch.Elapsed}");
                    }
                }
            }
        }

        async Task Receiver(CancellationToken token)
        {
            var serializer = new DataSegmentSerializer();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            await stream.WebSocket.ReadLinesUsingPipesAsync(Callback, cts.Token, separator: '\n');

            Task Callback(ReadOnlySequence<byte> buffer)
            {
                if (buffer.Length == 0 && connectionLifetimeToken.IsCancellationRequested)
                {
                    // server echoed back empty buffer sent by client,
                    // signal cancellation and complete the connection.
                    cts.Cancel();
                    //Console.WriteLine("************** Client CANCEEEEEEEL ******************");
                    return Task.CompletedTask;
                }

                // deserialize to validate the checksum, then discard
                DataSegment chunk = serializer.Deserialize(buffer);
                chunk.Return();
                Interlocked.Decrement(ref messagesInFlight);
                lastRead = DateTime.Now;
                return Task.CompletedTask;
            }
        }

        async Task Monitor(CancellationToken token)
        {
            do
            {
                await Task.Delay(500);

                if ((DateTime.Now - lastWrite) >= TimeSpan.FromSeconds(10))
                {
                    throw new Exception($"worker #{workerId} job #{jobId} has stopped writing bytes to server");
                }

                if ((DateTime.Now - lastRead) >= TimeSpan.FromSeconds(10))
                {
                    throw new Exception($"worker #{workerId} job #{jobId} has stopped receiving bytes from server");
                }
            }
            while (!token.IsCancellationRequested && !connectionLifetimeToken.IsCancellationRequested);
        }
    }

    public void PrintFinalReport()
    {
        lock (Console.Out)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("SslStress Run Final Report");
            Console.WriteLine();

            _aggregator.PrintCurrentResults(_stopwatch.Elapsed, showAggregatesOnly: true);
            _aggregator.PrintFailureTypes();
        }
    }

    private class StressResultAggregator
    {
        private long _totalConnections = 0;
        private readonly long[] _successes, _failures, _cancellations;
        private readonly ErrorAggregator _errors = new ErrorAggregator();
        private readonly StreamCounter[] _currentCounters;
        private readonly StreamCounter[] _aggregateCounters;

        public StressResultAggregator(int workerCount)
        {
            _currentCounters = Enumerable.Range(0, workerCount).Select(_ => new StreamCounter()).ToArray();
            _aggregateCounters = Enumerable.Range(0, workerCount).Select(_ => new StreamCounter()).ToArray();
            _successes = new long[workerCount];
            _failures = new long[workerCount];
            _cancellations = new long[workerCount];
        }

        public long TotalConnections => _totalConnections;
        public long TotalFailures => _failures.Sum();

        public StreamCounter GetCounters(int workerId) => _currentCounters[workerId];

        public void RecordSuccess(int workerId)
        {
            _successes[workerId]++;
            Interlocked.Increment(ref _totalConnections);
            UpdateCounters(workerId);
        }

        public void RecordCancellation(int workerId)
        {
            _cancellations[workerId]++;
            Interlocked.Increment(ref _totalConnections);
            UpdateCounters(workerId);
        }

        public void RecordFailure(int workerId, Exception exn)
        {
            _failures[workerId]++;
            Interlocked.Increment(ref _totalConnections);
            _errors.RecordError(exn);
            UpdateCounters(workerId);

            lock (Console.Out)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"Worker #{workerId}: unhandled exception: {exn}");
                Console.WriteLine();
                Console.ResetColor();
            }
        }

        private void UpdateCounters(int workerId)
        {
            // need to synchronize with GetCounterView to avoid reporting bad data
            lock (_aggregateCounters)
            {
                _aggregateCounters[workerId].Append(_currentCounters[workerId]);
                _currentCounters[workerId].Reset();
            }
        }

        private (StreamCounter total, StreamCounter current)[] GetCounterView()
        {
            // generate a coherent view of counter state
            lock (_aggregateCounters)
            {
                var view = new (StreamCounter total, StreamCounter current)[_aggregateCounters.Length];
                for (int i = 0; i < _aggregateCounters.Length; i++)
                {
                    StreamCounter current = _currentCounters[i].Clone();
                    StreamCounter total = _aggregateCounters[i].Clone().Append(current);
                    view[i] = (total, current);
                }

                return view;
            }
        }

        public void PrintFailureTypes() => _errors.PrintFailureTypes();

        public void PrintCurrentResults(TimeSpan elapsed, bool showAggregatesOnly)
        {
            (StreamCounter total, StreamCounter current)[] counters = GetCounterView();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"[{DateTime.Now}]");
            Console.ResetColor();
            Console.WriteLine(" Elapsed: " + elapsed.ToString(@"hh\:mm\:ss"));
            Console.ResetColor();

            for (int i = 0; i < _currentCounters.Length; i++)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"\tWorker #{i:N0}:");
                Console.ResetColor();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"\tPass: ");
                Console.ResetColor();
                Console.Write(_successes[i].ToString("N0"));
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Write("\tCancel: ");
                Console.ResetColor();
                Console.Write(_cancellations[i].ToString("N0"));
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.Write("\tFail: ");
                Console.ResetColor();
                Console.Write(_failures[i].ToString("N0"));

                if (!showAggregatesOnly)
                {
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.Write($"\tCurr. Tx: ");
                    Console.ResetColor();
                    Console.Write(FmtBytes(counters[i].current.BytesWritten));
                    Console.ForegroundColor = ConsoleColor.DarkMagenta;
                    Console.Write($"\tCurr. Rx: ");
                    Console.ResetColor();
                    Console.Write(FmtBytes(counters[i].current.BytesRead));
                }

                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write($"\tTotal Tx: ");
                Console.ResetColor();
                Console.Write(FmtBytes(counters[i].total.BytesWritten));
                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.Write($"\tTotal Rx: ");
                Console.ResetColor();
                Console.Write(FmtBytes(counters[i].total.BytesRead));

                Console.WriteLine();
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("\tTOTAL :   ");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"\tPass: ");
            Console.ResetColor();
            Console.Write(_successes.Sum().ToString("N0"));
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write("\tCancel: ");
            Console.ResetColor();
            Console.Write(_cancellations.Sum().ToString("N0"));
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.Write("\tFail: ");
            Console.ResetColor();
            Console.Write(_failures.Sum().ToString("N0"));

            if (!showAggregatesOnly)
            {
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("\tCurr. Tx: ");
                Console.ResetColor();
                Console.Write(FmtBytes(counters.Select(c => c.current.BytesWritten).Sum()));
                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.Write($"\tCurr. Rx: ");
                Console.ResetColor();
                Console.Write(FmtBytes(counters.Select(c => c.current.BytesRead).Sum()));
            }

            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.Write("\tTotal Tx: ");
            Console.ResetColor();
            Console.Write(FmtBytes(counters.Select(c => c.total.BytesWritten).Sum()));
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.Write($"\tTotal Rx: ");
            Console.ResetColor();
            Console.Write(FmtBytes(counters.Select(c => c.total.BytesRead).Sum()));

            Console.WriteLine();
            Console.WriteLine();

            static string FmtBytes(long value) => HumanReadableByteSizeFormatter.Format(value);
        }
    }
}

public class StreamCounter
{
    public long BytesWritten = 0L;
    public long BytesRead = 0L;

    public void Reset()
    {
        BytesWritten = 0L;
        BytesRead = 0L;
    }

    public StreamCounter Append(StreamCounter that)
    {
        BytesRead += that.BytesRead;
        BytesWritten += that.BytesWritten;
        return this;
    }

    public StreamCounter Clone() => new StreamCounter() { BytesRead = BytesRead, BytesWritten = BytesWritten };
}

public class CountingStream : Stream
{
    private readonly Stream _stream;
    private readonly StreamCounter _counter;

    public CountingStream(Stream stream, StreamCounter counters)
    {
        _stream = stream;
        _counter = counters;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _stream.Write(buffer, offset, count);
        Interlocked.Add(ref _counter.BytesWritten, count);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _stream.Read(buffer, offset, count);
        Interlocked.Add(ref _counter.BytesRead, read);
        return read;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(buffer, cancellationToken);
        Interlocked.Add(ref _counter.BytesWritten, buffer.Length);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _stream.ReadAsync(buffer, cancellationToken);
        Interlocked.Add(ref _counter.BytesRead, read);
        return read;
    }

    public override void WriteByte(byte value)
    {
        _stream.WriteByte(value);
        Interlocked.Increment(ref _counter.BytesRead);
    }

    // route everything else to the inner stream

    public override bool CanRead => _stream.CanRead;

    public override bool CanSeek => _stream.CanSeek;

    public override bool CanWrite => _stream.CanWrite;

    public override long Length => _stream.Length;

    public override long Position { get => _stream.Position; set => _stream.Position = value; }

    public override void Flush() => _stream.Flush();

    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

    public override void SetLength(long value) => _stream.SetLength(value);

    public override void Close() => _stream.Close();
}

public static class HumanReadableByteSizeFormatter
{
    private static readonly string[] s_suffixes = { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };

    public static string Format(long byteCount)
    {
        // adapted from https://stackoverflow.com/a/4975942
        if (byteCount == 0)
        {
            return $"0{s_suffixes[0]}";
        }

        int position = (int)Math.Floor(Math.Log(Math.Abs(byteCount), 1024));
        double renderedValue = byteCount / Math.Pow(1024, position);
        return $"{renderedValue:0.#}{s_suffixes[position]}";
    }
}


public interface IErrorType
{
    string ErrorMessage { get; }

    IReadOnlyCollection<(DateTime timestamp, string? metadata)> Occurrences { get; }
}

public sealed class ErrorAggregator
{
    private readonly ConcurrentDictionary<(Type exception, string message, string callSite)[], ErrorType> _failureTypes;

    public ErrorAggregator()
    {
        _failureTypes = new ConcurrentDictionary<(Type, string, string)[], ErrorType>(new StructuralEqualityComparer<(Type, string, string)[]>());
    }

    public int TotalErrorTypes => _failureTypes.Count;
    public IReadOnlyCollection<IErrorType> ErrorTypes => ErrorTypes.ToArray();
    public long TotalErrorCount => _failureTypes.Values.Select(c => (long)c.Occurrences.Count).Sum();

    public void RecordError(Exception exception, string? metadata = null, DateTime? timestamp = null)
    {
        timestamp ??= DateTime.Now;

        (Type, string, string)[] key = ClassifyFailure(exception);

        ErrorType failureType = _failureTypes.GetOrAdd(key, _ => new ErrorType(exception.ToString()));
        failureType.OccurrencesQueue.Enqueue((timestamp.Value, metadata));

        // classify exception according to type, message and callsite of itself and any inner exceptions
        static (Type exception, string message, string callSite)[] ClassifyFailure(Exception exn)
        {
            var acc = new List<(Type exception, string message, string callSite)>();

            for (Exception? e = exn; e != null;)
            {
                acc.Add((e.GetType(), e.Message ?? "", new StackTrace(e, true).GetFrame(0)?.ToString() ?? ""));
                e = e.InnerException;
            }

            return acc.ToArray();
        }
    }

    public void PrintFailureTypes()
    {
        if (_failureTypes.Count == 0)
            return;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"There were a total of {TotalErrorCount} failures classified into {TotalErrorTypes} different types:");
        Console.WriteLine();
        Console.ResetColor();

        int i = 0;
        foreach (ErrorType failure in _failureTypes.Values.OrderByDescending(x => x.Occurrences.Count))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Failure Type {++i}/{_failureTypes.Count}:");
            Console.ResetColor();
            Console.WriteLine(failure.ErrorMessage);
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (IGrouping<string?, (DateTime timestamp, string? metadata)> grouping in failure.Occurrences.GroupBy(o => o.metadata))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"\t{(grouping.Key ?? "").PadRight(30)}");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("Fail: ");
                Console.ResetColor();
                Console.Write(grouping.Count());
                Console.WriteLine($"\tTimestamps: {string.Join(", ", grouping.Select(x => x.timestamp.ToString("HH:mm:ss")))}");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("\t    TOTAL".PadRight(31));
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"Fail: ");
            Console.ResetColor();
            Console.WriteLine(TotalErrorTypes);
            Console.WriteLine();
        }
    }

    /// <summary>Aggregate view of a particular stress failure type</summary>
    private sealed class ErrorType : IErrorType
    {
        public string ErrorMessage { get; }
        public ConcurrentQueue<(DateTime, string?)> OccurrencesQueue = new ConcurrentQueue<(DateTime, string?)>();

        public ErrorType(string errorText)
        {
            ErrorMessage = errorText;
        }

        public IReadOnlyCollection<(DateTime timestamp, string? metadata)> Occurrences => OccurrencesQueue;
    }

    private class StructuralEqualityComparer<T> : IEqualityComparer<T> where T : IStructuralEquatable
    {
        public bool Equals(T? left, T? right) => left != null && left.Equals(right, StructuralComparisons.StructuralEqualityComparer);
        public int GetHashCode([DisallowNull] T value) => value.GetHashCode(StructuralComparisons.StructuralEqualityComparer);
    }
}
