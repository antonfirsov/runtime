// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//using System.Diagnostics;
//using System.Diagnostics.Metrics;
//using System.Threading;
//using System.Threading.Tasks;

//namespace System.Net.Http;

//internal sealed class MetricsHandler : HttpMessageHandlerStage
//{
//    private readonly HttpMessageHandler _innerHandler;
//    private readonly HttpMetrics _metrics;

//    public MetricsHandler(HttpMessageHandler innerHandler, HttpMetrics metrics)
//    {
//        _metrics = metrics;
//        _innerHandler = innerHandler;
//    }

//    internal override ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool async, CancellationToken cancellationToken)
//    {
//        if (_metrics.RequestCountersEnabled())
//        {
//            ArgumentNullException.ThrowIfNull(request);
//            return SendAsyncCore(request, async, cancellationToken);
//        }
//        else
//        {
//            return async ?
//                new ValueTask<HttpResponseMessage>(_innerHandler.SendAsync(request, cancellationToken)) :
//                new ValueTask<HttpResponseMessage>(_innerHandler.Send(request, cancellationToken));
//        }
//    }

//    private async ValueTask<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request, bool async, CancellationToken cancellationToken)
//    {
//        long startTimestamp = Stopwatch.GetTimestamp();
//        HttpResponseMessage? response = null;
//        Exception? requestEx = null;
//        try
//        {
//            response = async ?
//                await _innerHandler.SendAsync(request, cancellationToken).ConfigureAwait(false) :
//                _innerHandler.Send(request, cancellationToken);
//            return response;
//        }
//        catch (Exception ex)
//        {
//            requestEx = ex;
//            throw;
//        }
//        finally
//        {
//            _metrics.RequestStop(request, response, requestEx, startTimestamp, Stopwatch.GetTimestamp());
//        }
//    }

//    protected override void Dispose(bool disposing)
//    {
//        if (disposing)
//        {
//            _innerHandler.Dispose();
//        }

//        base.Dispose(disposing);
//    }
//}
