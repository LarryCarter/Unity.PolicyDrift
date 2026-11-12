# UI 18 - Full User/Developer/Operations Runbook: Create BPMN Workflow Start to Finish

**Purpose:** DEVIN uses this as UI click acceptance script AND backend API regression. All endpoint names are proposed, not verified against the Unity repository. This guide is executable as a manual test only after the referenced services exist; the supplied wireframe is a simulation. Use DEV data, approved test identities and non-destructive substitutes unless explicitly authorized.

## Roles / test accounts

`WorkflowAuthor` may draft, configure, test and request review in scoped DEV applications. `WorkflowApprover` reviews and records required evidence. `ApplicationExecutor` may execute approved workflow for owned app/scope and view business status. `Operations` may activate approved releases and request pause/resume/cancel/restart subject to runbooks. `Auditor` may query redacted immutable evidence. `CapabilityDeveloper` may write handlers but does not inherit production deployment rights. These are proposed role names, reconcile with bank SSO/RBAC.

## Walkthrough A - New DCMS workflow from empty canvas

| Step | Exact BE interaction | Data configured | Expected server result and UI reaction | Test proof |
|---|---|---|---|---|
| A01 | Workbench > App selector `DCMS` | app scope | `GET /apps/DCMS`, role-scoped modules | unauthorized app hidden AND API 403 |
| A02 | Workflow Library > New > Blank BPMN | name `DCMS.BatchEnrichment`, owner, runbook | POST draft -> draftId/revision | app-specific catalog record, no shared code edit |
| A03 | Create Wizard > Trigger | `Manual CSV` + `CsvSchema.DCMS@1` | PUT trigger, form schema active | missing column blocks start |
| A04 | Triggers > Input mapper | `FileRef`, `RequestedBy`, `BatchCorrelation` | required fields bound | mappings survive reload |
| A05 | Data Sources > Add | Data/NAS stage operation (actual owner verified) | registry operation descriptor/permissions | no table/SQL editor |
| A06 | Data Sources > Configure | fileRef -> StageBatch fileRef; result batchId -> workflow context | typed binding validation | mismatch highlights field |
| A07 | Canvas > Add Start | `BPMN StartEvent` | stable ID + DI bounds | import/export preserves |
| A08 | Canvas > Add Service Task | `StageBatch` approved operation/version | Inspector shows required fileRef, output batchId | unknown op prohibited |
| A09 | Canvas > Connect Start -> Stage | sequence flow | draft graph edit, no backend side effect | undo/redo reverses |
| A10 | Canvas > Add Call Activity | `CyberArk.ProcessRows@v1`, embedded target | pins subprocess inputs, host, outputs | missing subprocess blocks publish |
| A11 | Embedded Workflow Editor > open subprocess | sequential multi-instance, collection pageRef, concurrency=1 | local BOB schema/graph | 3 rows ordered test |
| A12 | Canvas > Add GetAccount | CyberArk.GetAccount from capability registry | accountId from `CurrentRow.AccountId` | same handler reusable elsewhere |
| A13 | Canvas > Add AMIE Rule | DCMS.Eligibility from existing AMIE | facts CurrentRow, Account, Platform; output ALLOW/SKIP/REJECT | rule pinned, sample test |
| A14 | Canvas > Add XOR Gateway | ALLOW -> Map; SKIP/REJECT -> PersistReason | named/default flows | invalid branching blocked |
| A15 | Canvas > Add AMIE Mapping | approved custom attribute mapping rule | four inputs to changeSet; null overwrite behavior | typed changeSet |
| A16 | Canvas > Add ApplyInMemory | CyberArk.ApplyCustomAttributes | account + changeSet -> modifiedAccount | no external write yet |
| A17 | Canvas > Add AddOrUpdate | CyberArk.AddOrUpdateAccount | modified account; Write classification + no blind retry | outcome confirmed vs unknown |
| A18 | Canvas > Add RowOutcome | Data/NAS MarkRowOutcome | batchId, rowGuid, validation, write outcome, reason | one terminal row record |
| A19 | Canvas > connect loop back | next row until all processed | BOB sequential multi-instance | no duplicate row on resume |
| A20 | Central canvas > Add durable wait | Kafka correlated process outcome by run+command ID | published version pins/wait token persisted | duplicate event ignored |
| A21 | Central canvas > Reporting + Email | reportRef to Email via Kafka | separate send status | email fail not account fail |
| A22 | Canvas > Add End and failure branches | result and error classification | reachable and deterministic | graph checker passes |
| A23 | Toolbar > Save Draft | XML, DI, bindings + ETag | new revision | 409 concurrent edit works |
| A24 | Toolbar > Validate | BPMN graph, all operations/rules, scopes, retry | issue list / valid summary | no fake green for missing handler |
| A25 | Toolbar > Test | five-row DEV fixture | accepted test run, activity and outcome evidence | mock versus live labeled |
| A26 | Workflow > Review > Publish | changeRef, approver, digest | immutable version in DEV | cannot self-publish if policy forbids |
| A27 | Workbench > DCMS > Upload CSV | selected active version + fixture CSV | fileRef, batchId, runId | 202 != completed |
| A28 | Dashboard > Batch 81492 | counts/rows/report | GET projection + SignalR/SSE hint | refresh / reconnect works |
| A29 | Monitor > Run details | BPMN node overlay/causation | GET snapshot/timeline | technical permission separately enforced |
| A30 | Audit > reconstruct | runId and rowGuid | versions, actor, rule outcome, write outcome, changeRef | full trace with masking |

## Walkthrough B - NAS EF API integration

**Before UI demo:** SDK registry shows approved NAS service and operations. Inspect existing NAS source/Swagger to confirm names/DTOs/EF entities. Test `NAS.GetRowsPage` or equivalent end-to-end with a minimal authorized DEV batch.

1. BE > Capability Catalog > search `NAS` > select service > see owner, contract version, health, operation list and permitted environments.
2. Operation Inspector > choose GetRowsPage / equivalent; automatically display batch ID, page/cursor and maxPageSize from manifest schema.
3. Click Add to Workflow > service task appears with binding inspector; map prior `StageBatch.result.batchId` to required field; choose approved execution mode.
4. Click Preview > use redacted fixture; compare output schema fields to `CurrentRow` type in AMIE facts.
5. Click Validate > schema + data owner + permission policy check; publish DEV version; run and prove EF backend via API telemetry, without direct external table access.

## Walkthrough C - Account Delete (safe DEV)

1. App Studio > Create `PAM.AccountDeletion`; Workflow Library > template Guarded Write.
2. Trigger > Form / API; require `accountRef`, `reasonCode`, approved change/request ref, `dryRun=true` default.
3. Add CyberArk.GetAccount and AMIE DeleteEligibility; choose result path, denial outcome and details.
4. Add approval wait: only enable real BPMN User Task if supported; otherwise registered Control.RequestApproval + durable message wait with correlation and human form.
5. Add CyberArk.DeleteAccount operation; inspector displays destructive write warning, permission, no blind retry and verification. Require explicit confirmation in UI.
6. Validate denial cases and stale account revision. Run with stub/non-production approved fixture; status flow shows AwaitingApproval, Approved, DeletePending, Verified/Rejected/Unknown.
7. Audit export contains authorized actor, separate approver, change ID and confirmed outcome; no production account deletion in test fixture.

## Walkthrough D - AutoSys Policy Drift

1. App Studio > Policy Drift > New > Scheduled Policy Ingestion template.
2. Trigger Designer > AutoSys Job > choose approved job key/version from registry (not free text); assign `sourceRunId`, jobName, scheduled timestamp, dedupe window, overlap mode and inputRef schema.
3. Data Sources > choose EPV policy bundle acquisition and baseline EF API operations. Rule Editor > PolicyId sanitizer + attribute comparison + `MISSING_BASELINE` branch.
4. BPMN canvas > download ZIP, recursively parse INI, sequential per-policy evaluate, persist reasons/diffs, report/email; implement fatal download/unzip failure path and row-level invalid PolicyID behavior.
5. Validate > tests for nested ZIP, same-policy NO_DRIFT, changed DRIFT, missing baseline, invalid PolicyID, wrong AutoSys service identity and duplicate sourceRunId.
6. DEV adapter calls secured Unity trigger endpoint as AutoSys would. BE runtime shows accepted run and final summary; second identical call returns same runId, no second processing.
7. Complete evidence: scheduler job ID, Unity run ID, exact definitions/rules, source input hash/ref, EF result and report/email status.

## Failure and denial walkthroughs (all scenarios)

- User without Workflow.Edit clicks Create/Publish via direct HTTP: API 403 and audit policy-denial event.
- Operation disappeared or version disabled after editing: Validation blocker; published historic version remains viewable.
- Import malformed BPMN: per-element error; editor unchanged; no truncation of current draft.
- Network disconnect while dragging: canvas local state preserved; no repeated per-pointer remote requests.
- `If-Match` conflict during Save: compare revisions, preserve unsaved client changes, user resolves; never blind overwrite.
- Rule facts missing: highlight source field and suggested approved provider operation; no expression eval fallback.
- AutoSys double-trigger: same run ID returned; audit records deduplicated delivery.
- Kafka outcome late/duplicate after restart: no duplicate workflow token advancement or CyberArk write.
- CyberArk outcome unknown after timeout: require read/reconciliation, no automatic destructive retry.
- No real ServiceNow approval: PROD activation blocked, even if ticket text was typed.
- Business dashboard reconnect after 100 updates: counts from authoritative GET, not cumulative UI increments.

## Completion proof for DEVIN

Attach CI test run, exact source repository PR/commit and paths, approved dependency list, screenshots of each UI and error state, 5-row DCMS fixture result, API traces, screenshot of empty/forbidden/error states, audit SQL reconstruction without raw secrets, pinned package hash, and explicit list of BPMN elements supported versus not supported. No claim of production compliance or release approval without bank signoff.
