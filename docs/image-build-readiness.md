# Image Build Readiness

Status: the repository Dockerfile now builds a Linux image from a cold cache, and `tools/Test-ReadinessImage.ps1` passes against that image with a disposable Local storage root and a disposable Mongo. One earlier smoke-script revision failed for real and its evidence is retained.

## Build inputs

Build from the repository root so both `Shoreline.Api` and its project reference, `Shoreline.Imaging`, are in context. The Dockerfile copies both project files and lock files before a locked restore, then copies both source trees before publishing the API. `.dockerignore` excludes local build output, tests, docs, tools, Git metadata, and environment files.

Base references are pinned to MCR multi-architecture manifest-list digests verified on 2026-11-10 via `https://mcr.microsoft.com/v2/.../manifests/...`:

- `mcr.microsoft.com/dotnet/sdk:10.0`: index `sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e`; linux/amd64 `sha256:32ac63f80a3dfe48cf91ec1de80b0f394e37e353ee367c7fa87991c2111d22f4`; linux/arm64 `sha256:c99fcb290d1df5a09f0e4e99904d47d6027f966f50be4004088f14f57cf8ebbc`
- `mcr.microsoft.com/dotnet/aspnet:10.0.8`: index `sha256:8c0b6857eab7b2aa57884c839bf4678414606bd7d17370f18a842ac5cf414711`; linux/amd64 `sha256:a7f704b4b87284c80df9b90ab0e06810a241785204dc191ec05f2b26b5847623`; linux/arm64 `sha256:8f64177b20c835c0b92514afdef8f50ada582c8bbd0970196c9ef1e8f3bf2235`

MCR returns `MANIFEST_UNKNOWN` for `dotnet/sdk:10.0.204`; `global.json` requests `10.0.204` with `rollForward: latestFeature`. The pinned SDK image reports `dotnet --version` = `10.0.403`, which satisfies that policy.

The runtime stage installs no apt packages, so no unpinned apt repository input exists. The image deliberately has no Docker `HEALTHCHECK`; orchestration must probe `/health/live` for liveness and `/health/ready` for readiness, since the latter depends on Mongo.

## Validation

Host: Windows .NET SDK `10.0.204`. `dotnet build Shoreline.sln --no-restore -warnaserror` succeeds with 0 warnings and 0 errors.

Docker: WSL `Debian`, Engine and Compose `26.1.5`. On 2026-11-10 a cold, no-cache build of the repository Dockerfile succeeded:

```
docker build --no-cache --progress=plain --platform linux/amd64 -t shoreline-moeflow-readiness:clean-20261110 .
```

Locked restore completed for both projects (`Shoreline.Imaging` 20.53 s, `Shoreline.Api` 24.29 s), publish emitted `Shoreline.Imaging.dll` first and then `Shoreline.Api.dll`, and the image ID was `sha256:f264fe4b4265f5c2671a4284d523fb0ec0e394674007947526fffda5f0dd6a90` with configured user `1654` and no healthcheck. Earlier in the session the same build failed repeatedly with NuGet `Received an unexpected EOF or 0 bytes from the transport stream.`; that network failure later cleared, and it is not a Dockerfile defect.

`tools/Test-ReadinessImage.ps1 -SkipBuild -Image shoreline-moeflow-readiness:clean-20261110` passed:

| Check | Result |
| --- | --- |
| Image ID | `sha256:f264fe4b4265f5c2671a4284d523fb0ec0e394674007947526fffda5f0dd6a90` |
| Configured user / process UID | `1654` / `1654` (`app:app`, non-root) |
| `Shoreline.Imaging.dll` in `/app` | present |
| Local storage root | `test -w`, create, delete succeeded as UID 1654 |
| Native Magick | `ldd` on `Magick.Native-Q8-x64.dll.so` resolved every shared object |
| Native decode | probe printed `MAGICK_OK format=Xc bytes=310; IMAGING_OK cover=180x140 resample=2x2` |
| `GET /health/live` | HTTP 200 |
| `GET /health/ready` | HTTP 200 |

The same checks passed against the offline-derived review image `sha256:2608afbcb8f7efbb0f7c89097ef5fd18b2079dcc6db88daee4b84d52969a2fc9`, which was prepared outside this Dockerfile and therefore is supporting evidence, not acceptance of the original build.

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
