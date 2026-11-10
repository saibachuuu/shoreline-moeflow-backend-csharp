[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MongoUri,
    [string] $Image = 'shoreline-moeflow-readiness:local',
    [ValidateRange(1024, 65535)]
    [int] $Port = 27221
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$container = "shoreline-readiness-$([guid]::NewGuid().ToString('N'))"
$started = $false

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

try {
    Invoke-Docker build --platform linux/amd64 --tag $Image --file (Join-Path $repoRoot 'Dockerfile') $repoRoot

    $imageId = (Invoke-Docker image inspect --format '{{.Id}}' $Image | Out-String).Trim()
    $configuredUser = (Invoke-Docker image inspect --format '{{.Config.User}}' $Image | Out-String).Trim()
    if ($configuredUser -notmatch '^\d+$' -or [int]$configuredUser -eq 0) {
        throw "Expected a numeric non-root image user, got '$configuredUser'"
    }

    # Use only a disposable, isolated database: API startup creates indexes.
    & docker run --detach --name $container --publish "127.0.0.1:${Port}:8080" --env "Mongo__Uri=$MongoUri" $Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed with exit code $LASTEXITCODE" }
    $started = $true

    $assembly = Invoke-Docker exec $container test -f /app/Shoreline.Imaging.dll
    if ($LASTEXITCODE -ne 0) {
        throw 'Shoreline.Imaging.dll is missing from the published image'
    }

    $uid = (Invoke-Docker exec $container id -u | Out-String).Trim()
    if ($uid -notmatch '^\d+$' -or [int]$uid -eq 0) {
        throw "Expected a non-root process UID, got '$uid'"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $health = (Invoke-Docker inspect --format '{{.State.Health.Status}}' $container | Out-String).Trim()
        if ($health -eq 'healthy') { break }
        if ($health -eq 'unhealthy') { throw 'Container health check reported unhealthy' }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($health -ne 'healthy') {
        throw "Container did not become healthy (last status: '$health')"
    }

    $response = Invoke-WebRequest -Uri "http://127.0.0.1:${Port}/health/live" -TimeoutSec 5
    if ($response.StatusCode -ne 200) {
        throw "Health endpoint returned HTTP $($response.StatusCode)"
    }

    [pscustomobject]@{
        Image = $Image
        ImageId = $imageId
        ConfiguredUser = $configuredUser
        ProcessUid = $uid
        Health = $health
        LiveEndpoint = $response.StatusCode
    }
}
finally {
    if ($started) { & docker rm --force $container 2>$null | Out-Null }
}
