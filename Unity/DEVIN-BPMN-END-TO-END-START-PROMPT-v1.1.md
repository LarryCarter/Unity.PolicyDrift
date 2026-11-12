# DEVIN Start Prompt - Unity BPMN Designer, Reusable UI, Capabilities and End-to-End Pilots

You are implementing Unity's workflow composition experience. The supplied v1.1 folder is a **proposed design handoff** to merge with existing repository code. Do not replace established AMIE/BOB interfaces or create duplicate BE projects without source reconciliation. Do not claim bank PROD approval, BPMN full conformance, or real integration from the mock wireframe.

## Read in order

1. `AGENTS.md`, `INDEX.md`, `docs/00-START-HERE.md`, `docs/decisions/DECISION-REGISTER.md`.
2. `docs/ui/13-END-TO-END-BPMN-WORKFLOW-EXPERIENCE.md`, `docs/ui/14-BPMN-PALETTE-NODE-INSPECTORS-DYNAMIC-BINDINGS.md`, `docs/ui/15-REUSABLE-BLAZOR-COMPONENT-CATALOG.md`.
3. `docs/ui/16-EDITOR-ACTIONS-API-AND-TEST-CATALOG.md`, `docs/ui/18-FULL-BPMN-DESIGNER-UI-CONFIGURATION-AND-TEST-RUNBOOK.md`.
4. `docs/contracts/TRIGGER-NAS-KAFKA-DASHBOARD-CONTRACTS-v1.1.md`, `docs/diagrams/DIAGRAMS.md`, `docs/workflows/00-REUSABLE-WORKFLOW-PATTERNS.md` through `05-HYGIENE-SAFE-MOVE-SEPARATE-EXAMPLE.md`.
5. `jobs/bpmn/INDEX.md`, then corresponding job packet; related original `jobs/JOB-*` and `jobs/interaction/*`.

## Phase 0 (first PR): READ ONLY source reconciliation

Locate actual `/Project/Folder/File.cs` paths for existing AMIE runtime, BOB compiler/state/runtime, Blazor BE app, CyberArk client and handlers, Kafka contracts, MSSQL persistence, NAS EF backend/API, AutoSys job and Policy Drift SQL/parser/PolicyId sanitizer. Report which proposed components exist, missing operations, approved dependencies, role/policy conventions and failing tests. Do not implement speculative duplicate interfaces. Deliver proposed PR dependency graph and updated contract mapping.

## Phase 1: build minimum vertical seam

1. Capability handler -> trusted SDK manifest -> approved registry -> BE dynamic operation picker and schema-driven inspector.
2. BPMN Start -> ServiceTask -> AMIE BusinessRuleTask -> ExclusiveGateway -> End with XML+DI import/export, saving revisions, typed bindings and execution through shared BOB host.
3. Kafka command/outcome with correlation and safe wait, audit, run scope and idempotency.
4. BE screen action maps to APIs, negative role tests and a small read-only NAS/CyberArk operation.

## Phase 2: first full pilot DCMS

CSV from BE -> staged batch/row GUID -> central BOB -> Kafka -> embedded CyberArk BOB -> AMIE validation/custom attribute map -> CyberArk add/update -> durable outcome -> report + Kafka Email -> business dashboard and BPMN monitor/audit. Prove five-row fixture and failure reconciliation; mock external calls until bank-approved DEV access.

## Phase 3: second examples

Account Delete: guarded approval/dry-run/verification using mock or approved DEV account; no direct PROD operation. AutoSys Policy Drift: trusted job trigger -> ZIP/INI parser -> PolicyId normalization -> baseline data/EF -> AMIE NO_DRIFT/DRIFT/MISSING_BASELINE -> report/audit. Distinguish account hygiene safe-move from ZIP/INI policy baseline drift. Where actual NAS or AutoSys contracts are unknown, record blockers and use typed test adapters rather than invented endpoints.

## Each PR must include

Exact new/updated/deleted file paths and minimal summary, source truth vs proposed assumption, API contract/regression, typed manifests, security/role denial, version pin, signed/ticket control gaps, masked audit evidence, Dev build/test results, known failures, rollback instructions. When building a UI component, identify which `CMP-*` click contract and which reusable component from UI 15 it satisfies. When building BOB semantic support, prove actual execution with tests; XML well-formedness alone is insufficient.
