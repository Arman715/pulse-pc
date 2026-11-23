# Выпуск «Пульс ПК» из сборки GitHub Actions (.github\workflows\build.yml).
# Программу собирает GitHub из открытого кода; здесь файлы сборки скачиваются, обновление подписывается ключом
# с этого компьютера (build-release.ps1 -SignDir) и создаётся выпуск на GitHub. Ключ подписи в GitHub не попадает.
#
#   git tag v<версия>; git push origin v<версия>    - сначала: GitHub соберёт эту версию (версия — из PulsePC.csproj)
#   .\publish-release.ps1                            - скачать последнюю успешную сборку метки, подписать, показать команду выпуска
#   .\publish-release.ps1 -Publish                   - то же и сразу создать выпуск на GitHub
#   .\publish-release.ps1 -RunId 123456789           - взять конкретную сборку
param([long]$RunId, [switch]$Publish, [string]$Repo = 'Arman715/pulse-pc')

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'PulsePC.csproj'
$version = ([xml](Get-Content $proj -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'В PulsePC.csproj не указана версия (<Version>).' }
$tag = "v$version"

if (-not $RunId) {
    $id = gh run list --repo $Repo --workflow build.yml --branch $tag --status success --limit 1 --json databaseId --jq '.[0].databaseId'
    if ($LASTEXITCODE -ne 0) { throw 'gh не ответил: проверьте вход (gh auth status).' }
    if (-not $id) { throw "Нет успешной сборки метки $tag. Отправьте метку: git tag $tag; git push origin $tag — и дождитесь сборки." }
    $RunId = [long]$id
}

$dir = Join-Path $here "dist\$tag"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory $dir -Force | Out-Null
Write-Host "Скачиваю сборку $RunId ($tag)…"
gh run download $RunId --repo $Repo --name "PulsePC-$version" --dir $dir
if ($LASTEXITCODE -ne 0) { throw "Не удалось скачать файлы сборки $RunId." }

& (Join-Path $here 'build-release.ps1') -SignDir $dir

$setup = "PulsePC-Setup-$version.exe"
$portable = "PulsePC-Portable-$version.zip"
$files = @($setup, "$setup.sha256", "$setup.sig", 'update.json', $portable, "$portable.sha256") | ForEach-Object {
    $p = Join-Path $dir $_
    if (-not (Test-Path $p)) { throw "Нет файла $p" }
    $p
}
$notes = Join-Path $here 'release-notes.txt'
$ghArgs = @('release', 'create', $tag) + $files + @('--repo', $Repo, '--title', "Пульс ПК $version", '--notes-file', $notes, '--verify-tag')

if ($Publish) {
    gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw 'Выпуск не создан.' }
    Write-Host "Выпуск $tag создан: https://github.com/$Repo/releases/tag/$tag"
} else {
    Write-Host ''
    Write-Host "Готово к выпуску ($dir). Создать выпуск: .\publish-release.ps1 -RunId $RunId -Publish"
}
