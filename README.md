# Shoreline backend in C#

A drop-in replacement for the Python backend, written in ASP.NET Core / .NET 10.
It reads and writes the same MongoDB documents, the same object-storage keys and
the same HTTP contract, so the two stacks can share one database during cutover.

**Status:** feature-complete against the Python API and verified on 1157
automated tests plus differential replay against a Python oracle on cloned
databases. Pre-production use is ready. Production cutover still needs a
walkthrough with real R2/SMTP credentials and an explicit acceptance of the
listed risks — see [docs/production-readiness-checklist.md](docs/production-readiness-checklist.md).

## What it is

One process serves HTTP and consumes a durable MongoDB job queue. MongoDB and
S3 clients are shared; password hashing has its own concurrency limit. There is
no RabbitMQ, no Celery and no second worker container by default — the job queue
lives in the `csharp_job` collection with atomic leases, retries and crash
recovery.

```text
src/Shoreline.Api/
  Features/         endpoints and their services (one file per domain)
  Modules/          optional modules: archive import, Ziteng partner, MIT bridge
  Infrastructure/   MongoStore, schema preflight, migrations runner, SSRF guard
  Security/         legacy itsdangerous token compatibility, permissions, errors
  Storage/          ObjectStorage, S3/R2 and Local backends, project router
  Notifications/    publishing, delivery, mail gating
  Jobs/             durable queue, worker, background scanners
  Formats/          LabelPlus, archives, JSON compatibility formats
tests/Shoreline.Tests/   xUnit suite (1157 tests)
tools/                   full-suite runner, parity probe, differential replay
docs/                    contracts, parity notes and operational runbooks
```

## Relationship to the Python stack

Both stacks can serve the same database, but only one should write at a time.

| Concern | Rule |
|---|---|
| Sessions | `Auth__Secret` **must equal** the legacy `SECRET_KEY`, otherwise every existing token fails with `BadTokenError` / 1004 |
| Migrations | Legacy ledger kept; C# rows are tagged `runner:"csharp"` and have their own checksum |
| Storage keys | Legacy `OSS_KEY_PREFIX=storage/` maps to `Storage__KeyPrefix=storage/`; a mismatch turns every old object into a 404 |
| Object storage | `STORAGE_TYPE=OSS` + `OSS_BUCKET_STYLE=R2` resolves to the S3 backend; native Alibaba OSS is rejected on purpose |
| Rollback | `tools/legacy-rollback-strip.js` unloads C# fields, drops C# indexes and clears the migration lock before Python takes over again |

The legacy environment variable names are also understood directly
(`SECRET_KEY`, `MONGODB_URI`, `EMAIL_*`, `OSS_*`, `ENABLE_NOTIFICATIONS`,
`NOTIFICATION_*`, `ZITENG_PARTNER_API_KEY`, …) — see
[docs/legacy-environment.md](docs/legacy-environment.md). Explicit `Section__Key`
values win over legacy names.

## Quick start (local)

```bash
# disposable MongoDB + S3Mock + Mailpit
docker compose -f compose.test.yml up -d mongo s3 mailpit

dotnet restore
dotnet build Shoreline.sln
dotnet test Shoreline.sln --filter "FullyQualifiedName~Storage"
```

Full suite on isolated ports (creates and removes its own stack):

```bash
pwsh tools/Run-FullSuite.ps1 -BasePort 33400
```

Default test endpoints are MongoDB `27128`, S3Mock `29128`, Mailpit SMTP
`21128` / UI `28128`. Tests use random databases and delete only those.

## Deployment

The recommended deployment lives in the `shoreline-moeflow-deploy` repository:
`docker-compose.csharp.yml` plus `.env-csharp.sample`. It runs one backend
container, one nginx frontend and joins the network of an existing MongoDB.

Minimum configuration (environment variables, `__` maps to `:`):

- `Mongo__Uri` — must include the database name
- `Auth__Secret`, `Auth__AdminEmail`
- `Storage__Type`/`Endpoint`/`Bucket`/`AccessKey`/`SecretKey`, plus
  `Storage__KeyPrefix` and, for public URLs, `Storage__Domain` + `Storage__ViaCdn`
- `Mail__*` when mail is enabled

Operational switches:

| Key | Default | Effect |
|---|---|---|
| `Jobs__WorkerEnabled` | true | consume the durable job queue in this process |
| `Jobs__Concurrency` | 1 | async claim loops (1–8); each is ~10 KB, not a process |
| `Jobs__MaxAttempts` | 5 | delivery attempts before a job is marked `failed` |
| `Migrations__Mode` | `check` | `check` refuses to serve when a migration is pending; `apply` runs them (first boot only) |
| `Migrations__Enabled` | true | set false on replica/worker roles |
| `Notifications__WorkerEnabled` | false | publish/deliver notification mail in the background |
| `StoragePool__WorkerEnabled` | false | run the storage-pool transfer scanner |

Health endpoints: `/ping`, `/health/live`, `/health/ready` (the last one gates on
MongoDB and the migration state). Behind nginx the API is served under `/api`;
the proxy must strip that prefix and raise `client_max_body_size` to at least
the 1 GiB per-file limit.

Full deploy, rollout and rollback procedure:
[docs/production-deploy-runbook.md](docs/production-deploy-runbook.md).

## Verification

- 1157 tests, no failures, no skips (`tools/Run-FullSuite.ps1`, ~1m40s)
- `tools/parity-probe.mjs` compares 51 read endpoints against the Python oracle
- `tools/replay-parity.mjs` replays 11 write flows against two cloned databases
  and diffs both the responses and the resulting documents
- CI (`.github/workflows/check-pr.yml`) runs the same suite on every push

Both differential tools expect a Python instance and an equivalent C# instance;
[docs/parity-harness.md](docs/parity-harness.md) documents how to build that
pair.

## Known limitations

- Native Alibaba OSS signing is not ported; those configurations fail closed.
- OCR endpoints mirror the Python stubs (`501` in both stacks).
- The MIT bridge is optional and needs either in-process handlers or a
  RabbitMQ URL; without either the routes are not mapped.
- Notification mail on port 465 needs `Mail__SecureSocketOptions=SslOnConnect`.
  The legacy default (mandatory STARTTLS) cannot work on implicit TLS — that is
  a pre-existing Python behaviour, reproduced for parity.
- Metrics and load testing were never part of the legacy stack, so they are
  treated as optional enhancements rather than cutover gates.

## Documentation

Start with the acceptance and runbook documents, then reach for the per-domain
contracts:

- [Acceptance gate](docs/replacement-acceptance-20261111.md)
- [Production readiness checklist](docs/production-readiness-checklist.md)
- [Deploy runbook](docs/production-deploy-runbook.md)
- [Migration parity](docs/migration-parity.md) · [Rollback](docs/legacy-rollback.md)
- [Storage contract](docs/storage-contract.md) · [Notification contract](docs/notification-contract.md)
- [Authorization matrix](docs/authz-matrix.md) · [Route coverage](docs/route-coverage.md)
- [Celery cutover analysis](docs/celery-cutover-analysis.md) · [MIT bridge](docs/mit-bridge-spike.md)
- [Parity harness](docs/parity-harness.md)

Files whose names carry `20261110`, `20261111` or `20261112` are dated records of
one investigation; they are history, not current state. [docs/README.md](docs/README.md)
indexes which documents are authoritative.

## Development notes

- One commit per verified change; keep focused test output in the message.
- `dotnet test --filter` for iteration, `tools/Run-FullSuite.ps1` before claiming
  a green baseline.
- Never point tests at a real deployment database, and never commit credentials.
