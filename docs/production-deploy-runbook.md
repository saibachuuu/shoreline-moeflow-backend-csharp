# Production deploy, rollback and cutover runbook (C# backend)

> Status: **NOT validated against real R2 / real SMTP / production.**
> Every step below was checked only against the disposable loopback stack
> (mongo + s3mock + mailpit). The R2/SMTP commands are templates: fill in
> values, run them from the production host, never commit them.

## 0. Pre-deploy checks (all must pass)

1. Build is green on the pinned SDK:
   `dotnet build Shoreline.sln --nologo`
2. Migration check is a **placeholder**: `Infrastructure/Migrations` is owned
   by another agent. Until it lands, the "migration" is the startup index
   initialization (`MongoStore.InitializeAsync` + module `Initialize*Async`
   in `Program.cs`); a deploy is only safe if the new image boots once
   against a copy of production data without index errors.
3. Record the image digest and keep the previous one for rollback:
   `docker pull $SHORELINE_IMAGE && docker inspect --format='{{.RepoDigests}}' $SHORELINE_IMAGE`
4. Take a full MongoDB backup and verify it restores (dump + restore to a
   scratch database, boot the Python 1.2.x reference against it).
5. `docker compose -f compose.prod.example.yml config` renders without
   "variable is not set" errors (every required value fails fast).
6. Or run all of the above read-only via `pwsh scripts/verify-deploy.ps1`.

## 1. Start order

External MongoDB and RabbitMQ first (managed services, outside compose),
then the C# roles, then the edge proxy:

```
mongo (external, reachable) → worker → api → nginx (external)
```

- `docker compose -f compose.prod.example.yml up -d worker`, wait for
  `/health/ready` 200 on the worker host (it serves HTTP on 8080 too, but
  unpublished; `docker exec` a loopback curl or check logs for index init).
- `docker compose -f compose.prod.example.yml up -d api`, wait for
  `GET /health/ready` → `{"status":"ok"}` through nginx.
- Only then point nginx upstream at the new api container.

Rolling update: start the new worker, then the new api, move nginx upstream,
then stop the old pair. Never run old + new workers against the same
database longer than the cutover window (Mongo leases make it safe, but two
consumers double-claim and spam `owner` churn).

## 2. Rollback

Prefer image rollback; the database needs attention only when downgrading
major behavior or returning to Python.

- C# → previous C# image: `docker compose ... up -d` with the recorded
  previous digest. No data step: schema changes are additive (`csharp_*`
  collections, additive indexes).
- C# → Python (legacy): stop api AND worker first (a running worker keeps
  writing lease fields), restore-or-strip the database, then start Python:
  1. `docker compose -f compose.prod.example.yml stop api worker`
  2. Backup, then run `tools/legacy-rollback-strip.js` (see
     `docs/legacy-rollback.md`): removes C#-only fields and the conflicting
     index names; Python recreates its own indexes on first access.
  3. Migration-version handling: there is no down-migration. The only
     forward state C# writes outside `csharp_*` collections is removable by
     the strip script; if the deploy wrote anything else, restore the
     pre-deploy backup instead of stripping.
  4. Smoke-test `/v1/user/info`, a project detail, files, outputs, members.

## 3. Graceful-shutdown verification

- `HostOptions.ShutdownTimeout` is 30s (`Program.cs`); compose gives both
  roles `stop_grace_period: 40s` so SIGTERM always finishes inside budget.
- Verify once per image: `docker stop` the worker while a job is running,
  then confirm the job row is NOT lost — it stays `running` under lease and
  is reclaimed after `lease_until` (2 min), or finishes if the handler won
  the race. No `failed` rows attributable to the restart.
- In-flight HTTP requests drain the same way via the api's 30s budget.

## 4. Health checks and alert thresholds

| Signal | Source | Threshold / action |
| --- | --- | --- |
| `/health/live` non-200 | api, load balancer | page: process is dead |
| `/health/ready` non-200 > 60s | api, nginx passive check | page: MongoDB unreachable from app |
| `csharp_job` `pending` age > 10 min | mongo query | warn: worker stalled or `Jobs:WorkerEnabled=false` everywhere |
| `csharp_job` `failed` growth | mongo query | warn: poison payload; inspect, do not auto-retry |
| Mongo pool exhaustion / selection timeouts | app logs (`MongoStore`, 5s selection timeout) | page: connection storm or primary failover |
| 5xx rate from nginx upstream | nginx access log | page on spike; correlate with deploy window |

## 5. Real R2 / SMTP acceptance templates (fill in, do not commit)

```bash
# R2 object round-trip through the app (no credentials on the command line;
# export them in the shell, never in a file that gets committed).
curl -s http://127.0.0.1:8080/ping
# Upload a real project file via the frontend, then confirm the object exists:
#   aws --endpoint-url "$STORAGE_ENDPOINT" s3 ls "s3://$STORAGE_BUCKET/<prefix>/" \
#     --region auto
# Download it back through /v1 download and diff against the original.

# SMTP end-to-end (Mailpit is test-only; point at the real relay):
#   1. Trigger an email-code request for a real mailbox.
#   2. Confirm delivery and that the code verifies within its TTL.
#   3. Confirm a second request for the same address reuses one durable code
#      (see EmailTests.ConcurrentIssueCreatesOneDurableCode*).
```

## 6. Celery → C# cutover / drain checklist

The C# `JobWorker` consumes the Mongo `csharp_job` collection; it is NOT a
Celery worker and never touches RabbitMQ. Cutover means draining the old
broker before traffic moves:

1. Stop the Celery beat scheduler (no new periodic tasks).
2. Stop Celery workers after current tasks finish; confirm the RabbitMQ
   queues are empty via the management API
   (`$RABBITMQ_MANAGEMENT_URL/api/queues` → `messages` == 0 on every queue).
3. Confirm no in-flight jobs: no unacked deliveries in RabbitMQ AND
   `csharp_job` has no `running` rows started by Python-side bridges.
4. Deploy C# worker with `Jobs:WorkerEnabled=true`, api with `false`.
5. Cut nginx traffic to the C# api. Watch `/health/ready`, 5xx rate, and
   `csharp_job` `pending` age for 30 minutes before decommissioning Flask.
6. Keep the last Flask image + the pre-cutover Mongo backup for one release
   cycle; rollback is section 2 (image pin or legacy strip).
