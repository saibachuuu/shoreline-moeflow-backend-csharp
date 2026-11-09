# Refactor checkpoint — 2026-11-09

The user resumed work after the language catalog checkpoint. Team identity APIs
are now complete; the replacement backend is still incomplete. Do not replace
the Python deployment yet.

## Constraints for continuation

- Keep one business container running one C# process, with shared database/storage
  clients and bounded background jobs. Measure RAM again after equivalent business
  workloads exist; the initial idle sample is not a full-backend comparison.
- Work only on local Windows/WSL/Docker. Do not connect to remote servers or use
  real deployment credentials. Use the disposable services in compose.test.yml.
- Continue making local commits after validated stages. No remote is configured.

## Completed stages

- Shared HTTP, MongoDB and S3 infrastructure; a durable leased job queue; legacy
  password/token verification; captchas and bounded password hashing.
- Login, profile/aliases, email codes and email delivery, password recovery,
  site settings, administrator listing/password reset/admin grants.
- Live team/project identity permissions (`92097b9`) and tag eligibility,
  qualification/open modes, custom tags and protected identities (`b56c42c`).
  Eligibility validation does not replace mutation authorization or version checks.
- Public `/v1/languages`: 106 legacy constants, exact English catalog labels,
  Accept-Language negotiation, original BSON schema and IDs. Existing catalogs
  remain intact. A persisted seed marker lets interrupted new installations resume;
  unique-code upserts permit concurrent initialization without replacing rows.
- `/ping` now returns the legacy plain-text `pong`.
- Public legacy type options and team/project system-role catalogs, with numeric
  permission descriptions and existing-role-ID preservation. Public constants
  are converted offline; the runtime never loads the Python app or configuration.
- Team membership add/restore, soft removal, aliases and display names, worker
  qualifications, database-side joined pagination, capacity recounts and version
  fencing. Display-name synchronization uses bounded project-id batches.
- Team/project custom tag policies, protected system overrides, resets and checks
  preventing removal of tags used by any member status. Mutation audits preserve
  the original identity audit schema.

Current verification: `dotnet test --no-restore --verbosity minimal` passes
97 tests against disposable local MongoDB, S3Mock and Mailpit, including concurrent
admission/edit tests and cross-team/hierarchy checks. The service builds with zero
warnings. Its Docker image has not been
rebuilt; the existing API container still runs the earlier foundation image.

The language data is embedded in the assembly. The offline
`tools/Import-LegacyLanguages.ps1 -LegacyDirectory ../shoreline-moeflow-backend`
regenerates it from public checked-in Python constants and the English PO catalog;
the running C# service requires neither Python nor the old repository.

## Next work

1. Team creation/editing/listing, default project-set creation and user joining.
   Reuse the fixed-size team mutation gates for all membership-changing paths.
2. Complete registration with email-code consumption and automatic team joining.
   The Python registration path uses site_setting.ajt; do not omit this behavior.
3. Project/member services and ownership, position locks, lifecycle operations,
   project sets, listings and identity policy endpoints.
4. Files, targets, labels/translations/tips, complete snapshots and recovery,
   text parsing/OCR/import, exports, notifications, term banks and optional modules.
5. Storage transfers, quota/capacity reconciliation and their progress/cancellation
   endpoints; then full API/data compatibility and equivalent workload RAM testing.

Known issues to revisit: concurrent case-insensitive username uniqueness is not
fully enforced by the current case-sensitive Mongo name index; profile/team writes
and their audit inserts are separate documents. Team gates are local to the single
business process, not distributed locks; do not run a second writer on the same DB.
Durable jobs need real business
handlers beyond security email. Do not describe the current service as ready for
production or infer full compatibility from the existing tests.
