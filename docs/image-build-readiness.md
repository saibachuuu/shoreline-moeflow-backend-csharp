# Image Build Readiness

Status: Dockerfile and smoke-test workflow prepared; end-to-end image validation is blocked by unreliable container registry/NuGet connectivity and unavailable MongoDB test dependency.

## Build inputs

Build from the repository root so both `Shoreline.Api` and its project reference, `Shoreline.Imaging`, are in context. The Dockerfile copies both project files and lock files before a locked restore, then copies both source trees before publishing the API. `.dockerignore` excludes local build output, tests, docs, tools, Git metadata, and environment files.

The base references are pinned to MCR multi-architecture manifest-list digests observed on 2026-11-10 via `https://mcr.microsoft.com/v2/.../manifests/...`:

- `mcr.microsoft.com/dotnet/sdk:10.0`: index `sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e`; linux/amd64 `sha256:32ac63f80a3dfe48cf91ec1de80b0f394e37e353ee367c7fa87991c2111d22f4`; linux/arm64 `sha256:c99fcb290d1df5a09f0e4e99904d47d6027f966f50be4004088f14f57cf8ebbc`
- `mcr.microsoft.com/dotnet/aspnet:10.0.8`: index `sha256:8c0b6857eab7b2aa57884c839bf4678414606bd7d17370f18a842ac5cf414711`; linux/amd64 `sha256:a7f704b4b87284c80df9b90ab0e06810a241785204dc191ec05f2b26b5847623`; linux/arm64 `sha256:8f64177b20c835c0b92514afdef8f50ada582c8bbd0970196c9ef1e8f3bf2235`

MCR returned `MANIFEST_UNKNOWN` for `dotnet/sdk:10.0.204`; the repository's `global.json` requests SDK `10.0.204` with `rollForward: latestFeature`. Registry index requests confirmed both pinned tags (HTTP 200); the tag's selected SDK version still needs confirmation by a successful pull/build.

## Validation

On 2026-11-10, `dotnet restore Shoreline.sln --locked-mode` succeeded and `dotnet build Shoreline.sln --no-restore -warnaserror` succeeded with 0 warnings and 0 errors using SDK 10.0.204. `dotnet test tests/Shoreline.Tests/Shoreline.Tests.csproj --no-restore` failed: 56 passed and 781 failed because the tests require MongoDB at `127.0.0.1:27128`, which was not listening. A focused `FullyQualifiedName~ThumbnailTests` run was also blocked by its shared MongoDB fixture (0 passed, 19 failed).

Docker Engine is Linux (`Docker 26.1.5`, Compose `2.26.1`). An initial `docker pull --platform linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0` failed with `Get "https://mcr.microsoft.com/v2/": EOF`, while another pull reported TLS handshake timeout. A subsequent `docker build --platform linux/amd64 -t shoreline-moeflow-readiness:local .` resolved both base manifests and reached locked restore with both project files, but NuGet downloads failed with `Received an unexpected EOF or 0 bytes from the transport stream.` The build timed out after 180 seconds while base layers were also downloading. No image was built or started; image ID, runtime UID, and health state are unavailable.

The API initializes MongoDB indexes before listening, including for `/health/live`. Supply a **dedicated disposable MongoDB database**, reachable from the container and not belonging to an existing stack; the test does not start or delete MongoDB. Build and smoke-test once the Engine can reach MCR and NuGet:

```powershell
./tools/Test-ReadinessImage.ps1 -MongoUri 'mongodb://test-host:27017/isolated_readiness'
```

The script builds a Linux image, starts it on loopback, verifies `/app/Shoreline.Imaging.dll`, confirms the container process UID is non-root, waits for Docker health status, and requests `/health/live`. The container is removed on exit; the image is intentionally retained. It does not read an env file or use Compose. The Mongo URI is passed to Docker as an environment argument and must not contain credentials when command-line exposure is unacceptable.

Record the command results, image ID, effective UID, health state, and MCR manifest digests here after running in an environment with registry access. Do not report an image validation until those runtime checks pass.
