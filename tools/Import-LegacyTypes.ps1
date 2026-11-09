param([Parameter(Mandatory = $true)][string]$LegacyDirectory)
$ErrorActionPreference = 'Stop'
# Read only checked-in constants and translation text. No Python application,
# deployment configuration or service is loaded by this offline converter.
function Read-PublicSource([string]$relative) {
    [IO.File]::ReadAllText((Join-Path $LegacyDirectory $relative), [Text.Encoding]::UTF8)
}
$translations = @{}
foreach ($entry in [regex]::Matches((Read-PublicSource 'app/translations/en/LC_MESSAGES/messages.po'), '(?m)^msgid ("(?:[^"\\]|\\.)*")\r?\nmsgstr ("(?:[^"\\]|\\.)*")')) {
    $translations[($entry.Groups[1].Value | ConvertFrom-Json)] = $entry.Groups[2].Value | ConvertFrom-Json
}
function English([string]$text) {
    if ($translations[$text]) { $translations[$text] } else { $text }
}
function Class-Block([string]$source, [string]$name) {
    $match = [regex]::Match($source, '(?ms)^class ' + [regex]::Escape($name) + '\b[^\r\n]*:\r?\n(.*?)(?=^class |\z)')
    if (!$match.Success) { throw "Missing constant class: $name" }
    $match.Groups[1].Value
}
function Read-Types([string]$source, [string]$name, [object[]]$parents = @()) {
    $block = Class-Block $source $name
    $types = @{}
    foreach ($row in $parents) {
        $copy = [ordered]@{}
        foreach ($key in $row.Keys) { $copy[$key] = $row[$key] }
        $types[$row.code] = $copy
    }
    foreach ($entry in [regex]::Matches($block, '(?m)^    ([A-Z_]+)\s*=\s*(\d+|"[^"\r\n]*")\s*(?:#.*)?$')) {
        $code = $entry.Groups[1].Value
        $types[$code] = [ordered]@{ code = $code; id = ($entry.Groups[2].Value | ConvertFrom-Json); name = $code; intro = '' }
    }
    foreach ($entry in [regex]::Matches($block, '(?s)"([A-Z_]+)"\s*:\s*\{(.*?)\}')) {
        $code = $entry.Groups[1].Value
        if (!$types.ContainsKey($code)) { continue }
        foreach ($field in @('name', 'intro')) {
            $match = [regex]::Match($entry.Groups[2].Value, '"' + $field + '":\s*(?:lazy_)?gettext\(\s*("(?:[^"\\]|\\.)*")\s*\)')
            if ($match.Success) { $types[$code][$field] = $match.Groups[1].Value | ConvertFrom-Json }
        }
    }
    @(foreach ($row in ($types.Values | Sort-Object { $_.id })) {
        $row.localized_en = English $row.name
        $row.intro_en = English $row.intro
        $row
    })
}
function Read-Roles([string]$source, [object[]]$permissions) {
    $ids = @{}
    foreach ($permission in $permissions) { $ids[$permission.code] = $permission.id }
    $block = [regex]::Match($source, '(?s)system_role_data:.*?=\s*\[(.*?)\r?\n    \]').Groups[1].Value
    $rows = @(foreach ($entry in [regex]::Matches($block, '(?s)\{(.*?)\}')) {
        $fields = @{}
        foreach ($field in @('name', 'system_code')) {
            $match = [regex]::Match($entry.Value, '"' + $field + '":\s*("(?:[^"\\]|\\.)*")')
            if (!$match.Success) { throw "Incomplete role: $field" }
            $fields[$field] = $match.Groups[1].Value | ConvertFrom-Json
        }
        $roleIds = @(foreach ($reference in [regex]::Matches($entry.Value, '(?:Team|Project)Permission\.([A-Z_]+)')) {
            $code = $reference.Groups[1].Value
            if (!$ids.ContainsKey($code)) { throw "Unknown permission constant: $code" }
            $ids[$code]
        })
        $level = [regex]::Match($entry.Value, '"level":\s*(\d+)')
        if (!$level.Success -or !$roleIds.Count) { throw 'Incomplete role definition' }
        [ordered]@{ name = $fields.name; localized_en = (English $fields.name); system_code = $fields.system_code;
            level = [int]$level.Groups[1].Value; permissions = @($roleIds | Sort-Object -Unique) }
    })
    if (!$rows.Count -or @($rows.system_code | Select-Object -Unique).Count -ne $rows.Count) { throw 'Empty or duplicate roles' }
    $rows
}
$rbac = Read-PublicSource 'app/core/rbac.py'
$base = @(Read-Types $rbac 'PermissionMixin')
$allow = @(Read-Types $rbac 'AllowApplyType')
$checks = @(Read-Types $rbac 'ApplicationCheckType')
$team = Read-PublicSource 'app/models/team.py'
$project = Read-PublicSource 'app/models/project.py'
$teamPermissions = @(Read-Types $team 'TeamPermission' $base)
$projectPermissions = @(Read-Types $project 'ProjectPermission' $base)
$result = [ordered]@{
    team = [ordered]@{ permissions = $teamPermissions; roles = @(Read-Roles $team $teamPermissions);
        allow_apply_types = $allow; application_check_types = $checks }
    project = [ordered]@{ permissions = $projectPermissions; roles = @(Read-Roles $project $projectPermissions);
        allow_apply_types = @(Read-Types $project 'ProjectAllowApplyType' $allow); application_check_types = $checks }
    locales = @(Read-Types (Read-PublicSource 'app/constants/locale.py') 'Locale')
}
$destination = Join-Path $PSScriptRoot '../src/Shoreline.Api/Features/system-types.json'
[IO.File]::WriteAllText($destination, (($result | ConvertTo-Json -Depth 10) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output "Converted $($result.team.roles.Count) team roles and $($result.project.roles.Count) project roles from public constants."
