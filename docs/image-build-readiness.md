# Image Build Readiness (WIP, not validated)

Status: blocked / not validated. No Dockerfile changes committed yet.

## Scope constraints
- Exclusive worktree: `.readiness-worktrees/moeflow-readiness-ops`
- Branch: `fix/readiness-image-20261110`
- Base: `2306053`
- Allowed files only: Dockerfile, .dockerignore, docs/image-build-readiness.md, tools/Test-ReadinessImage.ps1

## Known issue
- Dockerfile currently copies only `Shoreline.Api`, but `Shoreline.Api.csproj` references `Shoreline.Imaging`, so image build fails.
- SDK/aspnet tags are floating `10.0`; should pin to SDK `10.0.204` matching global.json and a concrete runtime digest.

## Not completed in this session
- Dockerfile not yet updated (lock restore for both projects, locked publish including Imaging).
- .dockerignore not yet updated.
- Docker build not executed.
- Tests not executed in this session.
- LSP navigation not recorded due to tool invocation issue (line parameter not accepted).

## Next steps
1. Update Dockerfile to COPY both projects + locks, restore/publish with Imaging.
2. Pin SDK/runtime versions with verified digests.
3. Run `dotnet build/test` and `docker build`.
4. Record SDK/runtime/digests, build command, test counts, UID, ImageID.
