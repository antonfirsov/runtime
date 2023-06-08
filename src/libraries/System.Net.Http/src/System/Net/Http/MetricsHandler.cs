// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http;

internal sealed class MetricsHandler : HttpMessageHandlerStage
{
    private readonly HttpMessageHandler _innerHandler;
    private readonly Meter _meter;
    private readonly UpDownCounter<long> _currentRequests;
    private readonly Histogram<double> _requestsDuration;

    public MetricsHandler(HttpMessageHandler innerHandler, Meter meter)
    {
        _meter = meter;
        _innerHandler = innerHandler;

        _currentRequests = _meter.CreateUpDownCounter<long>(
               "http-client-current-requests",
               description: "Number of outbound HTTP requests that are currently active on the client.");

        _requestsDuration = _meter.CreateHistogram<double>(
            "http-client-request-duration",
            unit: "s",
            description: "The duration of outbound HTTP requests.");
    }

    public static Meter DefaultMeter { get; } = new SharedMeter();

    internal override ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool async, CancellationToken cancellationToken)
    {
        if (_currentRequests.Enabled || _requestsDuration.Enabled)
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
        RequestSpanData span = RequestStart(request);
        HttpResponseMessage? response = null;
        try
        {
            response = async ?
                await _innerHandler.SendAsync(request, cancellationToken).ConfigureAwait(false) :
                _innerHandler.Send(request, cancellationToken);
        }
        finally
        {
            RequestStop(request, response, span);
        }

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

    private RequestSpanData RequestStart(HttpRequestMessage request)
    {
        TagList tags = InitializeCommonTags(request);
        RequestSpanData span = new RequestSpanData(_currentRequests.Enabled, _requestsDuration.Enabled, tags);

        if (span.CurrentRequestsEnabled)
        {
            _currentRequests.Add(1, tags);
        }

        return span;
    }

    private void RequestStop(HttpRequestMessage request, HttpResponseMessage? response, in RequestSpanData span)
    {
        if (span.CurrentRequestsEnabled)
        {
            _currentRequests.Add(-1, span.TagsAtStart);
        }

        if (_requestsDuration.Enabled)
        {
            long endTimeStamp = Stopwatch.GetTimestamp();

            TagList tags = InitializeCommonTags(request);
            if (response is not null)
            {
                tags.Add("status-code", StatusCodeCache.GetBoxedStatusCode(response.StatusCode));
                tags.Add("protocol", GetProtocolName(response.Version)); // Hacky
            }

            if (request._options?.TryGetCustomMetricsTags(out IReadOnlyCollection<KeyValuePair<string, object?>>? customTags) is true)
            {
                foreach (var customTag in customTags!)
                {
                    tags.Add(customTag);
                }
            }

            TimeSpan duration = Stopwatch.GetElapsedTime(span.StartTimestamp, endTimeStamp);
            _requestsDuration.Record(duration.TotalSeconds, tags);
        }
    }

    private static TagList InitializeCommonTags(HttpRequestMessage request)
    {
        TagList tags = default;

        if (request.RequestUri is { } requestUri && requestUri.IsAbsoluteUri)
        {
            if (requestUri.Scheme is not null)
            {
                tags.Add("scheme", requestUri.Scheme);
            }
            if (requestUri.Host is not null)
            {
                tags.Add("host", requestUri.Host);
            }
            // Add port tag when not the default value for the current scheme
            if (!requestUri.IsDefaultPort)
            {
                tags.Add("port", requestUri.Port);
            }
        }
        tags.Add("method", request.Method.Method);

        return tags;
    }

    private static string GetProtocolName(Version httpVersion) => (httpVersion.Major, httpVersion.Minor) switch
    {
        (1, 1) => "HTTP/1.1",
        (2, 0) => "HTTP/2",
        (3, 0) => "HTTP/3",
        _ => "unknown"
    };

    private static class StatusCodeCache
    {
        private static readonly object OK = (int)HttpStatusCode.OK;
        private static readonly object Created = (int)HttpStatusCode.Created;
        private static readonly object Accepted = (int)HttpStatusCode.Accepted;
        private static readonly object NoContent = (int)HttpStatusCode.NoContent;
        private static readonly object Moved = (int)HttpStatusCode.Moved;
        private static readonly object Redirect = (int)HttpStatusCode.Redirect;
        private static readonly object NotModified = (int)HttpStatusCode.NotModified;
        private static readonly object InternalServerError = (int)HttpStatusCode.InternalServerError;

        public static object GetBoxedStatusCode(HttpStatusCode statusCode) => statusCode switch
        {
            HttpStatusCode.OK => OK,
            HttpStatusCode.Created => Created,
            HttpStatusCode.Accepted => Accepted,
            HttpStatusCode.NoContent => NoContent,
            HttpStatusCode.Moved => Moved,
            HttpStatusCode.Redirect => Redirect,
            HttpStatusCode.NotModified => NotModified,
            HttpStatusCode.InternalServerError => InternalServerError,
            _ => (int)statusCode
        };
    }

    private sealed class SharedMeter : Meter
    {
        public SharedMeter()
            : base("System.Net.Http")
        {
        }

        protected override void Dispose(bool disposing)
        {
            // NOP to prevent disposing the global instance from arbitrary user code.
        }
    }

    private struct RequestSpanData
    {
        public long StartTimestamp;
        public readonly bool CurrentRequestsEnabled;
        public readonly bool RequestDurationEnabled;
        public readonly TagList TagsAtStart;

        public RequestSpanData(bool currentRequestsEnabled, bool requestDurationEnabled, in TagList tagsAtStart)
        {
            StartTimestamp = Stopwatch.GetTimestamp();
            CurrentRequestsEnabled = currentRequestsEnabled;
            RequestDurationEnabled = requestDurationEnabled;
            TagsAtStart = tagsAtStart;
        }
    }
}
