
<!-- RDEL-DOCOPS-ID: 39B2FED57740B6BC -->
<!-- RDEL-DOCOPS-SOURCE: .rdel-docops/context/update.md -->
<!-- RDEL-DOCOPS-UTC: 2026-07-06 04:01:27Z -->

<!-- RDEL-DOCOPS-ID: DIAG-FRAMEWORK-1.5.0 -->
<!-- RDEL-DOCOPS-SOURCE: .rdel-docops/context/update.md -->
<!-- RDEL-DOCOPS-UTC: 2026-07-06T03:43:53Z -->

## 2026-07-06 — Infrastructure Diagnostic Framework (RDEL 1.5.0)

Added exception-driven conditional diagnostic system — a flight recorder pattern
that fires at the point of infrastructure failure.

### Problem Solved

Developers cannot access production infrastructure. When sporadic errors occur,
the evidence window closes the moment the error clears. This package makes the
application investigate on the developer's behalf and store queryable forensic
evidence in the UnityEvent bus.

### New Namespaces

- `CVIS.Unity.Core.Diagnostics` — contracts and models
- `CVIS.Unity.Infrastructure.Diagnostics.Probes` — probe implementations

### Key Components

- `ExceptionClassifier` — maps exception type/number to `DiagnosticPlan`
- `DiagnosticPlan` — specifies which probes to run (not always the full chain)
- `IInfrastructureProbe` / `ProbeResult` / `ProbeStep` — composable probe contract
- `DbDiagnosticInterceptor` — EF Core interceptor, fires at point of failure
- `InfrastructureHealthService` — probe registry and orchestrator
- `InfrastructureConnectionException` — enriched exception for non-transient failures

### UnityEvent Bus Extension

Added `EventType = "DIAGNOSTIC"` alongside existing AUDIT, STATUS, DRIFT.
Added `PublishDiagnosticEventAsync` to `IUnityEventPublisher`.
All implementations updated: `ImmediateUnityPublisher`, `NullUnityPublisher`.

### Pattern Query

```sql
SELECT JSON_VALUE(Metadata, '$.HourOfDay'), JSON_VALUE(Metadata, '$.ResolvedAddresses'), COUNT(*)
FROM unity.UnityEvents
WHERE EventType = 'DIAGNOSTIC' AND EventName LIKE 'DB_FAILURE%'
GROUP BY JSON_VALUE(Metadata, '$.HourOfDay'), JSON_VALUE(Metadata, '$.ResolvedAddresses')
ORDER BY COUNT(*) DESC
```

### OpenShift Path

`InfrastructureHealthReport.IsAlive` and `.IsReady` map directly to
Kubernetes liveness and readiness probe semantics. `UnityHealthCheck` adapter
is ready to plug into `AddHealthChecks()` when containerization begins.

