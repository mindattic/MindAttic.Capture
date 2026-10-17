# Publishes the capture agent (capture.exe) and the tray recorder (capture-tray.exe) to %LOCALAPPDATA%\MindAttic\Capture\bin.
# Apps find the agent there to export sessions after they exit; add the folder to PATH to use `capture` anywhere.
# Safe while an export is running: a file in use is renamed to *.old (the running process keeps it) and replaced;
# leftover *.old files are removed on the next install once nothing uses them.
param([switch]$AddToPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $env:LOCALAPPDATA 'MindAttic\Capture\bin'
$stage = Join-Path ([IO.Path]::GetTempPath()) ("mindattic-capture-install-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $bin, $stage | Out-Null
try {
    foreach ($p in @('MindAttic.Capture.Agent', 'MindAttic.Capture.Tray')) {
        dotnet publish (Join-Path $root "src\$p\$p.csproj") -c Release -o $stage --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "publish $p failed" }
    }
    Get-ChildItem $bin -Recurse -File -Filter *.old | ForEach-Object { Remove-Item -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
    $moved = 0
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $target = Join-Path $bin $file.FullName.Substring($stage.Length + 1)
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        try {
            Copy-Item -LiteralPath $file.FullName $target -Force
        } catch [System.IO.IOException] {
            $old = "$target.$([DateTime]::Now.ToString('yyyyMMddHHmmss')).old"
            Move-Item -LiteralPath $target $old
            Copy-Item -LiteralPath $file.FullName $target -Force
            $moved++
        }
    }
    if ($moved -gt 0) { Write-Host "$moved file(s) were in use; the running copies were renamed to *.old and will be cleaned up next install." }
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
if ($AddToPath) {
    $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
    if (($userPath -split ';') -notcontains $bin) {
        [Environment]::SetEnvironmentVariable('PATH', "$userPath;$bin", 'User')
        Write-Host "Added $bin to your user PATH (new terminals pick it up)."
    }
}
Write-Host "Installed capture.exe and capture-tray.exe (the tray recorder) to $bin (MindAttic Browser now installs from its own repo, MindAttic.Browser)"
