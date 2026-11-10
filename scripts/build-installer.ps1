#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

& "$PSScriptRoot\publish.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$iscc = @(
  "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
  Write-Error "Inno Setup 6 nao encontrado. Instale com: winget install JRSoftware.InnoSetup"
  exit 1
}

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "Gerando instalador..." -ForegroundColor Cyan
& $iscc (Join-Path $root "installer\Lumos.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem $dist -Filter "Lumos-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object {
  Write-Host "Instalador: $($_.FullName)" -ForegroundColor Green
}
