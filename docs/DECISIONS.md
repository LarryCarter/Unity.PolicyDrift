
<!-- RDEL-DOCOPS-ID: A2248BFF8A1021EB -->
<!-- RDEL-DOCOPS-SOURCE: .rdel-docops/decisions/ADR-001-diagnostic-framework.md -->
<!-- RDEL-DOCOPS-UTC: 2026-07-06 04:01:27Z -->

<!-- RDEL-DOCOPS-ID: ADR-001-DIAG-FRAMEWORK -->
<!-- RDEL-DOCOPS-SOURCE: .rdel-docops/decisions/ADR-001-diagnostic-framework.md -->
<!-- RDEL-DOCOPS-UTC: 2026-07-06T03:43:53Z -->

## ADR-001 — Infrastructure Diagnostic Framework

**Date:** 2026-07-06  
**Status:** Accepted  
**Package:** RDEL 1.5.0

### Context

Developers cannot access production infrastructure directly. Sporadic errors
(e.g. SQL Server hostname not found) produce insufficient information to diagnose
the root cause. Ops asks questions the developer cannot answer without prod access.
Evidence is lost the moment the error clears.

### Decision

Build an exception-driven conditional diagnostic framework that fires at the exact
point of infrastructure failure, runs a targeted probe chain, and stores structured
forensic evidence in the existing UnityEvent bus.

Key decisions:
1. Use EF Core `DbCommandInterceptor` — fires at the exact point of failure, not on a timer
2. Exception type/number determines which probes run — no wasted DNS probe after auth failure
3. DNS/TCP failures abort retry immediately — retrying cannot fix network-layer failures
4. Diagnostic events go to UnityEvent bus as EventType=DIAGNOSTIC — no new table needed
5. Write to Serilog and Console first — always survives DB outage
6. Same `DbConnectionProbe` class handles any `DbContext` via named injection

### Consequences

- Developers can answer ops questions with timestamped probe evidence
- Pattern queries on UnityEvents reveal time-of-day and IP correlation
- `IUnityEventPublisher` gains `PublishDiagnosticEventAsync` — breaking change for other implementations
- Probe framework is OpenShift-ready via `InfrastructureHealthReport.IsAlive/IsReady`
- Adding new probes requires no changes to existing code — implement `IInfrastructureProbe` and register

### Alternatives Rejected

- Scheduled health check only — misses the exact failure moment, evidence may be stale
- Separate diagnostics table — redundant with UnityEvent bus already in place
- Inheriting from DbContext — breaks when multiple contexts exist, couples concerns

