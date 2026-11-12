# Documentation index

Two kinds of documents live here: **contracts and runbooks** that describe how
the system behaves today, and **dated records** of a single investigation. Only
the first kind is authoritative.

## Start here

| Document | Purpose |
|---|---|
| [replacement-acceptance-20261111.md](replacement-acceptance-20261111.md) | Go/no-go gate: what must be true before the C# backend replaces Python |
| [production-readiness-checklist.md](production-readiness-checklist.md) | Current status per area, with the evidence behind each entry |
| [production-deploy-runbook.md](production-deploy-runbook.md) | Deploy, rollout, rollback and first-boot procedure |
| [preprod-walkthrough-20261111.md](preprod-walkthrough-20261111.md) | Step-by-step walkthrough for the two remaining gates (real R2/SMTP, production dump) |

## Contracts

| Document | Covers |
|---|---|
| [route-coverage.md](route-coverage.md) | Every legacy route and its C# status |
| [authz-matrix.md](authz-matrix.md) | Endpoint × identity authorization matrix and known deltas |
| [migration-parity.md](migration-parity.md) | Legacy migrations m0000–m0009 and the schema differences they reconcile |
| [legacy-rollback.md](legacy-rollback.md) | Returning control to Python after C# has written |
| [storage-contract.md](storage-contract.md) | Keys, prefixes, signing, CDN mode, R2 behaviour |
| [notification-contract.md](notification-contract.md) | Publish/deliver state machine, the four mail switches, SMTP behaviour |
| [job-semantics.md](job-semantics.md) | Queue fields, leases, retries, shutdown behaviour |
| [celery-cutover-analysis.md](celery-cutover-analysis.md) | Why the C# stack does not consume RabbitMQ, and how to drain instead |
| [mit-bridge-spike.md](mit-bridge-spike.md) | MIT task protocol and the two supported bridge shapes |
| [legacy-environment.md](legacy-environment.md) | Legacy environment variable names and their mapping |
| [parity-harness.md](parity-harness.md) | How to run the read probe and the dual-database replay |

## Module compatibility notes

One file per domain, written while the domain was ported. They record the exact
legacy behaviour that was reproduced, including error codes and edge cases:

`additional-lists`, `admin-users`, `application`, `archive-import`,
`avatar-storage-overview`, `cleanup`, `files`, `imports`, `label-idempotent-writes`,
`label-snapshots`, `labels`, `notifications`, `notification-management`,
`notification-publishing`, `outputs`, `partner-search`, `presence`,
`proofread-feedback`, `storage`, `storage-pool`, `team-dissolution`,
`team-insight`, `terms`, `thumbnails`, `ziteng-partner` — all suffixed with
`-compatibility.md` (or their own name where noted).

## Dated records

These describe a single night's investigation. They are kept only where they
hold evidence that has not been folded into a contract document above.

- [readiness-20261111.md](readiness-20261111.md) — the 1149-test baseline and the
  reasoning that led to the current go/no-go verdict
- [runtime-differential.md](runtime-differential.md) — first differential runs
  against a live Python instance
- [image-reproducibility.md](image-reproducibility.md) — digest pinning and the
  first-boot smoke test that exposed the index-name conflict

Older progress snapshots and handoff notes were removed once their conclusions
landed in the documents above; `git log --diff-filter=D -- docs/` lists them if
the history is ever needed.
