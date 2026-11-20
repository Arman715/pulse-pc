# Сборка дистрибутива «Пульс ПК»: один файл dist\PulsePC-Setup-<версия>.exe (+ .sha256, .sig и update.json рядом).
#
# Что внутри. Сборок две:
#   1) установленная программа — однофайловый PulsePC.exe БЕЗ распаковки библиотек (IncludeNativeLibrariesForSelfExtract=false):
#      рядом с ним лежат его библиотеки WPF (*_cor3.dll). Установщик кладёт их в папку установки, закрытую от записи для всех,
#      кроме администраторов. Так программа, которую автозапуск запускает с правами администратора, ничего не распаковывает
#      в %TEMP%\.net (туда может писать любая программа пользователя, и подменённая DLL выполнилась бы с правами администратора).
#   2) установщик — та же программа, однофайловая, с распаковкой (чтобы её можно было скачать одним файлом) и со встроенным
#      архивом PulsePC.app.zip из сборки 1. По имени «PulsePC-Setup…» она запускается как установщик; удаление — PulsePC.exe --uninstall.
#
#   .\build-release.ps1              - дистрибутив в dist\ (требует прав администратора при запуске, как и программа; нужен ключ подписи)
#   .\build-release.ps1 -NoElevate   - то же без запроса прав, в папку -Out (только для проверки сборки, не для раздачи; подпись — если есть ключ)
#   .\build-release.ps1 -NoElevate -Out C:\temp\pulse -ReadyToRun   - проверить, что даёт ReadyToRun
#   .\build-release.ps1 -Notes 'Первое изменение', 'Второе'           - что нового (иначе строки из release-notes.txt рядом со скриптом)
#   .\build-release.ps1 -NewSigningKey                                - ОДИН РАЗ: создать ключ подписи и вписать открытую часть в Services\VersionCheck.cs
#
# Подпись. Закрытый ключ ECDSA P-256 лежит вне репозитория: %USERPROFILE%\.pulsepc\release-signing.key (другое место — -SigningKey).
# Подписывается текст «PulsePC-Setup\n<версия из 4 частей>\n<sha256 установщика>»; подпись (64 байта r||s, Base64) — в <файл>.sig.
# Программа новее 1.4.2 без верной подписи не обновляется. Выкладывать в выпуск GitHub все четыре файла: .exe, .exe.sha256, .exe.sig, update.json.
param([switch]$NoElevate, [string]$Out, [switch]$ReadyToRun, [string[]]$Notes, [switch]$NewSigningKey,
      [string]$SigningKey = (Join-Path $env:USERPROFILE '.pulsepc\release-signing.key'))

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'PulsePC.csproj'
$versionCheck = Join-Path $here 'Services\VersionCheck.cs'
$utf8 = New-Object System.Text.UTF8Encoding $false
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# ------------------------------------------------------------------ ключ подписи

function Assert-KeyOutsideRepo([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $repo = [IO.Path]::GetFullPath($here).TrimEnd('\') + '\'
    if ($full.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw "Ключ подписи не должен лежать в папке проекта: $full" }
    if ($full -match '\\OneDrive') { Write-Warning "Ключ подписи в папке OneDrive ($full) уходит в облако. Лучше %USERPROFILE%\.pulsepc." }
}

function Read-SigningKey([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $j = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($j.curve -ne 'nistP256' -or -not $j.d -or -not $j.x -or -not $j.y) { throw "Файл ключа $path испорчен." }
    $q = New-Object System.Security.Cryptography.ECPoint
    $q.X = [Convert]::FromBase64String($j.x)
    $q.Y = [Convert]::FromBase64String($j.y)
    $p = New-Object System.Security.Cryptography.ECParameters
    $p.Curve = [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256
    $p.Q = $q
    $p.D = [Convert]::FromBase64String($j.d)
    return [pscustomobject]@{ Ecdsa = [System.Security.Cryptography.ECDsa]::Create($p); X = $j.x; Y = $j.y }
}

# Открытый ключ, встроенный в программу (Services\VersionCheck.cs).
function Get-EmbeddedKey {
    $text = [IO.File]::ReadAllText($versionCheck)
    $x = [regex]::Match($text, 'SigningKeyX = "([^"]*)"').Groups[1].Value
    $y = [regex]::Match($text, 'SigningKeyY = "([^"]*)"').Groups[1].Value
    return [pscustomobject]@{ X = $x; Y = $y }
}

if ($NewSigningKey) {
    Assert-KeyOutsideRepo $SigningKey
    if (Test-Path $SigningKey) { throw "Ключ уже есть: $SigningKey. Новый ключ сделает невозможным обновление уже выпущенных версий — старый не перезаписываю." }
    $dir = Split-Path -Parent $SigningKey
    New-Item -ItemType Directory $dir -Force | Out-Null
    $ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $p = $ec.ExportParameters($true)
    $json = [ordered]@{ curve = 'nistP256'; d = [Convert]::ToBase64String($p.D); x = [Convert]::ToBase64String($p.Q.X); y = [Convert]::ToBase64String($p.Q.Y) } | ConvertTo-Json
    [IO.File]::WriteAllText($SigningKey, $json, $utf8)
    # Читать и менять файл ключа может только текущий пользователь.
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'Allow')))
    Set-Acl -Path $SigningKey -AclObject $acl
    # Открытая часть — в программу.
    $text = [IO.File]::ReadAllText($versionCheck)
    $text = [regex]::Replace($text, '(SigningKeyX = ")[^"]*(")', '${1}' + [Convert]::ToBase64String($p.Q.X) + '${2}')
    $text = [regex]::Replace($text, '(SigningKeyY = ")[^"]*(")', '${1}' + [Convert]::ToBase64String($p.Q.Y) + '${2}')
    [IO.File]::WriteAllText($versionCheck, $text, $utf8)
    $check = Get-EmbeddedKey
    if ($check.X -ne [Convert]::ToBase64String($p.Q.X) -or $check.Y -ne [Convert]::ToBase64String($p.Q.Y)) { throw "Не удалось вписать открытый ключ в $versionCheck — впишите вручную." }
    Write-Host "Ключ подписи создан: $SigningKey"
    Write-Host "Открытый ключ вписан в Services\VersionCheck.cs:"
    Write-Host ("  X = " + $check.X)
    Write-Host ("  Y = " + $check.Y)
    Write-Host "Сделайте копию файла ключа на внешний носитель: без него новые версии нельзя будет подписать, и программа их не примет."
    return
}

# ------------------------------------------------------------------ сборка

$version = ([xml](Get-Content $proj -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'В PulsePC.csproj не указана версия (<Version>).' }
$v = [version]$version
$version4 = '{0}.{1}.{2}.{3}' -f $v.Major, $v.Minor, [Math]::Max($v.Build, 0), [Math]::Max($v.Revision, 0)

# Подпись: для дистрибутива обязательна, ключ должен совпадать со встроенным в программу.
Assert-KeyOutsideRepo $SigningKey
$key = Read-SigningKey $SigningKey
$embedded = Get-EmbeddedKey
$release = -not ($NoElevate -or $Out)
if ($release -and -not $key) { throw "Нет ключа подписи $SigningKey. Один раз выполните: .\build-release.ps1 -NewSigningKey" }
if ($key -and ($embedded.X -ne $key.X -or $embedded.Y -ne $key.Y)) {
    throw "Открытый ключ в Services\VersionCheck.cs не совпадает с $SigningKey — программа отвергла бы такую подпись."
}

# Свои промежуточные папки: не мешаем обычной сборке (PulsePC-build) и не тащим манифест прав из другого режима.
$mode = if ($NoElevate) { 'noelevate' } else { 'release' }
if ($ReadyToRun) { $mode += '-r2r' }
$artifacts = Join-Path $env:LOCALAPPDATA "PulsePC-release\$mode"
Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue

function Publish([string]$name, [bool]$extractNatives, [string[]]$more) {
    $out = Join-Path $artifacts "$name-out"
    $props = @(
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        "-p:IncludeNativeLibrariesForSelfExtract=$($extractNatives.ToString().ToLowerInvariant())",
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none',
        # Обрезку не включаем: WPF и Windows Forms её не поддерживают.
        '-p:PublishTrimmed=false',
        "-p:ArtifactsPath=$(Join-Path $artifacts $name)",
        '-o', $out, '--nologo', '-v', 'q'
    )
    if ($NoElevate) { $props += '-p:NoElevate=true' }
    if ($ReadyToRun) { $props += '-p:PublishReadyToRun=true' }
    if ($more) { $props += $more }
    dotnet publish $proj @props | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($name) завершился с кодом $LASTEXITCODE" }
    if (-not (Test-Path (Join-Path $out 'PulsePC.exe'))) { throw "Нет $out\PulsePC.exe" }
    return $out
}

# 1. Установленная программа: exe и библиотеки рядом, ничего не распаковывается при запуске.
Write-Host "Пульс ПК $version — программа для установки ($mode)…"
$appOut = Publish 'app' $false @()
$appFiles = @(Get-ChildItem $appOut -File | Where-Object { $_.Extension -in '.exe', '.dll' })
$other = @(Get-ChildItem $appOut -File | Where-Object { $_.Extension -notin '.exe', '.dll' })
if ($other) { Write-Warning ("В установленную программу не входят файлы: " + ($other.Name -join ', ')) }
foreach ($f in $appFiles) { if ($f.Name -notmatch '^[A-Za-z0-9._-]+\.(exe|dll)$') { throw "Имя файла $($f.Name) установщик не примет." } }
Write-Host ("  файлы: " + (($appFiles | ForEach-Object { $_.Name }) -join ', '))

try { Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop } catch { }
try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop } catch { }
$payload = Join-Path $artifacts 'PulsePC.app.zip'
$zip = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $appFiles) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $f.Name, [IO.Compression.CompressionLevel]::Optimal) }
} finally { $zip.Dispose() }

# 2. Установщик: один файл, внутри — архив из шага 1.
Write-Host "Пульс ПК $version — установщик…"
$publish = Publish 'setup' $true @("-p:AppPayload=$payload")
$exe = Join-Path $publish 'PulsePC.exe'
$extra = Get-ChildItem $publish -File | Where-Object { $_.Name -ne 'PulsePC.exe' }
if ($extra) { Write-Warning ("Кроме exe в публикации установщика лежат файлы: " + ($extra.Name -join ', ')) }

if (-not $release) {
    $dir = if ($Out) { $Out } else { Join-Path $artifacts 'check' }
    New-Item -ItemType Directory $dir -Force | Out-Null
    $target = Join-Path $dir "PulsePC-Setup-$version.exe"
} else {
    $dist = Join-Path $here 'dist'
    New-Item -ItemType Directory $dist -Force | Out-Null
    $target = Join-Path $dist "PulsePC-Setup-$version.exe"
}
Copy-Item $exe $target -Force
$mb = (Get-Item $target).Length / 1MB
Write-Host ("Готово: {0}  ({1:N1} МБ)" -f $target, $mb)

# Обновление программы: контрольная сумма, подпись и манифест рядом с установщиком (UTF-8 без BOM).
$name = Split-Path $target -Leaf
$hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$target.sha256", "$hash  $name`n", $utf8)

$sigB64 = $null
if ($key) {
    $message = [Text.Encoding]::UTF8.GetBytes("PulsePC-Setup`n$version4`n$hash")
    $sig = $key.Ecdsa.SignData($message, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    if ($sig.Length -ne 64) { throw "Подпись неожиданной длины ($($sig.Length) байт вместо 64)." }
    # Проверка открытым ключом, встроенным в программу, — так же, как её проверит программа.
    $pub = New-Object System.Security.Cryptography.ECParameters
    $pub.Curve = [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256
    $pq = New-Object System.Security.Cryptography.ECPoint
    $pq.X = [Convert]::FromBase64String($embedded.X); $pq.Y = [Convert]::FromBase64String($embedded.Y)
    $pub.Q = $pq
    $verifier = [System.Security.Cryptography.ECDsa]::Create($pub)
    if (-not $verifier.VerifyData($message, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) { throw 'Подпись не проходит проверку встроенным ключом.' }
    $sigB64 = [Convert]::ToBase64String($sig)
    [IO.File]::WriteAllText("$target.sig", "$sigB64`n", $utf8)
    Write-Host "Подпись: $name.sig (версия $version4)"
} else {
    Remove-Item "$target.sig" -ErrorAction SilentlyContinue
    Write-Warning "Без подписи: ключа $SigningKey нет. Программа новее 1.4.2 такой установщик не примет."
}

$noteList = @()
if ($Notes) { $noteList = @($Notes | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) }
else {
    $notesFile = Join-Path $here 'release-notes.txt'
    if (Test-Path $notesFile) { $noteList = @(Get-Content $notesFile -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
}
$manifest = [ordered]@{ version = "$version"; url = $name; sha256 = $hash; date = (Get-Date -Format 'yyyy-MM-dd'); notes = $noteList }
if ($sigB64) { $manifest.sig = $sigB64 }
$manifestPath = Join-Path (Split-Path $target -Parent) 'update.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 3), $utf8)
Write-Host "SHA-256: $hash"
Write-Host "Для обновления рядом: $name.sha256, $name.sig и update.json"
if ($release) {
    Write-Host ""
    Write-Host "Выпуск на GitHub (все четыре файла):"
    Write-Host "  gh release create v$version `"$target`" `"$target.sha256`" `"$target.sig`" `"$manifestPath`" --repo Arman715/pulse-pc --title `"Пульс ПК $version`" --notes-file release-notes.txt"
}
