// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace System.Net.Http
{
    internal sealed class HttpMetrics
    {
        private readonly Meter _meter;
        private readonly UpDownCounter<long> _currentRequests;
        private readonly Histogram<double> _requestsDuration;

        public static Meter DefaultMeter { get; } = new Meter("System.Net.Http");

        public HttpMetrics(Meter meter)
        {
            _meter = meter;

            _currentRequests = _meter.CreateUpDownCounter<long>(
                "http-client-current-requests",
                description: "Number of outbound HTTP requests that are currently active on the client.");

            _requestsDuration = _meter.CreateHistogram<double>(
                "http-client-request-duration",
                unit: "s",
                description: "The duration of outbound HTTP requests.");
        }

        public void RequestStart()
        {
            if (_currentRequests.Enabled)
            {
                _currentRequests.Add(1);
            }
        }

        public void RequestStop(HttpRequestMessage request, HttpResponseMessage? response, long startTimestamp, long currentTimestamp)
        {
            if (_currentRequests.Enabled || _requestsDuration.Enabled)
            {
                RequestStopCore(request, response, startTimestamp, currentTimestamp);
            }
        }

        private void RequestStopCore(HttpRequestMessage request, HttpResponseMessage? response, long startTimestamp, long currentTimestamp)
        {
            TagList tags = InitializeCommonTags(request);

            _currentRequests.Add(-1, tags);

            if (response is not null)
            {
                tags.Add("status-code", StatusCodeCache.GetBoxedStatusCode(response.StatusCode));
                tags.Add("protocol", GetProtocolName(response.Version)); // Hacky
            }

            if (request.Options.TryGetCustomMetricsTags(out IReadOnlyCollection<KeyValuePair<string, object?>>? customTags))
            {
                foreach (var customTag in customTags!)
                {
                    tags.Add(customTag);
                }
            }
            TimeSpan duration = Stopwatch.GetElapsedTime(startTimestamp, currentTimestamp);
            _requestsDuration.Record(duration.TotalSeconds, tags);
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

        internal bool RequestCountersEnabled() => _currentRequests.Enabled || _requestsDuration.Enabled;

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
    }
}
