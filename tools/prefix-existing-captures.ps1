# Renames captures made before file names carried the project name:
#   R:\MindAttic\Capture\UnityEve\2026-10-11T14.58.34.885.mkv  ->  ...\UnityEve\UnityEve_2026-10-11T14.58.34.885.mkv
# and rewrites the file names inside the .session.json manifests and .json sidecars to match.
# Only renames; never deletes. Files still being written (in use) are skipped, so it is safe to re-run.
#
#   .\prefix-existing-captures.ps1 -WhatIf     # list what would change
#   .\prefix-existing-captures.ps1             # do it
[CmdletBinding(SupportsShouldProcess)]
param([string]$Root = 'R:\MindAttic\Capture')

$utf8 = [System.Text.UTF8Encoding]::new($false)
$total = 0; $skipped = 0

foreach ($dir in Get-ChildItem $Root -Directory | Where-Object Name -NotLike '.*') {
    $prefix = "$($dir.Name)_"
    $renamed = @{}
    foreach ($file in Get-ChildItem $dir.FullName -File | Where-Object { -not $_.Name.StartsWith($prefix) }) {
        $new = $prefix + $file.Name
        if (Test-Path (Join-Path $dir.FullName $new)) { Write-Warning "$($file.FullName): $new already exists; skipped."; $skipped++; continue }
        if (-not $PSCmdlet.ShouldProcess($file.FullName, "Rename to $new")) { continue }
        try {
            Rename-Item -LiteralPath $file.FullName -NewName $new -ErrorAction Stop
            $renamed[$file.Name] = $new
        } catch {
            Write-Warning "$($file.FullName): $($_.Exception.Message) Skipped."
            $skipped++
        }
    }
    if ($renamed.Count -eq 0) { continue }
    $total += $renamed.Count

    # Manifests list full paths (JSON-escaped backslashes) and sidecars list bare names; both sit right after " or \.
    $pattern = '(?<=["\\])(' + (($renamed.Keys | Sort-Object Length -Descending | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?=")'
    $regex = [regex]::new($pattern)
    foreach ($json in Get-ChildItem $dir.FullName -File -Filter *.json) {
        try {
            $text = [System.IO.File]::ReadAllText($json.FullName)
            $updated = $regex.Replace($text, { param($m) $renamed[$m.Value] })
            if ($updated -ne $text) { [System.IO.File]::WriteAllText($json.FullName, $updated, $utf8) }
        } catch {
            Write-Warning "$($json.FullName): references not updated: $($_.Exception.Message)"
        }
    }
    Write-Host "$($dir.Name): renamed $($renamed.Count) file(s)"
}
Write-Host "Done: $total renamed, $skipped skipped."
