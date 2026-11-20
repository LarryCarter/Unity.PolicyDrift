# DEVIN BPMN / UI / Capability Work Packets v1.1

Use **JOB-00 and JOB-01 first**. These are refinements to existing `jobs/` and `jobs/interaction/`, not competing architecture. `/BusinessEnablement/` paths in specs are proposed; use actual repo locations found in read-only source audit.

| ID | Priority | Dependencies | Task |
|---|---|---|---|
| [BE-21](BE-21-shared-blazor-component-foundation.md) | P0 | JOB-00, UI-10 | Shared Blazor component foundation |
| [BE-22](BE-22-workflow-wizard-and-library-lifecycle.md) | P0 | BE-21, JOB-01, UI-12 | Workflow wizard and library lifecycle |
| [BE-23](BE-23-first-party-bpmn-canvas-xml-plus-di.md) | P0 | BE-21, JOB-05 | First-party BPMN canvas XML plus DI |
| [BE-24](BE-24-bpmn-palette-and-node-specific-inspector.md) | P0 | BE-23, SDK-31, BOB-37 | BPMN palette and node-specific inspector |
| [BE-25](BE-25-schema-driven-data-binding-and-amie-rule-editor.md) | P0 | BE-24, BOB-38 | Schema-driven data binding and AMIE rule editor |
| [BE-26](BE-26-triggers-and-input-forms-builder.md) | P0 | BE-21, SDK-32, SDK-35 | Triggers and input forms builder |
| [BE-27](BE-27-compiler-validation-panel-and-dev-test-bench.md) | P0 | BE-24, BE-25, BOB-37 | Compiler validation panel and DEV test bench |
| [BE-28](BE-28-workflow-version-review-and-release-wiring.md) | P0 | BE-22, BE-27, JOB-16 | Workflow version review and release wiring |
| [BE-29](BE-29-business-work-queue-and-near-real-time-dashboard.md) | P0 | BE-21, SDK-32, BOB-37 | Business work queue and near-real-time dashboard |
| [BE-30](BE-30-bpmn-runtime-monitor-controls-and-audit-views.md) | P0 | BE-23, BOB-37, JOB-08, JOB-15 | BPMN runtime monitor, controls and audit views |
| [SDK-31](SDK-31-operation-registry-and-automatic-be-schema-integration.md) | P0 | JOB-00, JOB-01, JOB-02, JOB-03 | Operation registry and automatic BE schema integration |
| [SDK-32](SDK-32-nas-ef-core-owned-data-api-integration.md) | P0 | JOB-00, SDK-31 | NAS EF Core owned-data API integration |
| [SDK-33](SDK-33-cyberark-dcms-reusable-primitives.md) | P0 | SDK-31, BOB-38, JOB-12 | CyberArk DCMS reusable primitives |
| [SDK-34](SDK-34-cyberark-guarded-account-delete-operation.md) | P1 | SDK-33, BOB-37, BE-30 | CyberArk guarded Account Delete operation |
| [SDK-35](SDK-35-autosys-scheduler-trigger-adapter.md) | P1 | JOB-00, BE-26, SDK-31 | AutoSys scheduler trigger adapter |
| [SDK-36](SDK-36-policy-drift-reusable-zip-ini-ef-operations.md) | P1 | SDK-32, SDK-35, BOB-38 | Policy Drift reusable ZIP/INI/EF operations |
| [BOB-37](BOB-37-bpmn-2.0-compiler-shared-runtime-central-embedded-wait.md) | P0 | JOB-01, JOB-05, JOB-06, JOB-07, JOB-08 | BPMN 2.0 compiler, shared runtime, central/embedded wait |
| [BOB-38](BOB-38-existing-amie-adapter-and-typed-binding-graph.md) | P0 | JOB-00, JOB-04, SDK-31 | Existing AMIE adapter and typed binding graph |
| [INT-39](INT-39-dcms-integrated-ui-to-kafka-to-email-proof.md) | P0 | BE-26, BE-27, BE-29, SDK-32, SDK-33, BOB-37, BOB-38 | DCMS integrated UI-to-Kafka-to-email proof |
| [QA-40](QA-40-complete-ui-api-flow-and-second-workload-regression.md) | P0 | BE-21..30, SDK-31..36 as enabled, INT-39 | Complete UI/API flow and second-workload regression |

## Suggested parallel lanes

1. **Contract gate:** JOB-00, JOB-01, SDK-31 and BOB-38 contract reconciliation. Do not create incompatible interfaces in parallel.
2. **Foundation lane:** BOB-37, SDK-31/32/33, BE-21/22/23 and platform Kafka/audit jobs.
3. **Composition lane:** BE-24/25/26/27/28 and operation/rule registry.
4. **Proof lane:** BE-29/30, INT-39, QA-40 and one second no-code workflow reuse.
5. **Second-pilot lane (conditional):** SDK-35+36 Policy Drift; SDK-34 account-delete mock, all gated by safe scopes.

## Stop conditions

- No real NAS controllers/EF owner verified -> implement mock/adapter interface only and report blocked real NAS integration.
- No approved CyberArk delete scope -> only mock/non-destructive account-delete demo.
- No AutoSys access -> demonstrate secure adapter contract using a signed/dev test trigger, not invent live scheduler integration.
- Any missing bank control approval -> DEV demo remains separate from PROD release.
