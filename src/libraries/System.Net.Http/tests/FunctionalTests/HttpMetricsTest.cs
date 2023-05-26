// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net.Test.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

namespace System.Net.Http.Functional.Tests
{
    public abstract class HttpMetricsTest : HttpClientHandlerTestBase
    {
        public HttpMetricsTest(ITestOutputHelper output) : base(output)
        {
        }

        protected static void VerifyCurrentRequest(Measurement<long> measurement, long expectedValue, string scheme, string host, int? port = null)
        {
            Assert.Equal(expectedValue, measurement.Value);
            Assert.Equal(scheme, measurement.Tags.ToArray().Single(t => t.Key == "scheme").Value);
            Assert.Equal(host, measurement.Tags.ToArray().Single(t => t.Key == "host").Value);
            AssertOptionalTag(measurement.Tags, "port", port);
        }

        protected static void VerifyRequestDuration(Measurement<double> measurement, string scheme, string host, string? protocol, int? statusCode, int? port = null)
        {
            Assert.True(measurement.Value > 0);
            Assert.Equal(scheme, measurement.Tags.ToArray().Single(t => t.Key == "scheme").Value);
            Assert.Equal(host, measurement.Tags.ToArray().Single(t => t.Key == "host").Value);
            AssertOptionalTag(measurement.Tags, "port", port);
            AssertOptionalTag(measurement.Tags, "protocol", protocol);
            AssertOptionalTag(measurement.Tags, "status-code", statusCode);
        }

        protected static void AssertOptionalTag<T>(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name, T value)
        {
            if (value is null)
            {
                Assert.DoesNotContain(tags.ToArray(), t => t.Key == "name");
            }
            else
            {
                Assert.Equal(value, (T)tags.ToArray().Single(t => t.Key == name).Value);
            }
        }

        private sealed class EnrichmentHandler : DelegatingHandler
        {
            public EnrichmentHandler(HttpMessageHandler innerHandler) : base(innerHandler)
            {
            }

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                try
                {
                    request.MetricsTags.Add(new KeyValuePair<string, object?>("before", "before!"));
                    return base.Send(request, cancellationToken);
                }
                catch
                {
                    request.MetricsTags.Add(new KeyValuePair<string, object?>("error", "error!"));
                    throw;
                }
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                try
                {
                    request.MetricsTags.Add(new KeyValuePair<string, object?>("before", "before!"));
                    return await base.SendAsync(request, cancellationToken);
                }
                catch
                {
                    request.MetricsTags.Add(new KeyValuePair<string, object?>("error", "error!"));
                    throw;
                }
            }
        }

        [Fact]
        public Task SendAsync_CurrentRequests_Success()
        {
            return LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using HttpClientHandler handler = CreateHttpClientHandler();
                using HttpClient client = CreateHttpClient(handler);

                using var recorder = new InstrumentRecorder<long>(GetUnderlyingSocketsHttpHandler(handler).Meter, "current-requests");

                using HttpRequestMessage request = new(HttpMethod.Get, uri) { Version = UseVersion };

                using var response = await client.SendAsync(request);

                Assert.Collection(recorder.GetMeasurements(),
                    m => VerifyCurrentRequest(m, 1, uri.Scheme, uri.IdnHost),
                    m => VerifyCurrentRequest(m, -1, uri.Scheme, uri.IdnHost));

            }, async server =>
            {
                await server.AcceptConnectionSendResponseAndCloseAsync();
            });
        }

        [Fact]
        public Task SendAsync_RequestDuration_Success()
        {
            return LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using HttpClientHandler handler = CreateHttpClientHandler();
                using HttpClient client = CreateHttpClient(handler);

                using var recorder = new InstrumentRecorder<double>(GetUnderlyingSocketsHttpHandler(handler).Meter, "request-duration");

                using HttpRequestMessage request = new(HttpMethod.Get, uri) { Version = UseVersion };

                using var response = await client.SendAsync(request);

                Measurement<double> m = recorder.GetMeasurements().Single();
                VerifyRequestDuration(m, uri.Scheme, uri.IdnHost, $"HTTP/{UseVersion}", 200);

            }, async server =>
            {
                await server.AcceptConnectionSendResponseAndCloseAsync();
            });
        }

        [Fact]
        public Task SendAsync_RequestDuration_CustomTags()
        {
            return LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using HttpClientHandler handler = CreateHttpClientHandler();
                using HttpClient client = CreateHttpClient(handler);

                using var recorder = new InstrumentRecorder<double>(GetUnderlyingSocketsHttpHandler(handler).Meter, "request-duration");

                using HttpRequestMessage request = new(HttpMethod.Get, uri) { Version = UseVersion };
                request.MetricsTags.Add(new KeyValuePair<string, object>("route", "/test"));

                using var response = await client.SendAsync(request);

                Measurement<double> m = recorder.GetMeasurements().Single();
                VerifyRequestDuration(m, uri.Scheme, uri.IdnHost, $"HTTP/{UseVersion}", 200);
                Assert.Equal("/test", m.Tags.ToArray().Single(t => t.Key == "route").Value);

            }, async server =>
            {
                await server.AcceptConnectionSendResponseAndCloseAsync();
            });
        }

        public enum ResponseContentType
        {
            Empty,
            ContentLength,
            TransferEncodingChunked
        }

        [Theory]
        [InlineData(HttpCompletionOption.ResponseContentRead, false, ResponseContentType.Empty)]
        [InlineData(HttpCompletionOption.ResponseContentRead, false, ResponseContentType.ContentLength)]
        [InlineData(HttpCompletionOption.ResponseContentRead, false, ResponseContentType.TransferEncodingChunked)]
        [InlineData(HttpCompletionOption.ResponseHeadersRead, false, ResponseContentType.Empty)]
        [InlineData(HttpCompletionOption.ResponseHeadersRead, false, ResponseContentType.ContentLength)]
        [InlineData(HttpCompletionOption.ResponseHeadersRead, false, ResponseContentType.TransferEncodingChunked)]
        [InlineData(HttpCompletionOption.ResponseHeadersRead, true, ResponseContentType.Empty)]
        [InlineData(HttpCompletionOption.ResponseHeadersRead, true, ResponseContentType.ContentLength)]
        [InlineData(HttpCompletionOption.ResponseHeadersRead, true, ResponseContentType.TransferEncodingChunked)]
        public Task SendAsync_RequestDuration_EnrichmentHandler_Success(HttpCompletionOption completionOption, bool loadIntoBuffer, ResponseContentType responseContentType)
        {
            return LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using HttpClientHandler handler = CreateHttpClientHandler();
                using HttpClient client = CreateHttpClient(new EnrichmentHandler(handler));
                using var recorder = new InstrumentRecorder<double>(GetUnderlyingSocketsHttpHandler(handler).Meter, "request-duration");
                using HttpRequestMessage request = new(HttpMethod.Get, uri) { Version = UseVersion };
                using var response = await client.SendAsync(request, completionOption);

                if (completionOption == HttpCompletionOption.ResponseHeadersRead)
                {
                    // Instrumentation should be delayed until reaching content EOF
                    Assert.Empty(recorder.GetMeasurements());

                    if (loadIntoBuffer)
                    {
                        await response.Content.LoadIntoBufferAsync();
                    }
                    else
                    {
                        using Stream stream = await response.Content.ReadAsStreamAsync();
                        await stream.CopyToAsync(new MemoryStream());
                    }
                }

                Measurement<double> m = recorder.GetMeasurements().Single();
                VerifyRequestDuration(m, uri.Scheme, uri.IdnHost, $"HTTP/{UseVersion}", 200); ;
                Assert.Equal("before!", m.Tags.ToArray().Single(t => t.Key == "before").Value);
            }, async server =>
            {
                if (responseContentType == ResponseContentType.ContentLength)
                {
                    string content = string.Join(' ', Enumerable.Range(0, 100));
                    int contentLength = Encoding.ASCII.GetByteCount(content);
                    await server.AcceptConnectionSendResponseAndCloseAsync(content: content, additionalHeaders: new[] { new HttpHeaderData("Content-Length", $"{contentLength}") });
                }
                else if (responseContentType == ResponseContentType.TransferEncodingChunked)
                {
                    string content = "3\r\nfoo\r\n3\r\nbar\r\n0\r\n\r\n";
                    await server.AcceptConnectionSendResponseAndCloseAsync(content: content, additionalHeaders: new[] { new HttpHeaderData("Transfer-Encoding", "chunked") });
                }
                else
                {
                    // Empty
                    await server.AcceptConnectionSendResponseAndCloseAsync();
                }
            });
        }

        [Fact]
        public Task SendAsync_RequestDuration_EnrichmentHandler_Error()
        {
            return LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using HttpClientHandler handler = CreateHttpClientHandler();
                using HttpClient client = CreateHttpClient(new EnrichmentHandler(handler));

                using var recorder = new InstrumentRecorder<double>(GetUnderlyingSocketsHttpHandler(handler).Meter, "request-duration");

                using HttpRequestMessage request = new(HttpMethod.Get, uri) { Version = UseVersion };

                await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

                Measurement<double> m = recorder.GetMeasurements().Single();
                VerifyRequestDuration(m, uri.Scheme, uri.IdnHost, $"HTTP/{UseVersion}", 200); ;
                Assert.Equal("before!", m.Tags.ToArray().Single(t => t.Key == "before").Value);

            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    // Emulate Content-Length mismatch
                    await connection.SendResponseAsync(headers: new[]
                    {
                        new HttpHeaderData("Content-Length", "1000")
                    }, content: "x");
                });
            });
        }

        
    }

    public class HttpMetricsTest_Http11 : HttpMetricsTest
    {
        protected override Version UseVersion => HttpVersion.Version11;
        public HttpMetricsTest_Http11(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public Task SendAsync_HttpVersionDowngrade_RequestDuration_LogsActualProtocol()
        {
            return LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using HttpClientHandler handler = CreateHttpClientHandler();
                using HttpClient client = CreateHttpClient(handler);

                using var recorder = new InstrumentRecorder<double>(GetUnderlyingSocketsHttpHandler(handler).Meter, "request-duration");

                using HttpRequestMessage request = new(HttpMethod.Get, uri)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                using var response = await client.SendAsync(request);

                Measurement<double> m = recorder.GetMeasurements().Single();
                VerifyRequestDuration(m, uri.Scheme, uri.IdnHost, "HTTP/1.1", 200);

            }, async server =>
            {
                await server.AcceptConnectionSendResponseAndCloseAsync();
            });
        }
    }

    public class HttpMetricsTest_Http20 : HttpMetricsTest
    {
        protected override Version UseVersion => HttpVersion.Version20;
        public HttpMetricsTest_Http20(ITestOutputHelper output) : base(output)
        {
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsNotBrowser))]
        public Task SendAsync_Redirect_RequestDuration_RecordedForEachHttpSpan()
        {
            return GetFactoryForVersion(HttpVersion.Version11).CreateServerAsync((originalServer, originalUri) =>
            {
                return GetFactoryForVersion(HttpVersion.Version20).CreateServerAsync(async (redirectServer, redirectUri) =>
                {
                    using HttpClientHandler handler = CreateHttpClientHandler();
                    using HttpClient client = CreateHttpClient(handler);

                    using var recorder = new InstrumentRecorder<double>(GetUnderlyingSocketsHttpHandler(handler).Meter, "request-duration");
                    using HttpRequestMessage request = new(HttpMethod.Get, originalUri) { Version = HttpVersion.Version20 };

                    Task clientTask = client.SendAsync(request);
                    Task serverTask = originalServer.HandleRequestAsync(HttpStatusCode.Redirect, new[] { new HttpHeaderData("Location", redirectUri.AbsoluteUri) });

                    await Task.WhenAny(clientTask, serverTask);
                    Assert.False(clientTask.IsCompleted, $"{clientTask.Status}: {clientTask.Exception}");
                    await serverTask;

                    serverTask = redirectServer.HandleRequestAsync();
                    await TestHelper.WhenAllCompletedOrAnyFailed(clientTask, serverTask);
                    await clientTask;

                    Assert.Collection(recorder.GetMeasurements(), m0 =>
                    {
                        VerifyRequestDuration(m0, "http", originalUri.IdnHost, $"HTTP/1.1", (int)HttpStatusCode.Redirect);
                    }, m1 =>
                    {
                        VerifyRequestDuration(m1, "https", originalUri.IdnHost, $"HTTP/2.0", (int)HttpStatusCode.OK);
                    });

                }, options: new GenericLoopbackOptions() { UseSsl = true });
            }, options: new GenericLoopbackOptions() { UseSsl = false});
        }
    }

    [ConditionalClass(typeof(HttpClientHandlerTestBase), nameof(IsQuicSupported))]
    public class HttpMetricsTest_Http30 : HttpMetricsTest
    {
        protected override Version UseVersion => HttpVersion.Version30;
        public HttpMetricsTest_Http30(ITestOutputHelper output) : base(output)
        {
        }
    }

    // TODO: Remove when Metrics DI intergration package is available https://github.com/dotnet/aspnetcore/issues/47618
    internal sealed class InstrumentRecorder<T> : IDisposable where T : struct
    {
        private readonly object _lock = new object();
        private readonly string _meterName;
        private readonly string _instrumentName;
        private readonly MeterListener _meterListener;
        private readonly List<Measurement<T>> _values;
        private readonly List<Action<Measurement<T>>> _callbacks;

        public InstrumentRecorder(Meter meter, string instrumentName, object? state = null) : this(new TestMeterRegistry(new List<Meter> { meter }), meter.Name, instrumentName, state)
        {
        }

        public InstrumentRecorder(IMeterRegistry registry, string meterName, string instrumentName, object? state = null)
        {
            _meterName = meterName;
            _instrumentName = instrumentName;
            _callbacks = new List<Action<Measurement<T>>>();
            _values = new List<Measurement<T>>();
            _meterListener = new MeterListener();
            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == _meterName && registry.Contains(instrument.Meter) && instrument.Name == _instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument, state);
                }
            };
            _meterListener.SetMeasurementEventCallback<T>(OnMeasurementRecorded);
            _meterListener.Start();
        }

        private void OnMeasurementRecorded(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            lock (_lock)
            {
                var m = new Measurement<T>(measurement, tags);
                _values.Add(m);

                // Should this happen in the lock?
                // Is there a better way to notify listeners that there are new measurements?
                foreach (var callback in _callbacks)
                {
                    callback(m);
                }
            }
        }

        public void Register(Action<Measurement<T>> callback)
        {
            _callbacks.Add(callback);
        }

        public IReadOnlyList<Measurement<T>> GetMeasurements()
        {
            lock (_lock)
            {
                return _values.ToArray();
            }
        }

        public void Dispose()
        {
            _meterListener.Dispose();
        }
    }

    internal interface IMeterRegistry
    {
        void Add(Meter meter);
        bool Contains(Meter meter);
    }

    internal class TestMeterRegistry : IMeterRegistry
    {
        private readonly List<Meter> _meters;

        public TestMeterRegistry() : this(new List<Meter>())
        {
        }

        public TestMeterRegistry(List<Meter> meters)
        {
            _meters = meters;
        }

        public void Add(Meter meter) => _meters.Add(meter);

        public bool Contains(Meter meter) => _meters.Contains(meter);
    }
}
