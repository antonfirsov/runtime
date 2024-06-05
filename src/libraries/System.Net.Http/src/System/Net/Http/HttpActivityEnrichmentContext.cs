// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;

namespace System.Net.Http
{
    public sealed class HttpActivityEnrichmentContext
    {
        private Activity _activity;
        private HttpRequestMessage _request;
        private HttpResponseMessage? _response;
        private Exception? _exception;

        internal HttpActivityEnrichmentContext(Activity activity, HttpRequestMessage request, HttpResponseMessage? response, Exception? exception)
        {
            _activity = activity;
            _request = request;
            _response = response;
            _exception = exception;
        }

        public Activity Activity => _activity;
        public HttpRequestMessage Request => _request!;
        public HttpResponseMessage? Response => _response;
        public Exception? Exception => _exception;
    }
}
