# Compila ENCOFRADO para Revit 2024, 2025, 2026 y 2027 y deja los paquetes en .\dist
#   .\build.ps1                 -> todas las versiones
#   .\build.ps1 -Versions 2025  -> solo una versión
param([string[]]$Versions = @("2024", "2025", "2026", "2027"))
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "src\Encofrado\Encofrado.csproj"

foreach ($v in $Versions) {
    Write-Host "== Revit $v ==" -ForegroundColor Cyan
    dotnet build $proj -c Release -p:RevitVersion=$v
    if ($LASTEXITCODE -ne 0) { throw "Falló la compilación para Revit $v" }

    $out = Join-Path $root "src\Encofrado\bin\Release\Revit$v"
    $dist = Join-Path $root "dist\Revit$v"
    New-Item -ItemType Directory -Force -Path (Join-Path $dist "Encofrado") | Out-Null
    Copy-Item (Join-Path $out "Encofrado.dll") (Join-Path $dist "Encofrado") -Force
    Copy-Item (Join-Path $out "Encofrado.addin") $dist -Force
}
Write-Host "Listo. Paquetes en $root\dist" -ForegroundColor Green
