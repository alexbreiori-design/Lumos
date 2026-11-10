#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$out = Join-Path $root "publish\win-x64"
Write-Host "Publicando Lumos (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish .\Lumos.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $out

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Publicado em $out" -ForegroundColor Green
