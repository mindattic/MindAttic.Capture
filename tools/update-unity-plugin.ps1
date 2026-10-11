# Rebuilds the netstandard2.1 core and copies it into the Unity package (unity/com.mindattic.capture/Runtime/Plugins).
# Run after changing src/MindAttic.Capture; every Unity project references the package by path and picks it up on focus.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'src\MindAttic.Capture\MindAttic.Capture.csproj') -c Release -f netstandard2.1 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$out = Join-Path $root 'src\MindAttic.Capture\bin\Release\netstandard2.1'
$plugins = Join-Path $root 'unity\com.mindattic.capture\Runtime\Plugins'
Copy-Item (Join-Path $out 'MindAttic.Capture.dll'), (Join-Path $out 'MindAttic.Capture.xml') $plugins -Force
Write-Host "Updated $plugins"
