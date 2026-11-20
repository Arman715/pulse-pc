# Сборка «Пульс ПК».
#   .\build.ps1          - итоговая версия в папку app (требует прав администратора при запуске)
#   .\build.ps1 -Preview - версия для просмотра без прав администратора (ничего не может менять)
param([switch]$Preview)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'PulsePC.csproj'
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# Промежуточные файлы удаляем всегда: иначе при смене режима остаётся старый манифест прав.
Remove-Item (Join-Path $env:LOCALAPPDATA 'PulsePC-build\obj') -Recurse -Force -ErrorAction SilentlyContinue
Get-Process PulsePC -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if ($Preview) {
    dotnet build $proj -c Release -p:NoElevate=true --nologo -v q
    Write-Host "Версия для просмотра: $env:LOCALAPPDATA\PulsePC-build\bin\PulsePC\release\PulsePC.exe"
} else {
    $out = Join-Path $here 'app'
    dotnet publish $proj -c Release -o $out --nologo -v q -p:DebugType=none
    Write-Host "Готово: $out\PulsePC.exe"
}
