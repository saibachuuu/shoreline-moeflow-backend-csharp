[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int] $Port = 25221,
    [string] $WslDistribution = 'Debian'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$token = [guid]::NewGuid().ToString('N')
$network = "shoreline-readiness-$token"
$mongoName = "shoreline-mongo-$token"
$apiName = "shoreline-api-$token"
$database = "readiness_$token"
$image = "shoreline-readiness:$token"
$ownedNetwork = $null
$ownedMongo = $null
$ownedApi = $null

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        & wsl.exe -d $WslDistribution -- docker @Arguments
    } else {
        & docker @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        # Never log arguments: the API environment includes the Mongo URI.
        throw "Docker command failed with exit code $LASTEXITCODE"
    }
}

try {
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        if ($repoRoot.Contains("'")) { throw 'Worktree paths containing an apostrophe are unsupported by WSL path conversion' }
        $context = (& wsl.exe -d $WslDistribution -- wslpath -a "'$repoRoot'" | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $context.StartsWith('/')) { throw 'WSL path conversion failed' }
        if ($context.Contains("'")) { throw 'Worktree paths containing an apostrophe are unsupported' }
        $context = "'$context'"
    } else { $context = $repoRoot }

    Invoke-Docker build --platform linux/amd64 --tag $image $context | Out-Host
    $imageId = (Invoke-Docker image inspect --format '{{.Id}}' $image | Out-String).Trim()
    $configuredUser = (Invoke-Docker image inspect --format '{{.Config.User}}' $image | Out-String).Trim()
    if ($configuredUser -notmatch '^\d+$' -or [int]$configuredUser -eq 0) { throw 'Image user is not numeric non-root' }

    $ownedNetwork = (Invoke-Docker network create --internal --label "shoreline.readiness=$token" $network | Out-String).Trim()
    $ownedMongo = (Invoke-Docker run --detach --name $mongoName --network $ownedNetwork --network-alias $mongoName --label "shoreline.readiness=$token" --tmpfs /data/db mongo:8.0 | Out-String).Trim()
    if ($ownedMongo -notmatch '^[a-f0-9]{64}$' -or $ownedNetwork -notmatch '^[a-f0-9]{64}$' -or $database -notmatch '^readiness_[a-f0-9]{32}$') { throw 'Invalid disposable resource identity' }
    $actualName = (Invoke-Docker inspect --format '{{.Name}}' $ownedMongo | Out-String).Trim()
    $mongoLabels = (Invoke-Docker inspect --format '{{json .Config.Labels}}' $ownedMongo | Out-String) | ConvertFrom-Json
    $networkLabels = (Invoke-Docker network inspect --format '{{json .Labels}}' $ownedNetwork | Out-String) | ConvertFrom-Json
    $actualLabel = $mongoLabels.'shoreline.readiness'
    $networkLabel = $networkLabels.'shoreline.readiness'
    $mongoNetworks = (Invoke-Docker inspect --format '{{json .NetworkSettings.Networks}}' $ownedMongo | Out-String) | ConvertFrom-Json
    $attachedNetwork = $mongoNetworks.$network
    if ($actualName -ne "/$mongoName" -or $actualLabel -ne $token -or $networkLabel -ne $token -or $attachedNetwork.NetworkID -ne $ownedNetwork -or $attachedNetwork.Aliases -notcontains $mongoName) { throw 'Disposable Mongo or network identity mismatch' }

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $ready = $false
    do {
        try {
            $pingExpression = 'quit(db.adminCommand({ping:1}).ok?0:1)'
            if ($IsWindows -or $env:OS -eq 'Windows_NT') { $pingExpression = "'$pingExpression'" }
            Invoke-Docker exec $ownedMongo mongosh --quiet --eval $pingExpression 2>$null | Out-Null
            $ready = $true
        } catch { Start-Sleep -Seconds 2 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Disposable Mongo did not become available' }

    # URI is constructed solely from this invocation's verified resources; never accept a target URI.
    $mongoUri = "mongodb://${mongoName}:27017/$database"
    $ownedApi = (Invoke-Docker run --detach --name $apiName --network $ownedNetwork --label "shoreline.readiness=$token" --publish "127.0.0.1:${Port}:8080" --env "Mongo__Uri=$mongoUri" $image | Out-String).Trim()
    if ($ownedApi -notmatch '^[a-f0-9]{64}$') { throw 'Invalid API container identity' }
    Invoke-Docker exec $ownedApi test -f /app/Shoreline.Imaging.dll | Out-Null
    $uid = (Invoke-Docker exec $ownedApi id -u | Out-String).Trim()
    if ($uid -notmatch '^\d+$' -or [int]$uid -eq 0) { throw 'API process user is not non-root' }

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $live = $null
    do {
        try { $live = Invoke-WebRequest -Uri "http://127.0.0.1:${Port}/health/live" -TimeoutSec 3 } catch { Start-Sleep -Seconds 2 }
    } while (-not $live -and [DateTime]::UtcNow -lt $deadline)
    if (-not $live -or $live.StatusCode -ne 200) { throw 'Liveness endpoint did not return HTTP 200' }
    $readyResponse = Invoke-WebRequest -Uri "http://127.0.0.1:${Port}/health/ready" -TimeoutSec 5
    if ($readyResponse.StatusCode -ne 200) { throw 'Readiness endpoint did not return HTTP 200' }
    [pscustomobject]@{ Image = $image; ImageId = $imageId; ConfiguredUser = $configuredUser; ProcessUid = $uid; Liveness = $live.StatusCode; Readiness = $readyResponse.StatusCode }
}
finally {
    if ($ownedApi) { try { Invoke-Docker rm --force $ownedApi | Out-Null } catch { Write-Warning 'Failed to remove owned API container' } }
    if ($ownedMongo) { try { Invoke-Docker rm --force $ownedMongo | Out-Null } catch { Write-Warning 'Failed to remove owned Mongo container' } }
    if ($ownedNetwork) { try { Invoke-Docker network rm $ownedNetwork | Out-Null } catch { Write-Warning 'Failed to remove owned network' } }
}
