// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebSocketStress;

internal class StressClient
{
    private readonly StressResultAggregator _aggregator;
    private readonly Stopwatch _stopwatch = new Stopwatch();

    public long TotalErrorCount { get; private set; }

    public StressClient(Configuration config)
    {
        _aggregator = new StressResultAggregator(config.MaxConnections);
    }

    public void Start()
    {
        _stopwatch.Start();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;


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
