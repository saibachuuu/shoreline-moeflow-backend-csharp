<#
.SYNOPSIS
  Read-only pre-deploy self-check for the C# backend. Never touches remote hosts.
.DESCRIPTION
  Verifies, in order: dotnet build, `docker compose config` on the prod
  example file, the /ping + /health/live + /health/ready endpoints of a
  loopback API instance, and a migration-check placeholder (owned by another
  agent). Failing build/config checks exit non-zero; unreachable health
  endpoints and the migration placeholder are warnings only.
  All network targets must be loopback; anything else is refused.
.EXAMPLE
  pwsh scripts/verify-deploy.ps1
  pwsh scripts/verify-deploy.ps1 -ApiBaseUrl http://127.0.0.1:32034 -ComposeFile compose.prod.example.yml
#>
[CmdletBinding()]
param(
  [string]$ApiBaseUrl = "http://127.0.0.1:32034",
  [string]$ComposeFile = "compose.prod.example.yml"
)

$ErrorActionPreference = "Stop"
$warnings = @()

function Assert-Loopback([string]$url) {
  $uri = [Uri]$url
  $target = $uri.DnsSafeHost
  if ($target -notin @("localhost", "127.0.0.1", "::1")) {
    throw "Refusing non-loopback target: $url (verify-deploy only checks local instances)"
  }
}

function Step([string]$name, [scriptblock]$body, [switch]$WarnOnly) {
  Write-Host "==> $name"
  try {
    & $body | Out-Null
    Write-Host "    OK"
  } catch {
    if ($WarnOnly) {
      $msg = "WARN: ${name}: $($_.Exception.Message)"
      Write-Host "    $msg"
      $script:warnings += $msg
      return
    }
    Write-Host "    FAIL: $($_.Exception.Message)"
    throw
  }
}

Assert-Loopback $ApiBaseUrl
$repoRoot = Split-Path -Parent $PSScriptRoot
$composePath = if ([IO.Path]::IsPathRooted($ComposeFile)) { $ComposeFile } else { Join-Path $repoRoot $ComposeFile }

Step "dotnet build" {
  Push-Location $repoRoot
  try {
    dotnet build Shoreline.sln --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet build exited with $LASTEXITCODE" }
  } finally { Pop-Location }
}

Step "compose config ($ComposeFile)" {
  if (-not (Test-Path $composePath)) { throw "compose file not found: $composePath" }
  # docker runs inside WSL here, so translate the Windows path first and
  # forward the compose variables (WSL does not inherit Windows env).
  $wslPath = (wsl -e wslpath -a "$composePath" 2>&1 | Out-String).Trim()
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslPath)) { throw "wslpath failed for: $composePath" }
  $env:WSLENV = "SHORELINE_IMAGE:SHORELINE_PROD_STACK_NAME:SHORELINE_API_PORT:MONGO_URI:AUTH_SECRET:AUTH_ADMIN_EMAIL:AUTH_ADMIN_INITIAL_PASSWORD:BOOTSTRAP_ENABLED:STORAGE_ENDPOINT:STORAGE_BUCKET:STORAGE_ACCESS_KEY:STORAGE_SECRET_KEY:STORAGE_FORCE_PATH_STYLE:MAIL_ENABLED:MAIL_HOST:MAIL_PORT:MAIL_START_TLS:MAIL_FROM:MAIL_USERNAME:MAIL_PASSWORD:CORS_ORIGIN_0:TRUSTED_PROXY_0:JOBS_CONCURRENCY"
  $out = wsl -e bash -lc "docker compose -f '$wslPath' config" 2>&1
  if ($LASTEXITCODE -ne 0) { throw "docker compose config failed: $out" }
}

Step "health endpoints ($ApiBaseUrl)" {
  foreach ($path in @("/ping", "/health/live", "/health/ready")) {
    $res = Invoke-WebRequest -Uri ($ApiBaseUrl.TrimEnd("/") + $path) -TimeoutSec 10 -UseBasicParsing
    if ($res.StatusCode -ge 400) { throw "$path returned $($res.StatusCode)" }
    Write-Host "    $path -> $($res.StatusCode)"
  }
} -WarnOnly

Step "migration check (placeholder)" {
  $msg = "Infrastructure/Migrations is owned by another agent; no migration CLI exists yet. " +
    "Safe-deploy rule: boot the new image once against a copy of production data and confirm " +
    "startup index initialization (MongoStore.InitializeAsync + module Initialize*Async) succeeds."
  Write-Host "    PLACEHOLDER: $msg"
  $script:warnings += "migration check is a placeholder"
} -WarnOnly

if ($warnings.Count -gt 0) {
  Write-Host "`nCompleted with $($warnings.Count) warning(s):"
  $warnings | ForEach-Object { Write-Host "  - $_" }
} else {
  Write-Host "`nAll checks passed."
}
