# Policy Drift audit implementation plan

## Isolation

This work belongs on `codex/policy-drift-audit-event` and must not be mixed into PR #2.

## Bounded Spark assignments

### A. Dependency verification

Read only. Validate the dependency map against the source and report contradictions. No code.

### B. Domain and event contract

Define strongly typed audit result records and a versioned Kafka envelope. Preserve all SQL result fields, per-policy evidence, execution parameters, SQL version, SHA-256, and retrievable executable SQL. No transport implementation until the approved Kafka contract is known.

### C. Data access

Implement only approved data-source adapters. Add the narrow read-only EF mapping for `dbo.CA_EPV_REPORTING` from the supplied authoritative DDL, using `PolicyID` and `CAFDeletionDate` for this audit. Use EF projections for mapped sources. Do not invent other entities or silently cross databases. Preserve distinct/set semantics, case/collation decisions, date boundaries, and ExecutionId prefix behavior from the SQL.

### D. Classification

Port the SQL CASE precedence exactly. Treat PolicyID filtering, deletion state, and log findings as separate evidence inputs. The temporary `FilterPolicyIdForAudit` compatibility implementation keeps ASCII letters, digits, and hyphen and must carry the required merge-integration TODO. Add table-driven tests for every exclusion reason and precedence combination.

### E. SQL evidence

Keep the complete original reference SQL under version control. Generate or retain an executable parameterized audit SQL artifact for each schema version. Record its SHA-256 and parameters in the event. Never interpolate untrusted values into executable SQL.

### F. Parity harness

For an authorized completed execution, run the original SQL and application implementation against the same data snapshot. Compare sorted per-policy records as well as summary totals. Emit a machine-readable mismatch report and fail on any discrepancy.

## Ten-minute task contract

Each Spark assignment receives exact files, source excerpts, acceptance criteria, prohibited assumptions, expected output files, and test commands. It stops after ten minutes and reports blockers rather than searching broadly or inventing dependencies.

## Main-agent intervention points

The main Codex agent must review:

- every newly proposed data source or schema mapping;
- equivalence of SQL set/collation/date semantics;
- exclusion precedence;
- Kafka payload privacy and size;
- SQL evidence integrity and retrieval;
- all parity mismatches;
- final changes before commit or PR publication.
