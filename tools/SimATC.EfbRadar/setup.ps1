# Prepara y compila la app del EFB "SimATC Radar" (ADR-073, prueba V-40).
# Copia del SDK de MSFS 2024 la API del EFB y el SDK de JS (no se suben al repo), instala dependencias y compila.
$ErrorActionPreference = "Stop"
$sdk = if ($env:MSFS2024_SDK) { $env:MSFS2024_SDK } else { "C:\MSFS 2024 SDK\" }
$src = Join-Path $sdk "Samples\DevmodeProjects\EFB"
$here = $PSScriptRoot
Copy-Item -Recurse -Force (Join-Path $src "PackageSources\efb_api") (Join-Path $here "PackageSources\")
Copy-Item -Recurse -Force (Join-Path $src "PackageSources\vendor") (Join-Path $here "PackageSources\")
Copy-Item -Force (Join-Path $src "PackageDefinitions\mycompany-efb-templateapp\ContentInfo\thumbnail.jpg") (Join-Path $here "PackageDefinitions\simatc-efb-radar\ContentInfo\")
Push-Location (Join-Path $here "PackageSources\efb_api"); npm install --no-audit --no-fund; Pop-Location
Push-Location (Join-Path $here "PackageSources\RadarApp"); npm install --no-audit --no-fund; npm run build; Pop-Location
Write-Host "Listo: PackageSources\RadarApp\dist. Compila el paquete en MSFS (Modo Desarrollador > Project Editor) con SimATCRadarProject.xml."
