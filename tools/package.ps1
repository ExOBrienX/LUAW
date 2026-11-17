<#
.SYNOPSIS
  Arma el paquete de LUAW para publicar: build y tests, publicación autocontenida (win-x64, sin necesidad de instalar
  .NET), limpieza de archivos de depuración, documentos para el usuario y un zip con su SHA-256.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package.ps1
  powershell -ExecutionPolicy Bypass -File tools\package.ps1 -SkipTests

.NOTES
  La versión sale de Directory.Build.props (VersionPrefix y VersionSuffix). El resultado queda en artifacts\ (ignorado
  por git). Requiere el SDK de MSFS 2024 instalado para compilar (de ahí sale SimConnect.dll, ver SimATC.Sim.csproj).
#>
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

$app = 'src\SimATC.App\SimATC.App.csproj'
$version = (dotnet msbuild $app -getProperty:Version -nologo).Trim()
if (-not $version) { throw 'No se pudo leer la versión (Directory.Build.props).' }
# Nombre descriptivo (flightsim.to pide que el archivo diga qué es).
$name = "LUAW-ATC-and-AI-Traffic-MSFS2024-$version-win-x64"
$artifacts = Join-Path $repo 'artifacts'
$stage = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"
Write-Host "LUAW $version" -ForegroundColor Green

if (-not $SkipTests) {
    Step 'Build y tests'
    dotnet build -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'El build falló.' }
    dotnet test -c Release --no-build -v q
    if ($LASTEXITCODE -ne 0) { throw 'Hay tests en rojo: no se empaqueta.' }
}

Step 'Publicación autocontenida (win-x64)'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip) { Remove-Item -Force $zip }
dotnet publish $app -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o (Join-Path $stage 'LUAW') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'La publicación falló.' }

Step 'Limpieza'
# Símbolos y bibliotecas de enlace de los paquetes nativos (SkiaSharp, ONNX): el usuario no los necesita (~100 MB).
Get-ChildItem (Join-Path $stage 'LUAW') -Recurse -Include *.pdb, *.lib | Remove-Item -Force

# Lo que tiene que estar sí o sí: si falta algo, el paquete no sirve.
$required = @('LUAW.exe', 'SimATC.KokoroHost.exe', 'SimConnect.dll', 'Microsoft.FlightSimulator.SimConnect.dll',
    'data\i18n\en.json', 'data\phraseology\icao-en.json', 'data\map\world-50m.json')
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $stage "LUAW\$f"))) { throw "Falta $f en el paquete." }
}

Step 'Licencias de terceros'
# Los textos de licencia y avisos que traen los paquetes incluidos (MIT, BSD, avisos de ONNX y .NET): las licencias piden
# acompañar el binario con ellos. Se toman de la caché de NuGet según project.assets.json (lo que de verdad se publicó).
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$assetFiles = Get-ChildItem (Join-Path $repo 'src') -Recurse -Filter project.assets.json |
    Where-Object { $_.FullName -match 'LUAW\.(App|Core|Sim|Voice|KokoroHost)\\obj' }
$libs = @{}
foreach ($a in $assetFiles) {
    $json = Get-Content $a.FullName -Raw | ConvertFrom-Json
    foreach ($lib in $json.libraries.PSObject.Properties) {
        if ($lib.Value.type -eq 'package') { $libs[$lib.Name.ToLowerInvariant()] = $lib.Value.path }
    }
}
$runtime = (Get-Content (Join-Path $stage 'LUAW\LUAW.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks[0].version
$libs["microsoft.netcore.app.runtime.win-x64/$runtime"] = "microsoft.netcore.app.runtime.win-x64/$runtime"
$copied = 0
foreach ($path in $libs.Values) {
    $dir = Join-Path $nuget $path
    if (-not (Test-Path $dir)) { continue }
    $id = ($path -split '/')[0]
    # Fuera las herramientas de desarrollo y las bibliotecas nativas de otros sistemas (no van en el paquete de Windows).
    if ($id -match '^(xunit|microsoft\.net\.test|benchmarkdotnet|avaloniaui\.diagnosticssupport)|nativeassets\.(linux|macos|webassembly|android|ios)') { continue }
    Get-ChildItem $dir -File | Where-Object { $_.Name -match '(?i)^(licen[cs]e|third.?party.?notices?|notice)' } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $licenses "$id-$($_.Name)")
        $copied++
    }
}
Copy-Item (Join-Path $repo 'docs\licenses\*') $licenses
Write-Host "  $copied archivos de licencia de paquetes + los de docs\licenses"

Step 'Documentos para el usuario'
# Junto a la carpeta de la app, a la vista al abrir el zip. Los que todavía no existen se avisan (no detienen el paquete).
$docs = @{ 'README.txt' = 'docs\package\README.txt'; 'LICENSE.txt' = 'LICENSE'; 'THIRD-PARTY-NOTICES.txt' = 'THIRD-PARTY-NOTICES.md' }
foreach ($target in $docs.Keys) {
    $source = Join-Path $repo $docs[$target]
    if (Test-Path $source) {
        # {VERSION} en el README del paquete se reemplaza por la versión real.
        $text = [IO.File]::ReadAllText($source).Replace('{VERSION}', $version)
        [IO.File]::WriteAllText((Join-Path $stage $target), $text, (New-Object Text.UTF8Encoding $false))
    }
    else { Write-Warning "Falta $($docs[$target]): el paquete sale sin $target." }
}

Step 'Zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item $zip).Length / 1MB
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $name.zip" | Set-Content -Encoding ascii "$zip.sha256"
$unpacked = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("`nListo: {0}`n  {1:0.0} MB comprimido, {2:0} MB descomprimido`n  SHA-256 {3}" -f $zip, $size, $unpacked, $hash) -ForegroundColor Green
