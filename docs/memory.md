
<!-- RDEL-DOCOPS-ID: 6FA16DC74EEB219C -->
<!-- RDEL-DOCOPS-SOURCE: .rdel-docops/memory/update.md -->
<!-- RDEL-DOCOPS-UTC: 2026-07-06 04:01:27Z -->

<!-- RDEL-DOCOPS-ID: DIAG-FRAMEWORK-MEMORY-1.5.0 -->
<!-- RDEL-DOCOPS-SOURCE: .rdel-docops/memory/update.md -->
<!-- RDEL-DOCOPS-UTC: 2026-07-06T03:43:53Z -->

## Diagnostic Framework — Reusable Patterns

### Exception Classifier Pattern

`ExceptionClassifier.Classify(ex)` is the right first step before any probe.
SQL error numbers tell you which layer failed before any probe runs.
Never probe DNS after a 18456 auth failure — DNS worked or you'd have a different error.

### Probe Registration Pattern

Same probe class handles multiple contexts via named injection:
```csharp
services.AddScoped<IInfrastructureProbe>(sp => new DbConnectionProbe(
    probeName: "PolicyDb", dbContext: sp.GetRequiredService<PolicyDbContext>(), ...));
```

### Event Bus Diagnostic Pattern

Diagnostic events go to UnityEvent bus with EventType = "DIAGNOSTIC".
Write is always best-effort — Serilog + Console are primary fallback when DB is down.
Never throw from PublishDiagnosticEventAsync.

### Abort vs Retry Decision

DNS/TCP failures → abort immediately (retrying is pointless).
SQL_NO_PROCESS_AT_PIPE, EF_CONTEXT_FAILURE → allow EF retry strategy to continue.

