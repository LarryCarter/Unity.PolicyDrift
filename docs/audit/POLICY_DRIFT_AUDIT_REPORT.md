# Policy Drift report audit

The report workflows now run a structured audit after their existing report event is produced.

## Implemented behavior

- Reads `dbo.CA_EPV_REPORTING` through a narrow, read-only EF projection.
- Reads the current `unity.PolicyDriftEval` execution using execution-prefix behavior.
- Reads the relevant `unity.LogEvents` window and preserves policy-block issue parsing.
- Preserves `NO`, `YES`, and `YES+` deletion states and SQL CASE precedence.
- Uses the explicitly temporary `FilterPolicyIdForAudit` compatibility implementation.
- Produces the versioned `com.contollo.policydrift.audit` JSON envelope.
- Embeds the complete executable reference SQL in the envelope and records its SHA-256 and parameters.
- Provides per-summary-field and per-policy parity comparison.

## Transport boundary

The repository has no Kafka producer. The current publisher stores the complete JSON envelope in `UnityEvents` with `TransportStatus=PERSISTED_NOT_SENT_TO_KAFKA`. It does not claim Kafka delivery. A future adapter can implement `IPolicyDriftAuditPublisher` without changing audit classification or schema.

## Production validation still required

The automated suite validates classification, filtering, log correlation, evidence, and mismatch detection. Final database parity requires an authorized completed execution: run [policy-drift-audit-reference.sql](policy-drift-audit-reference.sql) and compare its result with the application envelope from the same database snapshot.

The application must not be declared production-parity-verified until that comparison passes.
