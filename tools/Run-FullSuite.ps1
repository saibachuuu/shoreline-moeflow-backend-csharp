# Runs the full C# test suite against a disposable, exclusive
# MongoDB 4.4.1 + S3Mock + Mailpit stack. Ports are never the shared defaults.
#
# Usage:
#   pwsh -File tools/Run-FullSuite.ps1 -BasePort 33000
#
# The stack is stopped afterwards but its volumes (tmpfs) are disposable by
# design. Re-running with the same name recreates a clean stack.

param(
    [int]$BasePort = 33000,
    [string]$TrxDir = (Join-Path $env:TEMP "opencode\moeflow-r2-full-suite"),
    [string]$Filter = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$stack = "shoreline-r2-full"

$mongoPort = $BasePort
$s3Port = $BasePort + 1
$smtpPort = $BasePort + 2
$mailpitPort = $BasePort + 3
$apiPort = $BasePort + 4

$env:SHORELINE_TEST_STACK_NAME = $stack
$env:SHORELINE_TEST_MONGO_PORT = "$mongoPort"
$env:SHORELINE_TEST_S3_PORT = "$s3Port"
$env:SHORELINE_TEST_SMTP_PORT = "$smtpPort"
$env:SHORELINE_TEST_MAILPIT_PORT = "$mailpitPort"
$env:SHORELINE_TEST_API_PORT = "$apiPort"

Write-Host "Starting disposable stack '$stack' on ports $mongoPort-$apiPort ..."
wsl -e bash -lc "cd /mnt/c/Users/88888888/shoreline-moeflow/shoreline-moeflow-backend-csharp && SHORELINE_TEST_STACK_NAME='$stack' SHORELINE_TEST_MONGO_PORT='$mongoPort' SHORELINE_TEST_S3_PORT='$s3Port' SHORELINE_TEST_SMTP_PORT='$smtpPort' SHORELINE_TEST_MAILPIT_PORT='$mailpitPort' docker compose -f compose.test.yml up -d --wait mongo s3 mailpit"
if ($LASTEXITCODE -ne 0) { throw "stack failed to start" }

New-Item -ItemType Directory -Force -Path $TrxDir | Out-Null
$testArgs = @("test", "tests\Shoreline.Tests\Shoreline.Tests.csproj", "--nologo",
    "--logger", "trx;LogFileName=full.trx",
    "--results-directory", $TrxDir)
if ($Filter) { $testArgs += @("--filter", $Filter) }

try {
    Push-Location $repo
    & dotnet @testArgs
    $exit = $LASTEXITCODE
}
finally {
    Pop-Location
    Write-Host "Stopping disposable stack '$stack' ..."
    wsl -e bash -lc "docker compose -p '$stack' -f /mnt/c/Users/88888888/shoreline-moeflow/shoreline-moeflow-backend-csharp/compose.test.yml down -v" | Out-Null
}

Write-Host "TRX: $TrxDir\full.trx"
exit $exit
