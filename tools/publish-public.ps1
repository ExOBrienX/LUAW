<#
.SYNOPSIS
  Copia el estado actual del repositorio de desarrollo (privado) al repositorio público, sin lo que no se publica, y lo
  deja en un commit con el correo anónimo de GitHub (ADR-153).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish-public.ps1 -Message "LUAW 0.9.0-beta.1"
  powershell -ExecutionPolicy Bypass -File tools\publish-public.ps1 -Message "..." -Push

.NOTES
  - Se exporta lo que está commiteado en HEAD (git archive), no los cambios sin commitear.
  - No se publican: los aeropuertos guardados desde el simulador, CLAUDE.md, .claude/ ni la documentación interna
    (ver $excluded).
  - El repositorio público vive en ..\SimATC-public (clon de github.com/ExOBrienX/LUAW). Sin -Push solo se commitea ahí.
#>
param(
    [Parameter(Mandatory = $true)][string]$Message,
    [string]$PublicDir,
    [switch]$Push
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $PublicDir) { $PublicDir = Join-Path (Split-Path -Parent $repo) 'SimATC-public' }
$authorName = 'ExOBrienX'
$authorEmail = '126744020+ExOBrienX@users.noreply.github.com'

# Rutas que no se publican (relativas a la raíz, con / ).
# Documentación interna (requisitos, decisiones, pruebas, planes, propuestas, progreso, textos para flightsim.to): solo en
# el repo de desarrollo. Al público van los README, la licencia, los avisos y lo que usan la app y el empaquetado
# (docs/readme, docs/licenses, docs/package/README.txt).
$excluded = @('CLAUDE.md', '.claude/*', 'tests/SimATC.Core.Tests/Data/*.json', 'PROGRESO.md', 'PORTFOLIO.md', 'docs/*.md',
    'docs/proposals/*', 'docs/package/flightsim-*', 'docs/simatc-logo-docs.webp')

if (-not (Test-Path (Join-Path $PublicDir '.git'))) { throw "No existe el repositorio público en $PublicDir." }
if (git -C $repo status --porcelain) { Write-Warning 'Hay cambios sin commitear en el repo de desarrollo: se publica solo lo commiteado (HEAD).' }

# 1. Exportar HEAD a una carpeta temporal.
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("simatc-export-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$tar = Join-Path $tmp 'head.tar'
git -C $repo archive --format=tar -o $tar HEAD
$src = Join-Path $tmp 'src'
New-Item -ItemType Directory $src | Out-Null
# El tar de Windows (desde Git Bash, el tar de MSYS no entiende rutas C:\).
& (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $tar -C $src
if ($LASTEXITCODE -ne 0) { throw 'No se pudo extraer el archivo exportado.' }

# 2. Quitar lo que no se publica.
foreach ($pattern in $excluded) {
    Get-ChildItem (Join-Path $src $pattern) -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
}

# 3. Reemplazar el contenido del repo público (todo menos .git) por lo exportado.
Get-ChildItem $PublicDir -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force
Copy-Item (Join-Path $src '*') $PublicDir -Recurse -Force
Get-ChildItem $src -Force -Filter '.*' | Copy-Item -Destination $PublicDir -Recurse -Force
Remove-Item -Recurse -Force $tmp

# 4. Verificar que no se cuela nada excluido.
foreach ($pattern in $excluded) {
    if (Get-ChildItem (Join-Path $PublicDir $pattern) -Force -ErrorAction SilentlyContinue) { throw "Se coló $pattern en el repo público." }
}

# 5. Commit con el correo anónimo.
git -C $PublicDir add -A
if (-not (git -C $PublicDir status --porcelain)) { Write-Host 'Sin cambios para publicar.'; return }
git -C $PublicDir -c "user.name=$authorName" -c "user.email=$authorEmail" commit -q -m $Message
if ($LASTEXITCODE -ne 0) { throw 'El commit falló.' }
git -C $PublicDir log --oneline -1
if ($Push) {
    git -C $PublicDir push origin main
    if ($LASTEXITCODE -ne 0) { throw 'El push falló.' }
}
