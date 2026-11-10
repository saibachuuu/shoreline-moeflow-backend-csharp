# Image Build Readiness

Status: the repository Dockerfile pins both bases by version tag plus digest and builds a Linux image from a cold cache; `tools/Test-ReadinessImage.ps1` passed 8/8 runtime checks against that image with a disposable Local storage root and a disposable Mongo. One earlier smoke-script revision failed for real and its evidence is retained.

## Build inputs

Build from the repository root so both `Shoreline.Api` and its project reference, `Shoreline.Imaging`, are in context. The Dockerfile copies both project files and lock files before a locked restore, then copies both source trees before publishing the API. `.dockerignore` excludes local build output, tests, docs, tools, Git metadata, and environment files.

Base references are pinned to MCR multi-architecture manifest-list digests verified on 2026-11-10 via `https://mcr.microsoft.com/v2/.../manifests/...`:

- `mcr.microsoft.com/dotnet/sdk:10.0.403`: index `sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e`; linux/amd64 `sha256:32ac63f80a3dfe48cf91ec1de80b0f394e37e353ee367c7fa87991c2111d22f4`; linux/arm64 `sha256:c99fcb290d1df5a09f0e4e99904d47d6027f966f50be4004088f14f57cf8ebbc`
- `mcr.microsoft.com/dotnet/aspnet:10.0.8`: index `sha256:8c0b6857eab7b2aa57884c839bf4678414606bd7d17370f18a842ac5cf414711`; linux/amd64 `sha256:a7f704b4b87284c80df9b90ab0e06810a241785204dc191ec05f2b26b5847623`; linux/arm64 `sha256:8f64177b20c835c0b92514afdef8f50ada582c8bbd0970196c9ef1e8f3bf2235`

The SDK reference uses the version-specific tag `10.0.403`, not the floating `10.0` tag. On 2026-11-10 MCR reported the same index digest and the same linux/amd64 digest for both tags, and running the pinned image printed `dotnet --version` = `10.0.403`; `global.json` requests `10.0.204` with `rollForward: latestFeature`, which permits this.

The runtime stage installs no apt packages, so no unpinned apt repository input exists. The image deliberately has no Docker `HEALTHCHECK`; orchestration must probe `/health/live` for liveness and `/health/ready` for readiness, since the latter depends on Mongo.

## Validation

Host: Windows .NET SDK `10.0.204`. `dotnet build Shoreline.sln --no-restore -warnaserror` succeeds with 0 warnings and 0 errors.

Docker: WSL `Debian`, Engine and Compose `26.1.5`. On 2026-11-10 two independent cold, no-cache builds of the repository Dockerfile succeeded with the version-specific SDK reference:

```
docker build --no-cache --progress=plain --platform linux/amd64 -t shoreline-moeflow-readiness:tagged-20261110 .
docker build --no-cache --platform linux/amd64 -t shoreline-moeflow-readiness:tagged-20261110-repeat .
```

Run 1: locked restore of both projects (`Shoreline.Imaging` 16.51 s, `Shoreline.Api` 1.95 min), publish emitted `Shoreline.Imaging.dll` then `Shoreline.Api.dll`, image ID `sha256:b4ecaccdc482bc4ac35da2e9208589416e8279f0093126132fc6188848068e58`.
Run 2: locked restore (`Shoreline.Imaging` 24.25 s, `Shoreline.Api` 32.74 s), image ID `sha256:2adcc55f94ec0429d317618736185b726abad7c31f1f61452c841015f10eeec5`.
Both images have configured user `1654` and no healthcheck, and their image config (env, entrypoint, user) is identical. Their final filesystem layer differs, so their ImageIDs differ. This was before the timestamp normalization below. An earlier build of the pre-change Dockerfile also succeeded cold (`sha256:f264fe4b4265f5c2671a4284d523fb0ec0e394674007947526fffda5f0dd6a90`). Earlier in the session builds failed repeatedly with NuGet `Received an unexpected EOF or 0 bytes from the transport stream.`; that network failure later cleared and is not a Dockerfile defect.

### Reproducibility with explicit build epoch

The two earlier images had identical SHA-256 hashes for every `/app` file but different modification times on generated output and directories. The Dockerfile now normalizes `/out` timestamps to Unix epoch zero after publish. The supported reproducible build invocation must also set BuildKit's `SOURCE_DATE_EPOCH=0`, including the image configuration timestamps; a plain `docker build` without this argument is not claimed to be reproducible. The smoke script supplies it when it builds.

On the same WSL Docker 26.1.5 builder and identical source tree, two independent runs of:

```
docker build --no-cache --build-arg SOURCE_DATE_EPOCH=0 --platform linux/amd64 -t shoreline-moeflow-readiness:repro-a-20261110 .
docker build --no-cache --build-arg SOURCE_DATE_EPOCH=0 --platform linux/amd64 -t shoreline-moeflow-readiness:repro-b-20261110 .
```

both produced ImageID `sha256:61b67b429da83d78e9d79492a65aeec65d531bfe7b17e9a36300b55fee4fead9` with identical layer digests and creation time `1970-01-01T00:00:00Z`. `tools/Test-ReadinessImage.ps1 -SkipBuild -Image shoreline-moeflow-readiness:repro-a-20261110` passed non-root UID 1654, writable disposable local storage, native Imaging processing, `/health/live` 200 and `/health/ready` 200; evidence is `C:\Users\88888888\AppData\Local\Temp\shoreline-readiness-7b38a1bcf67842b4af3c9aa156a45cd8.log`. This only proves these two builds on this machine, architecture, builder and source tree. Other platforms, builders, source metadata and the final release image still require separate verification. Normalized published file mtimes are intentional and must be considered if future runtime code depends on them.

`tools/Test-ReadinessImage.ps1 -SkipBuild -Image shoreline-moeflow-readiness:tagged-20261110` passed:

| Check | Result |
| --- | --- |
| Image ID | `sha256:b4ecaccdc482bc4ac35da2e9208589416e8279f0093126132fc6188848068e58` |
| Configured user / process UID | `1654` / `1654` (`app:app`, non-root) |
| `Shoreline.Imaging.dll` in `/app` | present |
| Local storage root | `test -w`, create, delete succeeded as UID 1654 |
| Native Magick | `ldd` on `Magick.Native-Q8-x64.dll.so` resolved every shared object |
| Native decode | probe printed `MAGICK_OK format=Xc bytes=310; IMAGING_OK cover=180x140 resample=2x2` |
| `GET /health/live` | HTTP 200 |
| `GET /health/ready` | HTTP 200 |

The same checks previously passed against the cold-built pre-change image `sha256:f264fe4b4265f5c2671a4284d523fb0ec0e394674007947526fffda5f0dd6a90` and against the offline-derived review image `sha256:2608afbcb8f7efbb0f7c89097ef5fd18b2079dcc6db88daee4b84d52969a2fc9`. The latter was prepared outside this Dockerfile and is supporting evidence only, not acceptance of the original build.

### Recorded failure

Revision `ca374f8` of the smoke script set Mongo but no `Storage:*` values. A real run against that image produced:

```
Unhandled exception. System.InvalidOperationException: Storage:Bucket is required for S3 storage.
   at Shoreline.Api.Storage.ObjectStorage.Required(IConfiguration configuration, String key) in /src/src/Shoreline.Api/Storage/ObjectStorage.cs:line 193
   at Shoreline.Api.Storage.ObjectStorage..ctor(IConfiguration configuration, ILogger`1 logger) in /src/src/Shoreline.Api/Storage/ObjectStorage.cs:line 55
```

The container exited with code 139 before listening, so liveness was never reachable. The current script reproduces this on demand with `-SimulateStorageFailure` and fails with `API bootstrap failed (exited): Unhandled exception. System.InvalidOperationException: Storage:Bucket is required for S3 storage.` instead of reporting a pass.

## Smoke test design

- Only owned resources: a per-run random network, Mongo container, API container, database name and label. Each is identity-checked (label, container name, attached network ID, alias) before use, and only those container/network IDs are removed. Mongo runs on tmpfs with no published port; the API publishes only `127.0.0.1:25221`.
- No external storage or credentials: `Storage__Type=Local`, a tmpfs root mounted `uid=1654,gid=1654,mode=0700`, and a synthetic `Storage__Domain=http://127.0.0.1:25221/readiness-objects`. `Auth__Secret` is a per-run 64-character hex value and `Auth__AdminEmail` is a per-run `@example.invalid` address.
- Evidence is written to `%TEMP%\shoreline-readiness-<token>.log` with container state and the last 60 log lines, so startup failures stay inspectable after cleanup.
- Startup is checked before any HTTP probe; if the container exits, the script reports the first exception line and the evidence path.
- The Imaging probe binary is built in a temporary directory and mounted read-only; it is removed afterwards. The only published port is the exclusive `25221` for the API.

Known limitations: the test requires the host `dotnet` SDK and cached NuGet packages to compile the Imaging probe, the Mongo image is pinned to `mongo:8.0@sha256:b9924c330a8bbc5800fe9c69f04a84e221eca7b28fd049a0354147d9908fa142` but other packages are not image-level pinned, and no orchestration healthcheck is defined in the image.
