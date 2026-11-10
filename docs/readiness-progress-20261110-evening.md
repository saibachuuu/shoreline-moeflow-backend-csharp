# Production readiness progress — 2026-11-10 evening

**Not a release approval. The C# backend must not replace production Python yet.**
This record supplements the earlier handoff; snapshot results do not imply full-suite,
load, migration, or production acceptance. No README production-status wording was changed.

## Merged on main

- `2aaaa85` integrates two reviewed `team_member` indexes and a read-only duplicate
  diagnostic. `c9775d4` clarifies the third, conflicting index's synthetic test
  states. Python migration `m0006` and the model disagree about the search-index key;
  no automatic destructive resolution or migration acceptance is claimed.
- `4fd22bd` integrates user notification mail's channel-specific enablement gate.
  It does not implement log email, fix security-code mail or prove real SMTP/TLS
  delivery. On the merged main, `NotificationPublishTests|ProofreadFeedbackTests`
  passed **75/75**, 0 skipped, with isolated Mongo/S3Mock/Mailpit. TRX:
  `C:\Users\88888888\AppData\Local\Temp\opencode\mailgate-main-20261110.trx`,
  SHA-256 `E2871C02775C90F1DBC01C5D25BA467A898C4B2355EC438D95F7213D4354E501`.
- `ba56379` integrates ordinary project creation's LabelPlus parsing and existing
  import pipeline. The HTTP tests cover valid import, invalid input, permission and
  Python error precedence. On the merged main, `ProjectCreateLabelPlusHttpTests|
  ProjectTests|ProjectImportTests|TeamDissolutionTests` passed **61/61**, 0 skipped.
  TRX: `C:\Users\88888888\AppData\Local\Temp\opencode\labelplus-main-20261110.trx`,
  SHA-256 `12DF3A8074451506265F2A78F02EFDEC3BD92656E432DA3F2B7344506971F53C`.
- `22f63cf` integrates the image branch. Both referenced projects and lock files are
  copied, and SDK `10.0.403` and ASP.NET `10.0.8` are pinned by digest. Two clean
  Linux/amd64 builds of the branch Dockerfile succeeded; the tested branch image
  `sha256:b4ecaccdc482bc4ac35da2e9208589416e8279f0093126132fc6188848068e58`
  ran as UID 1654, loaded and exercised native Imaging, wrote disposable local
  storage, and returned 200 from live and ready endpoints. A further cold build/smoke
  against main content at `bc9557f` (no Dockerfile changes since) produced the
  identical ImageID twice: `sha256:7a1936a2db68f84572a0ff41ce701f162b9f4873ef9361234faed19ebedd662e`.
  Runtime UID was 1654, Imaging processed a sample, Local storage was writable, and
  live/ready both returned 200. This proves reproducibility only for the same source,
  builder and amd64 platform. The smoke removed only its own resources and did not
  alter the running user stack. No Docker HEALTHCHECK is included. See
  `docs/image-build-readiness.md` for commands and boundaries.

The main `dotnet build Shoreline.sln --nologo` after these merges reported **0
warnings / 0 errors**. Focused tests above are separate executions, not a full suite.
Their local disposable Compose stack was stopped, not deleted. Mongo/Mailpit exited
0; S3Mock exited 143 on stop; all reported `OOMKilled=false`.

## Unmerged Bootstrap integration

`fix/readiness-bootstrap-wiring-20261110` contains `fix/readiness-bootstrap-20261110`
and an explicit `Program.cs` startup connection. On a fresh isolated Mongo, the
actual wired `WebApplicationFactory<Program>` startup, login, creator membership,
role, project set and `ajt` test together with recovery cases passed **47/47**.
TRX: `C:\Users\88888888\AppData\Local\Temp\opencode\bootstrap-wired2-20261110.trx`,
SHA-256 `5A2A4978CC7B57E47B5F4587152C19AB4F3A0F60427189F296C950DB2062B4C0`.
The branch also tested an actual dropped Mongo TCP CAS reply; this is limited to
that operation, not an exactly-once or cross-document transaction guarantee.

**Do not merge yet:** the existing `LocalServices` fixture lacks the new initial
administrator password; the wired branch's `UserTests` failed **0/3** at startup
with `Auth:AdminInitialPassword is required before creating the configured administrator`.
Fixture adaptation must retain real startup and all assertions, and account for
existing tests that insert the same admin email. Separate fixture work is in progress.
The initial wired HTTP test run also failed because its test database name exceeded
Mongo's 63-character limit; it was shortened before the 47/47 rerun.

## Differential and remaining blockers

- An unmerged true Flask/C# differential branch has 16 requests including successful
  `partner_search` writes/reads and a real Python token. Its latest rerun has 17
  business differences, 3 expected-framework differences and 39 wire-format
  differences; the test correctly **fails**. It compares same raw BSON seeds and
  database before/after digests. Profile writes add BSON `csharp_profile_revision`,
  and CORS/HTTP headers differ.
  The profile revision protects C# optimistic concurrency and must not simply be
  removed to match a digest. An isolated real Flask probe confirmed Python
  MongoEngine raises `FieldDoesNotExist` when loading a user with
  `csharp_profile_revision`. CORS synthetic defaults were corrected against Python
  config and the real difference rerun. The corrected actual Flask responses include
  expose headers without Origin and wildcard CORS with Origin, while C# uses an
  explicit localhost default; policy differences require an owner decision. The
  differential also finds preflight 200 vs 204, 405 body/Allow differences, and
  three requests without Flask's no-Origin expose header. The two set-derived method
  headers are compared as token sets because Python's set iteration order varies;
  missing/extra methods still fail. No business mismatch is classified as accepted.
- Security-code delivery and rollback interoperability, the third legacy index,
  historical migrations, real R2, retry/lease/crash coverage for all jobs, optional
  routes, MIT consumers, SSRF/trusted proxies and resource/security controls remain
  unaccepted. No product exception is assumed.
- C# full, safely isolated Python full, complete real differential, test-server
  deployment, migration/backup/rollback, load and soak remain unverified. An earlier
  full-test attempt suffered Mongo OOM; it is not a pass. Do not read production
  credentials or treat S3Mock as evidence of real R2 authorization.
