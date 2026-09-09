<#
.SYNOPSIS
  Собирает Release-конфигурацию и упаковывает дистрибутив Enviriot.

.DESCRIPTION
  Единственное место, где заданы правила упаковки, - скрипт вызывается и вручную,
  и из .github\workflows\release.yml, чтобы локальный и CI-архив совпадали:

      enviriot\bin   <- Output\bin_r, без *.pdb
      enviriot\www   <- Output\www,   без WebUI.www.csproj, без папок bin и obj

  Номер версии не задаётся снаружи: его выставляет UpdateVersionInfo при сборке
  (VersionUpdate.targets), а сюда он приходит из FileVersion собранного enviriot.exe.

.PARAMETER OutDir
  Каталог результата. По умолчанию Output\release - он уже покрыт правилом /Output/*
  в .gitignore.
#>
[CmdletBinding()]
param(
  [string]$OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'Output\release' }

# MSBuild, а не dotnet build: проекты нацелены на net48 и собираются штатным
# инструментарием Visual Studio.
function Find-MSBuild {
  $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (Test-Path -LiteralPath $vswhere) {
    $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                        -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ($found) { return $found }
  }

  throw 'MSBuild не найден: запустите скрипт из Developer PowerShell либо установите Visual Studio / Build Tools.'
}

# Копирует дерево файлов, оставляя те, для которых $Keep вернул $true.
# $Keep получает путь относительно $Source.
function Copy-Filtered {
  param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][scriptblock]$Keep
  )

  $prefix = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\') + '\'
  $copied = 0

  foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
    $rel = $file.FullName.Substring($prefix.Length)
    if (-not (& $Keep $rel)) { continue }

    $target = Join-Path $Destination $rel
    $dir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $dir)) {
      New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    $copied++
  }

  return $copied
}

# --- Сборка ------------------------------------------------------------------

$solution = Join-Path $root 'EnviriotSW.sln'
$binRelease = Join-Path $root 'Output\bin_r'

# Каталог сносится целиком: MEF грузит плагины сканированием своей папки, и сборка
# от удалённого плагина, оставшаяся с прошлого раза, попала бы в релиз.
if (Test-Path -LiteralPath $binRelease) {
  Remove-Item -LiteralPath $binRelease -Recurse -Force
}

$msbuild = Find-MSBuild
Write-Host "MSBuild: $msbuild"

# -restore нужен и для PackageReference, и для NuGet-резолвера SDK
# Microsoft.Build.NoTargets, на котором держится Output\www\WebUI.www.csproj.
& $msbuild $solution -restore -p:Configuration=Release -m -v:m
if ($LASTEXITCODE -ne 0) { throw "MSBuild завершился с кодом $LASTEXITCODE" }

# --- Версия ------------------------------------------------------------------

$exe = Join-Path $binRelease 'enviriot.exe'
if (-not (Test-Path -LiteralPath $exe)) {
  throw "После сборки нет $exe - проверьте вывод MSBuild."
}

$version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if (-not $version) { throw "У $exe нет FileVersion." }

# --- Раскладка ---------------------------------------------------------------

if (Test-Path -LiteralPath $OutDir) {
  Remove-Item -LiteralPath $OutDir -Recurse -Force
}
$stage = Join-Path $OutDir 'enviriot'
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$binCount = Copy-Filtered -Source $binRelease -Destination (Join-Path $stage 'bin') -Keep {
  param($rel)
  [System.IO.Path]::GetExtension($rel) -ne '.pdb'
}

$wwwCount = Copy-Filtered -Source (Join-Path $root 'Output\www') -Destination (Join-Path $stage 'www') -Keep {
  param($rel)

  $parts = $rel -split '\\'
  # Сборка Release затрагивает и WebUI.www.csproj, поэтому bin\ и obj\ появляются
  # внутри Output\www не только локально, но и на чистом раннере.
  for ($i = 0; $i -lt $parts.Length - 1; $i++) {
    if ($parts[$i] -in 'bin', 'obj') { return $false }
  }
  if ($parts[-1] -like 'WebUI.www.csproj*') { return $false }

  return $true
}

# --- Архив -------------------------------------------------------------------

# Имена записей задаются вручную, а не через Compress-Archive или CreateFromDirectory:
# под Windows PowerShell 5.1 обе обёртки пишут разделитель "\", и такой архив на Linux
# распаковывается в файлы с "\" в имени вместо дерева каталогов. ZIP требует "/",
# и здесь путь нормализуется явно - архив одинаков и локально, и на раннере.
# Только Windows PowerShell 5.1: в PowerShell 7, которым скрипт запускается на раннере,
# эти типы доступны сразу, а сборки System.IO.Compression.FileSystem там уже нет.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
  Add-Type -AssemblyName System.IO.Compression            # ZipArchive, ZipArchiveMode
  Add-Type -AssemblyName System.IO.Compression.FileSystem # ZipFileExtensions
}

$zip = Join-Path $OutDir "enviriot_v$version.zip"
$prefix = (Resolve-Path -LiteralPath $OutDir).Path.TrimEnd('\') + '\'

$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::Create)
try {
  $archive = New-Object System.IO.Compression.ZipArchive(
    $stream, [System.IO.Compression.ZipArchiveMode]::Create)
  try {
    # Перечисляется $stage, а zip лежит рядом в $OutDir, поэтому в себя не попадает.
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
      $entry = $file.FullName.Substring($prefix.Length).Replace('\', '/')
      [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $archive, $file.FullName, $entry,
        [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
  } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

$sizeMb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2)
Write-Host ""
Write-Host "Версия : $version"
Write-Host "bin    : $binCount файлов"
Write-Host "www    : $wwwCount файлов"
Write-Host "Архив  : $zip ($sizeMb МБ)"

if ($env:GITHUB_OUTPUT) {
  "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
  "zip=$zip"         | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
