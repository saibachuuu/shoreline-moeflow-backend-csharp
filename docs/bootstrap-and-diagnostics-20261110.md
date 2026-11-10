# Fresh-install bootstrap and legacy diagnostics

Date: 2026-11-10. Base: `main` at `2650a74` plus the working-tree changes below.
Scope: close the two code-level gaps recorded in
[`readiness-audit-20261110.md`](readiness-audit-20261110.md) — the missing
`init_db` equivalent and the four missing root diagnostic routes.

Status: implemented, covered by focused tests, and exercised by the full C#
suite. Test-server deployment and a fresh-database run are still required
before the production checklist can move these rows to "已验证".

## Bootstrap (`init_db` port)

`src/Shoreline.Api/Features/BootstrapService.cs` ports
`app/factory.py:init_db` and `create_or_override_default_admin` /
`create_default_team`:

| Step | Python source | C# behaviour |
| --- | --- | --- |
| System roles + public types | `TeamRole.init_system_roles`, `ProjectRole.init_system_roles`, `Language.init_system_languages` | `LegacyTypes.InitializeAsync` + `LanguageCatalog.InitializeAsync` run first in `Program.cs`; `BootstrapService` calls `LegacyTypes.InitializeAsync` again (idempotent) |
| Single `site_setting` document | `SiteSetting.init_site_setting` | `SiteSettings.EnsureAsync` upserts `{n:"site", ew:true, we:[], oacg:true, ajt:[], h:"", hc:"", st:"", hw:"", hi:""}`; duplicate-key from a concurrent instance is re-read |
| Configured administrator | `create_or_override_default_admin` | lower-cases `Auth:AdminEmail`; grants `admin:true` to an existing account; otherwise requires `Auth:AdminInitialPassword`, hashes with `PasswordService`, inserts the Python field set, and re-checks under `IdentityNameGate("user","Admin")` |
| Default team | `create_default_team` | only when `team` is empty: inserts `Default Team` (name intentionally contains a space, matching Python's bypass of `verify_new_name`), creates the default `project_set`, joins the administrator as `creator`, and sets `site_setting.ajt` to that team |

Every step is idempotent. The service is registered as scoped and runs once at
startup after the HTTP pipeline is built.

### Activation

`Program.cs` reads `Bootstrap:Enabled` **after** `builder.Build()` so
`WebApplicationFactory` configuration is visible, and defaults it to:

- `false` in the `Testing` environment;
- `true` everywhere else.

The `Testing` default is deliberate: several existing tests insert exactly one
`site_setting` document and assert `count == 1`, and the runtime differential
tests replace startup-seeded users/team with fixture rows. Enabling bootstrap
unconditionally would change those fixtures. A deployment therefore starts with
bootstrap on, matching Python `manage.py migrate` / container init.

Fresh deployment requirements, identical in effect to Python:

- `Auth__AdminEmail` must be set (otherwise startup throws).
- `Auth__AdminInitialPassword` must be set when no account with that e-mail
  exists.

An existing database with an administrator only needs `Auth__AdminEmail`; the
password is not rewritten.

### Tests

`tests/Shoreline.Tests/BootstrapTests.cs` uses a random database and
`WebApplicationFactory<Program>` with `Testing` + `Bootstrap:Enabled=true`:

1. fresh startup creates the site setting, administrator, default team,
   creator relation, default project set and auto-join list;
2. restart is idempotent (one team/user/site_setting, same team id);
3. an existing non-admin account is promoted without touching its password, and
   no initial password is required;
4. an existing team suppresses a second default team.

## Legacy diagnostic routes

`LegacyDiagnosticsEndpoints.MapLegacyDiagnostics` maps the four routes that were
previously the only static route gap
(`app/apis/urls.py:121-134`, implementation `app/apis/index.py`):

| Route | Python behaviour | C# behaviour |
| --- | --- | --- |
| `GET /error` | `int("1.2")` → 500 | parses `"1.2"` → 500 |
| `GET /warning` | `raise Warning("test warning")` → 500 | throws `WarningException` → 500 |
| `GET /urls` | HTML table of endpoint, methods, URL | HTML table from `EndpointDataSource` |
| `GET /docs/<path>` | `send_from_directory("../docs", path)` | serves from `LegacyDocs:Root` when configured, traversal-guarded, 404 otherwise |

These are diagnostics, not business endpoints; `/urls` endpoint names and
`/docs` roots differ by framework/configuration. Missing documentation files
return 404 in both implementations.

`tests/Shoreline.Tests/LegacyDiagnosticsTests` asserts `/urls` contains `/ping`,
`/error` and `/warning` return 500, and a missing document returns 404.

## Verification evidence

| Check | Command | Result |
| --- | --- | --- |
| Build | `dotnet build Shoreline.sln --nologo` | 0 warnings / 0 errors |
| Focused | `dotnet test --filter "FullyQualifiedName~BootstrapTests\|FullyQualifiedName~LegacyDiagnosticsTests"` | 5/5 passed, 35 s |
| Full suite | `dotnet test tests/Shoreline.Tests/Shoreline.Tests.csproj --no-build` with the five `SHORELINE_TEST_*` ports | **888/888 passed, 0 failed, 0 skipped, 10 m 01 s** |

The full-suite run includes `LegacyRuntimeDifferentialTests`, so the eight-case
Python/C# business differential still reports zero business differences after
the bootstrap wiring.

Test services for the run were disposable loopback containers:
`sl-iso-mongo` (27241), `sl-iso-s3` (29241), `sl-iso-mailpit` (21241/28241);
API port `25241`.

## Parity follow-ups in the same change

- `StoragePool:WorkerEnabled` now defaults to `true` outside `Testing` (and
  `false` in tests), matching Python's always-registered beat entry. The
  database switches `se` (scan, default false) and `re` (run, default true)
  remain the runtime gate.
- `MongoStore` now creates the third Python identity index
  `team_member_alias_search_v1` on `(team, status, asrch)`, matching migration
  `m0006_search_projections.py`; the existing conflict check still refuses to
  replace an unequal index.
- `Storage:KeyPrefix` remains an explicit deployment setting
  (`Storage__KeyPrefix=storage/` for Python data); the code default was not
  changed because the in-process storage-pool engine and tests use empty
  prefixes deliberately.

## Remaining work

- Build the release image from this tree and deploy it to the test server.
- Exercise a genuinely empty database (administrator creation, default team,
  login) through HTTP, not only through `WebApplicationFactory`.
- Keep `Storage__KeyPrefix=storage/` in deployments that share Python data.
- The route is present, but `/docs` content is only served when
  `LegacyDocs:Root` points at a directory included in the deployment.
