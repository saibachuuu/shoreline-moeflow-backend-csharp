# Shoreline backend in C#

Work in progress: this repository cannot yet replace the Python backend.

The ASP.NET Core service runs HTTP requests and bounded, durable MongoDB jobs in
one process. MongoDB and object-storage clients are shared. Password hashing has
a separate concurrency limit. The current implementation includes legacy password
and token compatibility, captchas, user profiles and aliases, email verification,
password recovery, site settings, administrator user management, the public language
catalog with legacy locale negotiation and resumable new-database seeding, identity
permission calculation and tag-assignment validation, team setup/settings and
compact dashboard navigation, team membership and identity policy endpoints,
a leased job queue, and streaming S3 operations. Team mutations
enforce the base hierarchy, capacity and optimistic versions. Assignment validation
covers qualification and open modes, protected identities, external invitations
and custom policy tags; project mutations still need to be ported.

Legacy `/v1/types/*` configuration options and team/project system roles are
available too. Existing role collections remain untouched; empty-database role
initialization is resumable and preserves IDs already written by a partial seed.

Shared safety primitives include Python-compatible Fernet archive-key encryption,
allowlisted external API URL validation, and bounded name-write serialization to
prevent case-insensitive duplicate profile names in the single business process.

Project business endpoints, team/project lifecycle operations, translation workflows, files, snapshots, exports,
storage transfers and other business modules still need to be ported and tested.
No memory-saving claim applies to the complete backend yet.

At the current checkpoint, 130 tests pass. The completed stages and remaining
work are in [docs/refactor-checkpoint.md](docs/refactor-checkpoint.md).

## Local verification

Use .NET 10 and `docker compose -f compose.test.yml up -d` for isolated MongoDB,
S3Mock and Mailpit instances, then run `dotnet test`. The integration tests use
random databases on localhost and remove only those databases. The compose file
contains disposable test credentials, never production credentials. S3Mock does
not validate S3 signatures; these tests cannot prove Cloudflare R2 authorization.

The service requires `Mongo__Uri` (including a database), `Auth__Secret`, and S3
configuration when object storage is used. It does not load dotenv files. See
`tests/Shoreline.Tests/LocalServices.cs` for a complete disposable configuration.
Do not point tests at existing deployment databases or services.

Team membership/policy writes use a fixed-size in-process gate table, absolute
member recounts and Mongo version checks. Do not run a second backend writer
(including the Python backend) against the same database. Member lists are paged
and joined in MongoDB rather than materializing the complete team in the API.
Audits remain separate writes; they are not a multi-document transaction guarantee.

Team creation persists an uncancellable intent before writing the team, default
project set or creator membership. HTTP completion and worker recovery share
idempotent upserts; the same creator can retry an interrupted creation with the
same name/settings. Archive settings only return key tails, never full keys.
Global team discovery remains test-only, matching the original API.

At the first checkpoint, 33 tests pass on Windows with local WSL Docker services.
The SDK may roll forward within .NET 10 feature bands so the floating SDK image
works with global.json. `docker build -t shoreline-csharp:dev .` builds the service;
`docker compose -f compose.test.yml --profile api up -d api` runs it on localhost
port 25128. Its dedicated database is `shoreline_csharp_container_test`.
The container readiness check passes and its process list contains one `dotnet`
process. An initial idle sample was 52.77 MiB with only the currently ported
features; this is not a measurement of the completed replacement.
