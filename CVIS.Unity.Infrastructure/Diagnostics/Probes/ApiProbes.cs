using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CVIS.Unity.Infrastructure.Diagnostics.Probes
{
    /// <summary>
    /// ServiceNow API reachability probe.
    /// Config key: ServiceNow:BaseUrl
    /// Health check path: api/now/table/incident?sysparm_limit=1
    /// Named HTTP client: "ServiceNow"
    /// </summary>
    public class ServiceNowProbe : HttpApiProbe
    {
        public override string ProbeName            => "ServiceNow";
        public override string Category             => "ExternalApi";
        protected override string HttpClientName    => "ServiceNow";
        protected override string BaseUrl           { get; }
        protected override string HealthCheckPath   => "api/now/table/incident?sysparm_limit=1";

        public ServiceNowProbe(
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            ILogger<ServiceNowProbe> logger)
            : base(httpClientFactory, logger)
        {
            BaseUrl = config["ServiceNow:BaseUrl"] ?? string.Empty;
        }
    }

    /// <summary>
    /// CyberArk PVWA REST API reachability probe.
    /// Config key: CyberArk:PvwaBaseUrl
    /// Health check path: PasswordVault/API/Verify
    /// Named HTTP client: "PVWA"
    /// </summary>
    public class PvwaProbe : HttpApiProbe
    {
        public override string ProbeName            => "PVWA";
        public override string Category             => "ExternalApi";
        protected override string HttpClientName    => "PVWA";
        protected override string BaseUrl           { get; }
        protected override string HealthCheckPath   => "PasswordVault/API/Verify";

        public PvwaProbe(
            IConfiguration config,
            IHttpClientFactory httpClientFactory,
            ILogger<PvwaProbe> logger)
            : base(httpClientFactory, logger)
        {
            BaseUrl = config["CyberArk:PvwaBaseUrl"] ?? string.Empty;
        }
    }
}
