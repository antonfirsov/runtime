// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Metrics;

namespace System.Net.Http
{
    public sealed class HttpRequestDiagnosticOptions
    {
        internal List<Action<HttpActivityEnrichmentContext>>? _activityEnrichmentCallbacks;
        internal List<Action<HttpMetricsEnrichmentContext>>? _metricsEnrichmentCallbacks;
        internal List<Predicate<HttpRequestMessage>>? _activityFilters;

        public IList<Predicate<HttpRequestMessage>> ActivityFilters => _activityFilters ??= new();
        public IList<Action<HttpActivityEnrichmentContext>> ActivityEnrichmentCallbacks => _activityEnrichmentCallbacks ??= new();
        public IList<Action<HttpMetricsEnrichmentContext>> MetricsEnrichmentCallbacks => _metricsEnrichmentCallbacks ??= new();
        public Func<HttpRequestMessage, string?>? UriRedactorCallback { get; set; }
    }
}
