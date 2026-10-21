@echo off
rem Builds ScreenLayout.exe and packs it for a GitHub release: dist\ScreenLayout-<version>.zip
rem (version from src\AssemblyInfo.cs). Stop a running watcher first, it holds the exe open.
setlocal
call "%~dp0build.cmd" || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop'; Set-Location -LiteralPath '%~dp0';" ^
  "$v = (Get-Item .\ScreenLayout.exe).VersionInfo.ProductVersion;" ^
  "$out = \"dist\ScreenLayout-$v.zip\";" ^
  "New-Item -ItemType Directory -Force dist | Out-Null; if (Test-Path $out) { Remove-Item $out };" ^
  "Compress-Archive -Path ScreenLayout.exe, layouts.example.ini, README.md, LICENSE -DestinationPath $out;" ^
  "Get-FileHash $out -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + (Split-Path $out -Leaf) } | Set-Content \"$out.sha256\";" ^
  "Write-Host \"$out\"; Get-Content \"$out.sha256\""
