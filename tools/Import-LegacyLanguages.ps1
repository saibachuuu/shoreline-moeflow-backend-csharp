param([Parameter(Mandatory = $true)][string]$LegacyDirectory)
$ErrorActionPreference = 'Stop'
# Offline conversion of checked-in public constants; never import the Python app
# or load a deployment configuration. The C# runtime uses only the generated JSON.
$source = [IO.File]::ReadAllText((Join-Path $LegacyDirectory 'app/models/language.py'), [Text.Encoding]::UTF8)
$block = [regex]::Match($source, '(?s)SYSTEM_LANGUAGES_DATA:.*?=\s*\[(.*?)\n    \]').Groups[1].Value
$catalog = [IO.File]::ReadAllText((Join-Path $LegacyDirectory 'app/translations/en/LC_MESSAGES/messages.po'), [Text.Encoding]::UTF8)
$translations = @{}
foreach ($entry in [regex]::Matches($catalog, '(?m)^msgid ("(?:[^"\\]|\\.)*")\r?\nmsgstr ("(?:[^"\\]|\\.)*")')) {
    $translations[($entry.Groups[1].Value | ConvertFrom-Json)] = $entry.Groups[2].Value | ConvertFrom-Json
}
$rows = @(foreach ($entry in [regex]::Matches($block, '(?s)\{(.*?)\}')) {
    $row = [ordered]@{}
    foreach ($field in @('code', 'en_name', 'lo_name', 'g_tra_code', 'g_ocr_code')) {
        $match = [regex]::Match($entry.Value, '"' + $field + '":\s*(?:hardcode_text\()?"([^"]*)"')
        if (!$match.Success) { throw "Unsupported language constant: $field" }
        $row[$field] = $match.Groups[1].Value
    }
    if (!$row.code -or !$row.en_name -or !$row.lo_name) { throw 'Incomplete language constant' }
    $row.no_space = $entry.Value -match '"no_space": True'
    $row.localized_en = if ($translations[$row.lo_name]) { $translations[$row.lo_name] } else { $row.lo_name }
    $row
})
if ($rows.Count -eq 0 -or @($rows.code | Select-Object -Unique).Count -ne $rows.Count) { throw 'Empty or duplicate language constants' }
$destination = Join-Path $PSScriptRoot '../src/Shoreline.Api/Features/system-languages.json'
[IO.File]::WriteAllText($destination, (($rows | ConvertTo-Json -Depth 5) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output "Converted $($rows.Count) languages from public source constants."
