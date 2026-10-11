# Publishes the capture agent (capture.exe) and MindAttic Browser to %LOCALAPPDATA%\MindAttic\Capture\bin.
# Apps find the agent there to export sessions after they exit; add the folder to PATH to use `capture` anywhere.
param([switch]$AddToPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $env:LOCALAPPDATA 'MindAttic\Capture\bin'
New-Item -ItemType Directory -Force $bin | Out-Null
foreach ($p in 'MindAttic.Capture.Agent', 'MindAttic.Capture.Browser') {
    dotnet publish (Join-Path $root "src\$p\$p.csproj") -c Release -o $bin --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish $p failed" }
}
if ($AddToPath) {
    $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
    if (($userPath -split ';') -notcontains $bin) {
        [Environment]::SetEnvironmentVariable('PATH', "$userPath;$bin", 'User')
        Write-Host "Added $bin to your user PATH (new terminals pick it up)."
    }
}
Write-Host "Installed capture.exe and MindAttic.Browser.exe to $bin"
