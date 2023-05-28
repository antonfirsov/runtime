// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http;

internal sealed class MetricsHandler : HttpMessageHandlerStage
{
    private readonly HttpMessageHandler _innerHandler;
    private readonly HttpMetrics _metrics;

    public MetricsHandler(HttpMessageHandler innerHandler, Meter meter)
    {
        _metrics = new HttpMetrics(meter);
        _innerHandler = innerHandler;
    }

    internal override ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool async, CancellationToken cancellationToken)
    {
        if (_metrics.RequestCountersEnabled())
        {
            ArgumentNullException.ThrowIfNull(request);
            return SendAsyncWithMetrics(request, async, cancellationToken);
        }
        else
        {
            return async ?
                new ValueTask<HttpResponseMessage>(_innerHandler.SendAsync(request, cancellationToken)) :
                new ValueTask<HttpResponseMessage>(_innerHandler.Send(request, cancellationToken));
        }
    }

    private async ValueTask<HttpResponseMessage> SendAsyncWithMetrics(HttpRequestMessage request, bool async, CancellationToken cancellationToken)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        _metrics.RequestStart(request);
        HttpResponseMessage? response = null;
        try
        {
            response = async ?
                await _innerHandler.SendAsync(request, cancellationToken).ConfigureAwait(false) :
                _innerHandler.Send(request, cancellationToken);
        }
        catch
        {
            _metrics.RequestStop(request, response, startTimestamp, Stopwatch.GetTimestamp());
            throw;
        }

        response.Content = new TrackEofContent(startTimestamp, _metrics, response);
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerHandler.Dispose();
        }

        base.Dispose(disposing);
    }

    private struct MetricsRecorderData
    {
        private readonly long _startTimestamp;
        private HttpMetrics? _metrics;
        private readonly HttpResponseMessage _response;
        public readonly HttpContent InnerContent;

        public MetricsRecorderData(long startTimestamp, HttpMetrics metrics, HttpResponseMessage response)
        {
            _startTimestamp = startTimestamp;
            _metrics = metrics;
            _response = response;
            InnerContent = response.Content;
        }

        internal void LogRequestStop()
        {
            Debug.Assert(_response.RequestMessage is not null);
            _metrics?.RequestStop(_response.RequestMessage, _response, _startTimestamp, Stopwatch.GetTimestamp());
            _metrics = null;
        }
    }

    private interface IMetricsRecorder
    {
        void LogRequestStop();
    }

    private sealed class TrackEofContent : HttpContent, IMetricsRecorder
    {
        private MetricsRecorderData _metricsData;
        private Stream? _innerStream;

        private HttpContent InnerContent => _metricsData.InnerContent;

        public TrackEofContent(long startTimestamp, HttpMetrics metrics, HttpResponseMessage response)
        {
            _metricsData = new MetricsRecorderData(startTimestamp, metrics, response);

            foreach (var header in InnerContent.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            _innerStream = await InnerContent.ReadAsStreamAsync().ConfigureAwait(false);

            try
            {
                await _innerStream.CopyToAsync(stream).ConfigureAwait(false);
            }

            finally
            {
                LogRequestStop();
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            var stream = await InnerContent.ReadAsStreamAsync().ConfigureAwait(false);

            return new TrackEofStream(stream, this);
        }

        protected internal override bool TryComputeLength(out long length) => InnerContent.TryComputeLength(out length);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                LogRequestStop();
                InnerContent.Dispose();
                _innerStream?.Dispose();
            }

            base.Dispose(disposing);
        }

        public void LogRequestStop() => _metricsData.LogRequestStop();
    }

    private sealed class TrackEofStream : Stream
    {
        private readonly Stream _inner;
        private readonly IMetricsRecorder _metricsRecorder;

        public TrackEofStream(Stream inner, IMetricsRecorder metricsRecorder)
        {
            _inner = inner;
            _metricsRecorder = metricsRecorder;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }
        public override int ReadTimeout
        {
            get => _inner.ReadTimeout;
            set => _inner.ReadTimeout = value;
        }
        public override int WriteTimeout
        {
            get => _inner.WriteTimeout;
            set => _inner.WriteTimeout = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var result = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

            // TODO: Handle errors.
            if (count != 0 && result == 0)
            {
                _metricsRecorder.LogRequestStop();
            }
            return result;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            // TODO: Handle errors.
            if (buffer.Length != 0 && result == 0)
            {
                _metricsRecorder.LogRequestStop();
            }
            return result;
        }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await _inner.CopyToAsync(destination, bufferSize, cancellationToken).ConfigureAwait(false);
            _metricsRecorder.LogRequestStop();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                _inner.Dispose();
            }
        }
    }
}
