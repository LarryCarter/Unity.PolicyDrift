using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CVIS.Unity.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CVIS.Unity.Infrastructure.Diagnostics.Probes
{
    /// <summary>
    /// Reusable HTTP API probe base class.
    ///
    /// Extend this for any REST API dependency:
    ///   ServiceNowProbe : HttpApiProbe
    ///   PvwaProbe       : HttpApiProbe
    ///   Future probes   : HttpApiProbe
    ///
    /// Probe chain: DNS → HTTP reachability → Auth check (optional override)
    ///
    /// Override ProbeAuthAsync() in subclass to add authenticated
    /// API calls for services that require credential validation.
    /// Default implementation returns a success no-op.
    /// </summary>
    public abstract class HttpApiProbe : IInfrastructureProbe
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;

        protected abstract string BaseUrl { get; }
        protected abstract string HealthCheckPath { get; }
        protected abstract string HttpClientName { get; }

        public abstract string ProbeName { get; }
        public abstract string Category { get; }

        protected HttpApiProbe(
            IHttpClientFactory httpClientFactory,
            ILogger logger)
        {
            _httpClientFactory = httpClientFactory
                ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ProbeResult> RunAsync(
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            var result = new ProbeResult
            {
                ProbeName     = ProbeName,
                Category      = Category,
                CorrelationId = correlationId ?? Guid.NewGuid().ToString()
            };
            result.Metadata["BaseUrl"]         = BaseUrl;
            result.Metadata["HealthCheckPath"] = HealthCheckPath;

            // ── Step 1: DNS ──────────────────────────────────────────
            var uri     = new Uri(string.IsNullOrWhiteSpace(BaseUrl)
                ? "http://unconfigured" : BaseUrl);
            var dnsStep = await ProbeDnsAsync(uri.Host, cancellationToken);
            result.Steps.Add(dnsStep);

            // ── Step 2: HTTP reachability ────────────────────────────
            var httpStep = dnsStep.Success
                ? await ProbeHttpAsync(cancellationToken)
                : ProbeStep.Skip("HttpGet", "Skipped — DNS failed");
            result.Steps.Add(httpStep);

            // ── Step 3: Auth check (override in subclass) ────────────
            var authStep = httpStep.Success && !httpStep.Skipped
                ? await ProbeAuthAsync(cancellationToken)
                : ProbeStep.Skip("AuthCheck", "Skipped — HTTP failed");
            result.Steps.Add(authStep);

            ApplyRootCause(result);
            return result;
        }

        /// <summary>
        /// Override in subclass to perform an authenticated API call.
        /// Default returns success — meaning auth probing is not configured.
        /// </summary>
        protected virtual Task<ProbeStep> ProbeAuthAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProbeStep
            {
                StepName  = "AuthCheck",
                Success   = true,
                ElapsedMs = 0,
                Message   = "Auth probe not configured for this API — HTTP reachability confirmed only"
            });

        private async Task<ProbeStep> ProbeDnsAsync(
            string host, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);
                sw.Stop();
                return new ProbeStep
                {
                    StepName  = "DNS",
                    Success   = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message   = $"DNS resolved {host}"
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "DNS",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = $"DNS failed for {host}",
                    ErrorDetail = ex.Message
                };
            }
        }

        private async Task<ProbeStep> ProbeHttpAsync(
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var client   = _httpClientFactory.CreateClient(HttpClientName);
                var url      = $"{BaseUrl.TrimEnd('/')}/{HealthCheckPath.TrimStart('/')}";
                var response = await client.GetAsync(url, cancellationToken);
                sw.Stop();

                return new ProbeStep
                {
                    StepName    = "HttpGet",
                    Success     = response.IsSuccessStatusCode,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    ErrorDetail = response.IsSuccessStatusCode
                        ? null
                        : $"Unexpected status {(int)response.StatusCode}"
                };
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "HttpGet",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = "HTTP request failed",
                    ErrorDetail = ex.Message
                };
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "HttpGet",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = "HTTP request timed out",
                    ErrorDetail = $"No response from {BaseUrl}"
                };
            }
        }

        private static void ApplyRootCause(ProbeResult result)
        {
            var failed = result.Steps.Find(s => !s.Success && !s.Skipped);

            if (failed == null)
            {
                result.IsHealthy         = true;
                result.RootCause         = "NONE";
                result.Severity          = "INFO";
                result.RecommendedAction = $"{result.ProbeName} API is reachable.";
                return;
            }

            result.IsHealthy = false;

            (result.RootCause, result.Severity, result.RecommendedAction) =
                failed.StepName switch
                {
                    "DNS"       => ("API_DNS_FAILURE",  "CRITICAL",
                                   $"Cannot resolve {result.ProbeName} hostname."),
                    "HttpGet"   => ("API_UNREACHABLE",  "CRITICAL",
                                   $"{result.ProbeName} API did not respond."),
                    "AuthCheck" => ("API_AUTH_FAILURE", "HIGH",
                                   $"{result.ProbeName} reachable but auth failed."),
                    _           => ("API_UNKNOWN",      "HIGH",
                                   $"{result.ProbeName} probe failed at {failed.StepName}.")
                };
        }
    }
}
