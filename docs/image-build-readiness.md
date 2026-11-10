# Image Build Readiness

Status: Dockerfile and smoke-test workflow prepared; end-to-end image validation is blocked by unreliable container registry/NuGet connectivity and unavailable MongoDB test dependency.

## Build inputs

Build from the repository root so both `Shoreline.Api` and its project reference, `Shoreline.Imaging`, are in context. The Dockerfile copies both project files and lock files before a locked restore, then copies both source trees before publishing the API. `.dockerignore` excludes local build output, tests, docs, tools, Git metadata, and environment files.

The base references are pinned to MCR multi-architecture manifest-list digests observed on 2026-11-10 via `https://mcr.microsoft.com/v2/.../manifests/...`:

- `mcr.microsoft.com/dotnet/sdk:10.0`: index `sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e`; linux/amd64 `sha256:32ac63f80a3dfe48cf91ec1de80b0f394e37e353ee367c7fa87991c2111d22f4`; linux/arm64 `sha256:c99fcb290d1df5a09f0e4e99904d47d6027f966f50be4004088f14f57cf8ebbc`
- `mcr.microsoft.com/dotnet/aspnet:10.0.8`: index `sha256:8c0b6857eab7b2aa57884c839bf4678414606bd7d17370f18a842ac5cf414711`; linux/amd64 `sha256:a7f704b4b87284c80df9b90ab0e06810a241785204dc191ec05f2b26b5847623`; linux/arm64 `sha256:8f64177b20c835c0b92514afdef8f50ada582c8bbd0970196c9ef1e8f3bf2235`

MCR returned `MANIFEST_UNKNOWN` for `dotnet/sdk:10.0.204`; the repository's `global.json` requests SDK `10.0.204` with `rollForward: latestFeature`. On 2026-11-10, a Docker build using the pinned SDK index executed `dotnet --version` and printed `10.0.403`, which satisfies that roll-forward policy. The throwaway SDK probe image ID was `sha256:77c8481ff6d20e6c55c066359b91cbb746bbc9fd749a3c67af67cde795168451`, not an application image.

No apt packages are installed in the runtime image, avoiding an unpinned apt repository input. The image has no Docker `HEALTHCHECK`; deployment orchestration must independently probe `/health/live` for liveness and `/health/ready` for readiness. Liveness is not evidence of dependency readiness. NuGet lock files and pinned base manifests constrain inputs but do not by themselves guarantee byte-for-byte reproducible builds.

## Validation

On 2026-11-10, `dotnet restore Shoreline.sln --locked-mode` succeeded and `dotnet build Shoreline.sln --no-restore -warnaserror` succeeded with 0 warnings and 0 errors using SDK 10.0.204. `dotnet test tests/Shoreline.Tests/Shoreline.Tests.csproj --no-restore` failed: 56 passed and 781 failed because the tests require MongoDB at `127.0.0.1:27128`, which was not listening. A focused `FullyQualifiedName~ThumbnailTests` run was also blocked by its shared MongoDB fixture (0 passed, 19 failed).

Docker Engine is Linux (`Docker 26.1.5`, Compose `2.26.1`). An initial `docker pull --platform linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0` failed with `Get "https://mcr.microsoft.com/v2/": EOF`, while another pull reported TLS handshake timeout. A subsequent `docker build --platform linux/amd64 -t shoreline-moeflow-readiness:local .` resolved both base manifests and reached locked restore with both project files, but NuGet downloads failed with `Received an unexpected EOF or 0 bytes from the transport stream.` The build timed out after 180 seconds while base layers were also downloading. Review retry `docker build --progress=plain --platform linux/amd64 -t shoreline-moeflow-readiness:review-20261110 .` completed runtime base layer extraction, but NuGet repeatedly returned the same EOF for `Magick.NET-Q8-AnyCPU.15.1.0` and other packages; it timed out after 300 seconds. No application image was built or started; application image ID, runtime UID, and endpoint status remain unavailable.

The API initializes MongoDB indexes before listening, including for `/health/live`. The script does not accept a Mongo target: it creates an internal Docker network and a new disposable Mongo container using a per-run random name, alias, label and database name, checks those identities before starting the API, and stores Mongo data on tmpfs. It publishes no Mongo port. It removes only resource IDs created by that invocation; the image is retained. On Windows it calls Docker in WSL (default distribution `Debian`). Run once Docker can access MCR and NuGet:

```powershell
./tools/Test-ReadinessImage.ps1
```

The script builds a Linux image, starts it on loopback port 25221, verifies `/app/Shoreline.Imaging.dll`, checks its non-root UID, then separately requests `/health/live` and `/health/ready`. It does not read an env file or use Compose. The smoke test itself is not yet end-to-end verified, because image build remains blocked. An independent WSL check successfully created a disposable network and Mongo container, verified their identities, and pinged Mongo; an initial ping attempt exposed WSL argument quoting and was fixed before the successful retry. Both resources from each attempt were removed. Its Mongo test image is currently the locally resolved `mongo:8.0` tag and is not a reproducibility guarantee.

Record the command results, image ID, effective UID, health state, and MCR manifest digests here after running in an environment with registry access. Do not report an image validation until those runtime checks pass.
