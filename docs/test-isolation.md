# Test isolation

Disposable local test services use `tests/Shoreline.Tests/TestEnvironment.cs`. It reads only these environment variables:

- `SHORELINE_TEST_MONGO_PORT`
- `SHORELINE_TEST_S3_PORT`
- `SHORELINE_TEST_SMTP_PORT`
- `SHORELINE_TEST_MAILPIT_PORT`
- `SHORELINE_TEST_API_PORT`

The host is fixed to `127.0.0.1`. Arbitrary MongoDB URIs are not supported, so the helper cannot be redirected to a remote or production database. Ports must be integers in `1..65535`; unset variables use `27128`, `29128`, `21128`, `28128`, and `25128`.

## Compose

Use Docker Compose v2 with an explicit non-`.env` file so Compose does not automatically read a repository `.env`:

```sh
docker compose -f compose.test.yml --env-file /path/to/shoreline-isolation.ports up -d mongo s3 mailpit
```

Example disposable env file:

```text
SHORELINE_TEST_STACK_NAME=shoreline-readiness-isolation-20261110
SHORELINE_TEST_MONGO_PORT=27231
SHORELINE_TEST_S3_PORT=29231
SHORELINE_TEST_SMTP_PORT=21231
SHORELINE_TEST_MAILPIT_PORT=28231
SHORELINE_TEST_API_PORT=25231
```

Export the same five port variables in the shell running `dotnet test`; Compose's `--env-file` does not configure the host test process. Check port occupancy before starting the uniquely named stack. A fixed loopback host prevents arbitrary remote hosts, but ports may still reach an SSH tunnel or another local service: ensure these ports belong to your disposable containers.

Only this project's `mongo`, `s3`, and `mailpit` services are started. No API image is built and no existing services or volumes are changed. Use `docker compose -f compose.test.yml --env-file /path/to/shoreline-isolation.ports stop` to stop this stack without deleting its resources. Removing containers backed by tmpfs destroys their disposable data; do that only after checking ownership and preserving required evidence.
