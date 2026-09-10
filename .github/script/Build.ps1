<#
.SYNOPSIS
  Единая точка сборки Enviriot: Debug с тестами либо Release с дистрибутивом.

.DESCRIPTION
  Скрипт вызывается и вручную, и из .github\workflows, чтобы локальная и CI-сборка делали
  ровно одно и то же. Оба режима - полная пересборка (-t:Rebuild), а не инкрементальная:
  разница между "у меня собирается" и "на раннере нет" почти всегда и есть чей-то уцелевший
  промежуточный результат.

  Test (по умолчанию) - Debug, затем прогон X13.Tests через vstest.console.
      Режим разработчика и проверки коммита: .github\workflows\ci.yml.

  Pack - Release, затем раскладка и zip. Режим выпуска: .github\workflows\release.yml.

      enviriot\bin   <- Output\bin_r, без *.pdb
      enviriot\www   <- Output\www,   без WebUI.www.csproj, без папок bin и obj

  Нумерация у режимов разная, и считают её разные места.

  Debug-номер выставляет UpdateVersionInfo (VersionUpdate.targets): локальный счётчик сборок
  за сегодня, +1 за запуск. Он ничего не значит на другой машине - и не должен: чтобы отличить
  сборку от сборки между машинами, хеш коммита кладётся отдельно, в AssemblyMetadata("Commit").

  Релизный номер считается здесь, в Get-ReleaseVersion, и передаётся в MSBuild через
  -p:BuildVersion. Берётся он от последнего тега выпуска, а не из файла: теги пушатся вместе
  с релизом и потому общие для всех машин, тогда как файл приходилось коммитить обратно в
  ветку - с конфликтами при слиянии dev в master и коммитом от бота посреди выпуска.

.PARAMETER Mode
  Test - Debug и тесты; Pack - Release и архив.

.PARAMETER OutDir
  Только для Pack: каталог результата. По умолчанию Output\release - он уже покрыт
  правилом /Output/* в .gitignore.

.EXAMPLE
  .github\script\Build.ps1
  Пересобрать Debug и прогнать тесты.

.EXAMPLE
  .github\script\Build.ps1 -Mode Pack
  Пересобрать Release и собрать архив в Output\release.
#>
[CmdletBinding()]
param(
  [ValidateSet('Test', 'Pack')]
  [string]$Mode = 'Test',
  [string]$OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $root 'EnviriotSW.sln'
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

# vstest.console из той же установки Visual Studio, а не dotnet test: тесты нацелены на net48
# и собраны тем же MSBuild, что и остальное решение.
function Find-VSTest {
  $cmd = Get-Command vstest.console -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (Test-Path -LiteralPath $vswhere) {
    $found = & $vswhere -latest -products * -find '**\TestPlatform\vstest.console.exe' | Select-Object -First 1
    if ($found) { return $found }
  }

  throw 'vstest.console.exe не найден: нужна установка Visual Studio с компонентом "Testing tools".'
}

# Полная пересборка одной конфигурации. Каталог вывода сносится целиком, потому что Rebuild
# чистит только то, что произвели ЭТИ проекты: сборка от удалённого плагина, оставшаяся с
# прошлого раза, пережила бы Clean, а MEF грузит плагины сканированием своей папки и подобрал
# бы её обратно - в том числе в релизный архив.
function Invoke-Build {
  param(
    [Parameter(Mandatory)][ValidateSet('Debug', 'Release')][string]$Configuration,
    [Parameter(Mandatory)][string]$BinDir,
    [string]$BuildVersion
  )

  if (Test-Path -LiteralPath $BinDir) {
    Remove-Item -LiteralPath $BinDir -Recurse -Force
  }

  $msbuild = Find-MSBuild
  Write-Host "MSBuild: $msbuild"

  # -restore нужен и для PackageReference, и для NuGet-резолвера SDK
  # Microsoft.Build.NoTargets, на котором держится Output\www\WebUI.www.csproj.
  $msbuildArgs = @($solution, '-restore', '-t:Rebuild', "-p:Configuration=$Configuration", '-m', '-v:m')
  # Без BuildVersion UpdateVersionInfo считает номер сам - локальным счётчиком.
  if ($BuildVersion) { $msbuildArgs += "-p:BuildVersion=$BuildVersion" }

  & $msbuild @msbuildArgs
  if ($LASTEXITCODE -ne 0) { throw "MSBuild завершился с кодом $LASTEXITCODE" }
}

# Номер версии не задаётся снаружи: его выставляет UpdateVersionInfo при сборке
# (VersionUpdate.targets), а сюда он приходит из FileVersion собранного enviriot.exe.
function Get-BuiltVersion {
  param([Parameter(Mandatory)][string]$BinDir)

  $exe = Join-Path $BinDir 'enviriot.exe'
  if (-not (Test-Path -LiteralPath $exe)) {
    throw "После сборки нет $exe - проверьте вывод MSBuild."
  }
  $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
  if (-not $version) { throw "У $exe нет FileVersion." }
  return $version
}

# git, у которого нечего спросить, - это не отказ сборки: нет git, нет репозитория, нет тегов.
# Все три случая означают "источника номера нет", поэтому здесь возвращается $null, а stderr
# гасится. ErrorActionPreference снимается на время вызова: под 'Stop' перенаправление stderr
# нативной команды в Windows PowerShell 5.1 само по себе поднимает NativeCommandError.
function Invoke-Git {
  param([Parameter(Mandatory)][string[]]$Arguments)

  if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }

  $saved = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    $output = & git @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return $output
  }
  catch { return $null }
  finally { $ErrorActionPreference = $saved }
}

# major.minor читается из VersionUpdate.targets - оттуда же, откуда его берёт MSBuild, и там же,
# где его правит человек. Второй копии числа в скрипте нет намеренно.
function Get-VersionBaseline {
  $targets = Join-Path $root 'VersionUpdate.targets'
  $xml = [xml](Get-Content -LiteralPath $targets -Raw)
  foreach ($group in $xml.Project.PropertyGroup) {
    if ($group.VersionBaseline) { return [string]$group.VersionBaseline }
  }
  throw "В $targets нет свойства VersionBaseline."
}

# Номер последнего выпуска. Теги - единственное состояние, общее для всех машин: они уезжают
# вместе с релизом, в отличие от файла, который для этого приходилось коммитить.
# Префикс разбирается терпимо, а не по TAG_PREFIX из release.yml: формат за годы менялся, и в
# репозитории лежат сразу 0.4.2307.19100, V.0.4.2607.1100, v0.4.2307.7300 и v.0.5.2609.8100.
function Get-LastReleaseVersion {
  $tags = Invoke-Git -Arguments @('tag', '--list')
  if (-not $tags) { return $null }

  $best = $null
  foreach ($tag in $tags) {
    if ($tag -match '^[vV]?\.?(\d+\.\d+\.\d+\.\d+)$') {
      $candidate = [version]$Matches[1]
      if ($null -eq $best -or $candidate -gt $best) { $best = $candidate }
    }
  }
  return $best
}

# Номер выпуска: строго больше уже выпущенного и всегда оканчивается на сотню.
#
# Пол - максимум из последнего тега и локального VersionInfo.cs: на машине разработчика файл
# мог уйти вперёд тега, на чистом раннере его просто нет. Кандидат - сегодняшняя дата со
# счётчиком 100. Если кандидат не старше пола - тот же день, или часы машины отстали - берётся
# пол, округлённый вверх до следующей сотни: строгое возрастание важнее, чем совпадение
# счётчика с датой.
function Get-ReleaseVersion {
  $baseline = Get-VersionBaseline
  $floor = Get-LastReleaseVersion

  $versionInfo = Join-Path $root 'Server\Properties\VersionInfo.cs'
  if (Test-Path -LiteralPath $versionInfo) {
    $text = Get-Content -LiteralPath $versionInfo -Raw
    if ($text -match '(?m)^\[assembly: AssemblyVersion\("(\d+\.\d+\.\d+\.\d+)"\)\]') {
      $local = [version]$Matches[1]
      if ($null -eq $floor -or $local -gt $floor) { $floor = $local }
    }
  }

  # База главнее пола по major.minor: так подъём 0.5 -> 0.6 в VersionUpdate.targets срабатывает
  # сразу, а не ждёт, пока его догонит номер из тега.
  $parts = $baseline.Split('.')
  $major = [int]$parts[0]
  $minor = [int]$parts[1]
  if ($null -ne $floor -and ($floor.Major -gt $major -or ($floor.Major -eq $major -and $floor.Minor -gt $minor))) {
    $major = $floor.Major
    $minor = $floor.Minor
  }

  $now = Get-Date
  $stamp = ($now.Year % 100) * 100 + $now.Month
  $candidate = [version]"$major.$minor.$stamp.$($now.Day * 1000 + 100)"

  if ($null -ne $floor -and $candidate -le $floor) {
    $next = ([int][math]::Floor($floor.Revision / 100)) * 100 + 100
    $candidate = [version]"$($floor.Major).$($floor.Minor).$($floor.Build).$next"
  }

  return $candidate.ToString()
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

# Значения для последующих шагов workflow; вне GitHub Actions переменной нет и писать некуда.
function Set-StepOutput {
  param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Value)

  if ($env:GITHUB_OUTPUT) {
    "$Name=$Value" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
  }
}

if ($Mode -eq 'Test') {

  # --- Debug и тесты -----------------------------------------------------------

  # X13.Tests собирается в Output\tests, а не в bin_d - см. комментарий в Tests\X13.Tests.csproj.
  # Каталог сносится целиком по той же причине, что и каталог сборки: vstest.console набирает
  # адаптеры сканированием папки, и адаптер фреймворка, на который проект больше не ссылается,
  # продолжал бы участвовать в прогоне. Переход с MSTest на NUnit оставил там ровно такой мусор -
  # Clean его не трогает, потому что произвели его уже не эти пакеты.
  $testsDir = Join-Path $root 'Output\tests'
  if (Test-Path -LiteralPath $testsDir) {
    Remove-Item -LiteralPath $testsDir -Recurse -Force
  }

  $binDebug = Join-Path $root 'Output\bin_d'
  Invoke-Build -Configuration Debug -BinDir $binDebug
  $version = Get-BuiltVersion -BinDir $binDebug

  $testsDll = Join-Path $testsDir 'X13.Tests.dll'
  if (-not (Test-Path -LiteralPath $testsDll)) {
    throw "После сборки нет $testsDll - проверьте, что Tests\X13.Tests.csproj входит в решение и собирается в Debug."
  }

  $results = Join-Path $testsDir 'TestResults'

  $vstest = Find-VSTest
  Write-Host "VSTest : $vstest"

  # trx пишется всегда: на раннере это единственный способ разглядеть, ЧТО именно упало, не
  # вычитывая лог сборки целиком - workflow прикладывает файл к запуску и при неудаче тоже.
  $trx = Join-Path $results 'X13.Tests.trx'
  & $vstest $testsDll "/ResultsDirectory:$results" '/Logger:trx;LogFileName=X13.Tests.trx'
  $testsExit = $LASTEXITCODE

  Write-Host ''
  Write-Host "Версия : $version"
  Write-Host "Отчёт  : $trx"

  # Выходы выставляются до проверки кода возврата: шаг с отчётом должен получить путь и
  # тогда, когда тесты упали, - именно в этом случае отчёт и нужен.
  Set-StepOutput -Name 'version' -Value $version
  Set-StepOutput -Name 'trx' -Value $trx

  if ($testsExit -ne 0) { throw "Тесты не прошли: vstest.console завершился с кодом $testsExit" }

} else {

  # --- Release и дистрибутив ---------------------------------------------------

  $binRelease = Join-Path $root 'Output\bin_r'
  $requested = Get-ReleaseVersion
  Write-Host "Версия : $requested (от последнего выпуска)"

  Invoke-Build -Configuration Release -BinDir $binRelease -BuildVersion $requested
  $version = Get-BuiltVersion -BinDir $binRelease

  # Сверка не формальная: если -p:BuildVersion не доехал до UpdateVersionInfo - переименовали
  # свойство, сломали цель - задача молча посчитает номер локальным счётчиком, и выпуск уедет
  # с чужим номером. После создания тега это уже не исправить.
  if ($version -ne $requested) {
    throw "Собрано $version, а запрошено $requested - проверьте цель UpdateVersion в VersionUpdate.targets."
  }

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
  Write-Host ''
  Write-Host "Версия : $version"
  Write-Host "bin    : $binCount файлов"
  Write-Host "www    : $wwwCount файлов"
  Write-Host "Архив  : $zip ($sizeMb МБ)"

  Set-StepOutput -Name 'version' -Value $version
  Set-StepOutput -Name 'zip' -Value $zip
}
