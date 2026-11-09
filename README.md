# Shoreline backend in C#

Work in progress: this repository cannot yet replace the Python backend.

The ASP.NET Core service runs HTTP requests and bounded, durable MongoDB jobs in
one process. MongoDB and object-storage clients are shared. Password hashing has
a separate concurrency limit. The current implementation includes legacy password
and token compatibility, captchas, user profiles and aliases, email verification,
password recovery, a leased job queue, and streaming S3 operations.

Project/team permissions, translation workflows, files, snapshots, exports,
storage transfers and other business modules still need to be ported and tested.
No memory-saving claim applies to the complete backend yet.

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

At the first checkpoint, 33 tests pass on Windows with local WSL Docker services.
The first Docker build exposed an SDK feature-band mismatch between global.json
and the floating SDK image; container validation remains pending.
