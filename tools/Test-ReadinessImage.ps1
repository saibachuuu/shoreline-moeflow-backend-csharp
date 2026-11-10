[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int] $Port = 25221,
    [string] $WslDistribution = 'Debian',
    [string] $Image = 'shoreline-moeflow-readiness:local',
    [switch] $SkipBuild,
    [switch] $KeepResources,
    # Self-test: forces the historical missing-Storage bootstrap failure to prove honest reporting.
    [switch] $SimulateStorageFailure
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$token = [guid]::NewGuid().ToString('N')
$network = "shoreline-readiness-$token"
$mongoName = "shoreline-mongo-$token"
$apiName = "shoreline-api-$token"
$database = "readiness_$token"
# Disposable, per-run secret: 64 hex characters, never a real credential.
$secret = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$adminEmail = "readiness-admin-$token@example.invalid"
$storageDomain = "http://127.0.0.1:$Port/readiness-objects"
$storageTmpfs = "/tmp/shoreline-$token"
$storageRoot = "$storageTmpfs/storage"
$evidencePath = Join-Path $env:TEMP "shoreline-readiness-$token.log"
$probeRoot = Join-Path $env:TEMP "shoreline-imaging-probe-$token"
$onWindows = $IsWindows -or $env:OS -eq 'Windows_NT'
# mongo:8.0 index digest resolved by the local WSL Engine on 2026-11-10.
$mongoImage = 'mongo:8.0@sha256:b9924c330a8bbc5800fe9c69f04a84e221eca7b28fd049a0354147d9908fa142'
$ownedNetwork = $null
$ownedMongo = $null
$ownedApi = $null
$result = [ordered]@{ Evidence = $evidencePath }

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    if ($onWindows) { & wsl.exe -d $WslDistribution -- docker @Arguments }
    else { & docker @Arguments }
    if ($LASTEXITCODE -ne 0) {
        # Log only the operation and subcommand, never env values or the auth secret.
        $operation = ($Arguments | Select-Object -First 2) -join ' '
        throw "Docker command '$operation' failed with exit code $LASTEXITCODE"
    }
}

function Invoke-InApi {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    Invoke-Docker exec $ownedApi @Arguments
}

function Save-Evidence {
    param([string] $Note)
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("note: $Note")
    $lines.Add("time: $([DateTime]::UtcNow.ToString('o'))")
    $lines.Add("image: $Image")
    try { $lines.Add("api-state: " + (Invoke-Docker inspect --format '{{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}}' $ownedApi | Out-String).Trim()) } catch { $lines.Add("api-state: unavailable") }
    try { $lines.Add("api-logs:`n" + (Invoke-Docker logs --tail 60 $ownedApi 2>&1 | Out-String).Trim()) } catch { $lines.Add("api-logs: unavailable") }
    Set-Content -Path $evidencePath -Value $lines -Encoding utf8
}

function Assert-ApiRunning {
    $state = (Invoke-Docker inspect --format '{{.State.Status}}' $ownedApi | Out-String).Trim()
    if ($state -eq 'running') { return }
    Save-Evidence "API exited during startup (bootstrap failure): $state"
    $firstError = ''
    try { $firstError = ((Invoke-Docker logs --tail 60 $ownedApi 2>&1 | Out-String) -split "`r?`n" | Where-Object { $_ -match 'Exception|error' } | Select-Object -First 1) } catch { }
    throw "API bootstrap failed ($state): $firstError. Evidence: $evidencePath"
}

try {
    if ($onWindows) {
        if ($repoRoot.Contains("'")) { throw 'Worktree paths containing an apostrophe are unsupported by WSL path conversion' }
        $context = (& wsl.exe -d $WslDistribution -- wslpath -a "'$repoRoot'" | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $context.StartsWith('/')) { throw 'WSL path conversion failed' }
        if ($context.Contains("'")) { throw 'Worktree paths containing an apostrophe are unsupported' }
        $buildContext = "'$context'"
    } else {
        $buildContext = $repoRoot
    }

    if (-not $SkipBuild) {
        Invoke-Docker build --platform linux/amd64 --tag $Image $buildContext | Out-Host
    }
    $result.ImageId = (Invoke-Docker image inspect --format '{{.Id}}' $Image | Out-String).Trim()
    $configuredUser = (Invoke-Docker image inspect --format '{{.Config.User}}' $Image | Out-String).Trim()
    $entrypoint = (Invoke-Docker image inspect --format '{{json .Config.Entrypoint}}' $Image | Out-String).Trim()
    if ($configuredUser -notmatch '^\d+$' -or [int]$configuredUser -eq 0) { throw 'Image user is not numeric non-root' }
    if ($entrypoint -notmatch 'Shoreline\.Api\.dll') { throw 'Image entrypoint does not start Shoreline.Api.dll' }
    $result.ConfiguredUser = $configuredUser

    # Mongo stays on a private bridge with no published port; only the API loopback port is exposed.
    $ownedNetwork = (Invoke-Docker network create --label "shoreline.readiness=$token" $network | Out-String).Trim()
    if ($ownedNetwork -notmatch '^[a-f0-9]{64}$') { throw 'Invalid disposable network identity' }
    $ownedMongo = (Invoke-Docker run --detach --name $mongoName --network $ownedNetwork --network-alias $mongoName --label "shoreline.readiness=$token" --tmpfs /data/db $mongoImage | Out-String).Trim()
    if ($ownedMongo -notmatch '^[a-f0-9]{64}$') { throw 'Invalid disposable Mongo container identity' }

    $mongoLabels = (Invoke-Docker inspect --format '{{json .Config.Labels}}' $ownedMongo | Out-String) | ConvertFrom-Json
    $networkLabels = (Invoke-Docker network inspect --format '{{json .Labels}}' $ownedNetwork | Out-String) | ConvertFrom-Json
    $mongoNetworks = (Invoke-Docker inspect --format '{{json .NetworkSettings.Networks}}' $ownedMongo | Out-String) | ConvertFrom-Json
    $mongoPorts = (Invoke-Docker port $ownedMongo | Out-String).Trim()
    if ($mongoLabels.'shoreline.readiness' -ne $token -or $networkLabels.'shoreline.readiness' -ne $token) { throw 'Disposable resource label mismatch' }
    if ($mongoNetworks.$network.NetworkID -ne $ownedNetwork -or $mongoNetworks.$network.Aliases -notcontains $mongoName) { throw 'Disposable Mongo network attachment mismatch' }
    if ($mongoPorts.Length -ne 0) { throw 'Disposable Mongo unexpectedly publishes host ports' }

    $pingExpression = 'quit(db.adminCommand({ping:1}).ok?0:1)'
    if ($onWindows) { $pingExpression = "'$pingExpression'" }
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try { Invoke-Docker exec $ownedMongo mongosh --quiet --eval $pingExpression 2>$null | Out-Null; $ready = $true }
        catch { Start-Sleep -Seconds 2 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Disposable Mongo did not become available' }

    # Build both URIs from this run's verified resources only; no external storage or credentials.
    $mongoUri = "mongodb://${mongoName}:27017/$database"
    $runArguments = @(
        'run', '--detach', '--name', $apiName, '--network', $ownedNetwork,
        '--label', "shoreline.readiness=$token", '--publish', "127.0.0.1:${Port}:8080",
        '--tmpfs', "${storageTmpfs}:rw,uid=$configuredUser,gid=$configuredUser,mode=0700",
        '--env', "Mongo__Uri=$mongoUri", '--env', "Auth__Secret=$secret", '--env', "Auth__AdminEmail=$adminEmail"
    )
    if (-not $SimulateStorageFailure) {
        $runArguments += @('--env', 'Storage__Type=Local', '--env', "Storage__Root=$storageRoot", '--env', "Storage__Domain=$storageDomain")
    }
    $runArguments += $Image
    $ownedApi = (Invoke-Docker @runArguments | Out-String).Trim()
    if ($ownedApi -notmatch '^[a-f0-9]{64}$') { throw 'Invalid API container identity' }

    # Give startup a moment, then verify the app did not die during DI/bootstrap.
    Start-Sleep -Seconds 4
    Assert-ApiRunning

    $result.ProcessUid = (Invoke-InApi id -u | Out-String).Trim()
    if ($result.ProcessUid -notmatch '^\d+$' -or [int]$result.ProcessUid -eq 0) { throw "API process is not non-root: $($result.ProcessUid)" }
    if ($result.ProcessUid -ne $configuredUser) { throw "Process UID $($result.ProcessUid) differs from configured user $configuredUser" }
    Invoke-InApi test -f /app/Shoreline.Imaging.dll | Out-Null
    $result.ImagingAssembly = 'present'

    # The non-root process must own and write a disposable Local storage root.
    if (-not $SimulateStorageFailure) {
        Invoke-InApi @('test', '-w', $storageRoot) | Out-Null
        Invoke-InApi @('touch', "$storageRoot/probe") | Out-Null
        Invoke-InApi @('rm', "$storageRoot/probe") | Out-Null
        $result.LocalStorage = "writable:$storageRoot"
    }

    # Native Imaging: bundled Magick.NET native library must resolve every shared object.
    $nativePath = '/app/runtimes/linux-x64/native/Magick.Native-Q8-x64.dll.so'
    Invoke-InApi test -f $nativePath | Out-Null
    $ldd = (Invoke-InApi ldd $nativePath | Out-String)
    if ($ldd -match 'not found') { throw "Native Imaging library has unresolved dependencies: $ldd" }
    $result.NativeImaging = 'ldd:resolved'

    # Real decode through Shoreline.Imaging, so a load-only check cannot mask a broken codec.
    New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
    Set-Content -Path (Join-Path $probeRoot 'ImagingProbe.csproj') -Encoding utf8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$repoRoot\src\Shoreline.Imaging\Shoreline.Imaging.csproj" />
  </ItemGroup>
</Project>
"@
    Set-Content -Path (Join-Path $probeRoot 'Program.cs') -Encoding utf8 -Value @'
using ImageMagick;
using Shoreline.Imaging;

using var image = new MagickImage(MagickColors.Red, 2, 2);
using var stream = new MemoryStream();
image.Write(stream, MagickFormat.Png);
Console.WriteLine($"MAGICK_OK format={image.Format} bytes={stream.Length}");

var root = Path.Combine(Path.GetTempPath(), "imaging-probe-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(root);
    var source = Path.Combine(root, "source.png");
    File.WriteAllBytes(source, stream.ToArray());
    var processed = new ImageProcessor().Process(source, Path.Combine(root, "out"));
    Console.WriteLine($"IMAGING_OK cover={processed.CoverWidth}x{processed.CoverHeight} resample={processed.ResampleWidth}x{processed.ResampleHeight}");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
'@
    $publishOutput = Join-Path $probeRoot 'out'
    & dotnet publish $probeRoot -c Release -o $publishOutput --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Imaging probe could not be compiled on the host (SDK or cached packages unavailable)' }
    $probeWsl = if ($onWindows) { (& wsl.exe -d $WslDistribution -- wslpath -a "'$publishOutput'" | Out-String).Trim() } else { $publishOutput }
    if (-not $probeWsl.StartsWith('/')) { throw 'Imaging probe path conversion failed' }
    $probeRun = if ($onWindows) {
        & wsl.exe -d $WslDistribution -- docker run --rm --user "$($configuredUser):$($configuredUser)" --entrypoint dotnet --mount "type=bind,source=$probeWsl,target=/probe,readonly" $Image /probe/ImagingProbe.dll
    } else {
        & docker run --rm --user "$($configuredUser):$($configuredUser)" --entrypoint dotnet --mount "type=bind,source=$probeWsl,target=/probe,readonly" $Image /probe/ImagingProbe.dll
    }
    if ($LASTEXITCODE -ne 0 -or ($probeRun | Out-String) -notmatch 'IMAGING_OK') { throw 'Native Imaging probe failed to decode/process an image' }
    $result.NativeImagingDecode = (($probeRun | Out-String).Trim() -replace "`r?`n", '; ')

    $live = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try { $live = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health/live" -TimeoutSec 3 } catch { Start-Sleep -Seconds 2 }
        if (-not $live) {
            $state = (Invoke-Docker inspect --format '{{.State.Status}}' $ownedApi | Out-String).Trim()
            if ($state -ne 'running') {
                Save-Evidence "API exited before liveness (bootstrap failure): $state"
                $result.Bootstrap = "failed:$state"
                throw "API exited before liveness ($state). Evidence: $evidencePath"
            }
        }
    } while (-not $live -and [DateTime]::UtcNow -lt $deadline)
    if (-not $live -or $live.StatusCode -ne 200) { throw 'Liveness endpoint did not return HTTP 200' }
    $result.Liveness = "HTTP $($live.StatusCode)"

    # Readiness hits Mongo, so it is checked separately from liveness.
    $readyResponse = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health/ready" -TimeoutSec 10
    if ($readyResponse.StatusCode -ne 200) { throw "Readiness endpoint returned HTTP $($readyResponse.StatusCode)" }
    $result.Readiness = "HTTP $($readyResponse.StatusCode)"
    $result.Bootstrap = 'ok'

    Set-Content -Path $evidencePath -Encoding utf8 -Value @(
        "result: pass",
        "image: $Image ($($result.ImageId))",
        "user: configured=$configuredUser process=$($result.ProcessUid)",
        "liveness: $($result.Liveness)",
        "readiness: $($result.Readiness)",
        "native-imaging: $($result.NativeImagingDecode)"
    )
    [pscustomobject]$result
}
catch {
    $result.Error = $_.Exception.Message
    if (-not (Test-Path $evidencePath)) { try { Save-Evidence 'smoke test failed' } catch { } }
    [pscustomobject]$result
    throw
}
finally {
    if ($KeepResources) {
        Write-Warning "Keeping disposable resources: network=$network mongo=$mongoName api=$apiName"
    } else {
        if ($ownedApi) { try { Invoke-Docker rm --force $ownedApi | Out-Null } catch { Write-Warning 'Failed to remove owned API container' } }
        if ($ownedMongo) { try { Invoke-Docker rm --force $ownedMongo | Out-Null } catch { Write-Warning 'Failed to remove owned Mongo container' } }
        if ($ownedNetwork) { try { Invoke-Docker network rm $ownedNetwork | Out-Null } catch { Write-Warning 'Failed to remove owned network' } }
    }
    if (Test-Path $probeRoot) { Remove-Item $probeRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
