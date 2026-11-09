# Refactor checkpoint — 2026-11-09

The user resumed work after the language catalog checkpoint. Team identity APIs
and team setup/settings/navigation are now implemented; the replacement backend is still incomplete. Do not replace
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
- Shared archive Fernet encryption compatible with independent Python fixtures,
  legacy plaintext handling and tail-only formatting; external API URL allowlisting.
- Bounded, casefolded name gates; concurrent profile renames cannot create two
  differently cased copies of the same name in the single business process.
- Team membership add/restore, soft removal, aliases and display names, worker
  qualifications, database-side joined pagination, capacity recounts and version
  fencing. Display-name synchronization uses bounded project-id batches.
- Team/project custom tag policies, protected system overrides, resets and checks
  preventing removal of tags used by any member status. Mutation audits preserve
  the original identity audit schema.
- Team creation with a persisted, uncancellable intent, default project-set creation
  and creator membership. HTTP and worker recovery share idempotent upserts; partial
  creation can be retried by the same creator with the original name/settings.
- Team detail/settings APIs preserve the original fields, creator-only configuration
  rules, legacy role IDs and masked archive-key handling. Own-team navigation projects
  only compact fields and calculates page permissions in batches; global discovery
  remains test-only. Trusted joining is internal, not an authorization bypass API.
- Registration `POST /v1/users` ports Python's RegisterSchema + User.create:
  multi-field ValidateError collection before the type-2 code is consumed (local
  failures never burn the one-time code), bounded password hashing also before
  consumption, Python-order whitelist check after consumption (`1012` therefore
  needs a fresh code), insertion under the shared user-name gate with
  duplicate-key fallback, and best-effort `site_setting.ajt` auto-joins via the
  internal trusted join (missing teams skipped; a failed join is logged but the
  persisted account and its token still return, whereas Python would 500 after
  the user was saved). Response is `{"message": "注册成功", "token"}`.
- Project core (`5aab08c`): project sets (create/list/rename/delete with rehome to
  the default set), project create (team-create gating, target creation per
  language list, creator membership in one flow), detail/list views with member
  summaries, project renames with position-lock (ple/plt) guards, and target
  add/list/delete.
- Project members and lifecycle (Python identity parity): member list with
  status/tag/word filters and pagination; idempotent `apply_changes` claim
  ledger (identity_operation) with claim-token CAS and lease-based steal; add
  (user/external/restore), update (display name, tags, soft remove), bind with
  automatic merge when the target user already has a member, explicit merge,
  external hard delete; owner transfer with keep-admin and compensating rollback
  (5109 MEMBERS_ALREADY_EXISTS is only raised on duplicate-key races, matching
  Python); `complete` / `reopen` / `clear` with clear-in-progress fencing and
  file/output document removal plus count zeroing (real-file deletion deferred
  to the file-storage stage). Member audits now carry scope `project` and return
  their event IDs. New IdentityErrors codes 5103/5106/5108/5109/5110/5111/
  5112/5113. Non-manager external adds are tagless-only (tags ⇒ 103), and
  locked-tag assignment is team-creator-only, both matching Python. Note: an
  `invited` projection stand-in is persisted when a non-creator manager invites
  an existing team member — the invitation lifecycle stage replaces it with the
  real invitation adapter activation path.

Current verification: `dotnet test --no-restore --verbosity minimal` passes
150 tests against disposable local MongoDB, S3Mock and Mailpit, including 6
project-member tests (idempotent changes + duplicate 5106, update/remove/
restore with 5108 version fences, external bind auto-merge + explicit merge +
hard delete, manager/position-lock 103, complete/reopen/clear idempotency with
file/output removal and count zeroing, owner transfer with keep-admin +
idempotency + 5110), plus 7 registration tests (lowercase email, single-use
consumption, field-error collection without consumption, whitelist `1012`,
`ajt` member/admin base tags with missing-team skip, legacy code deletion and
concurrent same-name admission), concurrent
admission/edit/creation tests, partial-creation recovery and cross-team/hierarchy checks. The service builds with zero
warnings. Its Docker image has not been
rebuilt; the existing API container still runs the earlier foundation image.

The language data is embedded in the assembly. The offline
`tools/Import-LegacyLanguages.ps1 -LegacyDirectory ../shoreline-moeflow-backend`
regenerates it from public checked-in Python constants and the English PO catalog;
the running C# service requires neither Python nor the old repository.

## Next work

1. Invitation lifecycle (replace the `invited` projection stand-in with the real
   adapter activation path) and project application approve/reject; team
   application pages.
2. Team dissolution must be implemented together with complete project lifecycle
   cleanup; do not expose a partial delete that leaves project/storage data behind.
3. Files, targets, labels/translations/tips, complete snapshots and recovery,
   text parsing/OCR/import, exports, notifications, term banks and optional modules.
4. Storage transfers, quota/capacity reconciliation and their progress/cancellation
   endpoints; then full API/data compatibility and equivalent workload RAM testing.

## Remaining safety constraints

- Registration and other future name writers must share the name gates; existing
  duplicate legacy names are not silently rewritten or deleted.
- Profile/team writes and audit inserts remain separate documents, not a
  multi-document transaction guarantee. Gates are local to one business process;
  do not run a second writer on the same database.
- `Archive:EncryptionKey` is the optional legacy Fernet key. Otherwise the key is
  derived from `Auth:Secret`, which must match the old signing secret to read old
  archive ciphertext. No deployment configuration has been read during tests.
- Stored URL checks are not a DNS-rebinding defense by themselves: archive HTTP
  clients still need connection-time DNS checks and safe redirect handling.
- Durable jobs need business handlers beyond security email and team creation. Do not describe the
  current service as production-ready or infer full compatibility from its tests.
