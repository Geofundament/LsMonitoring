#Requires -Version 7
<#
.SYNOPSIS
  Собирает LS Monitoring для установщика: тесты, программа, Telegram-бот и gotify-server.exe в одну папку.

.DESCRIPTION
  Вызывается из GfDistribution/release.ps1 -App ls — на машине владельца и в CI (release.yml).
  Токен общего бота и служебная почта вшиваются из переменных окружения (секреты Actions):
  LS_TELEGRAM_BOT_TOKEN, LS_SMTP_HOST, LS_SMTP_PORT, LS_SMTP_SSL, LS_SMTP_FROM, LS_SMTP_USER, LS_SMTP_PASSWORD.
  Без переменных используются уже лежащие *.Local.cs; в CI их отсутствие — ошибка.
#>
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$secrets = Join-Path $root 'LsMonitoring.Core/Configuration'
$app = Join-Path $root 'LsMonitoring.Avalonia/LsMonitoring.Avalonia.csproj'
$version = ([xml](Get-Content $app -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

function Assert-Success([string]$What) {
    if ($LASTEXITCODE -ne 0) { throw "$What завершилась с кодом $LASTEXITCODE." }
}

function Assert-Secret([string]$File, [string]$What) {
    if (Test-Path (Join-Path $secrets $File)) { return }
    if ($env:CI) { throw "Нет секретов для «$What» (переменные LS_* в секретах Actions)." }
    Write-Warning "Сборка без «$What»: нет $File и переменных окружения."
}

if ($env:LS_TELEGRAM_BOT_TOKEN) {
    if ($env:LS_TELEGRAM_BOT_TOKEN -notmatch '^\d+:[A-Za-z0-9_-]+$') { throw 'LS_TELEGRAM_BOT_TOKEN в неожиданном формате.' }
    @"
namespace LsMonitoring.Core.Configuration;
public static partial class TelegramSecrets
{
    static partial void GetEmbeddedBotToken(ref string token) => token = "$($env:LS_TELEGRAM_BOT_TOKEN)";
}
"@ | Set-Content (Join-Path $secrets 'TelegramSecrets.Local.cs') -Encoding UTF8
}
Assert-Secret 'TelegramSecrets.Local.cs' 'Telegram-бот'

if ($env:LS_SMTP_HOST -and $env:LS_SMTP_FROM -and $env:LS_SMTP_PASSWORD) {
    # Одним Base64-блоком: спецсимволы пароля не надо экранировать в C#.
    $transport = [ordered]@{
        Host = $env:LS_SMTP_HOST
        Port = if ($env:LS_SMTP_PORT) { [int]$env:LS_SMTP_PORT } else { 587 }
        UseSsl = if ($env:LS_SMTP_SSL) { [bool]::Parse($env:LS_SMTP_SSL) } else { $true }
        From = $env:LS_SMTP_FROM
        Username = $env:LS_SMTP_USER
        Password = $env:LS_SMTP_PASSWORD
    }
    $blob = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($transport | ConvertTo-Json -Compress)))
    @"
namespace LsMonitoring.Core.Configuration;
public static partial class EmailSecrets
{
    static partial void GetEmbeddedEmailConfigBase64(ref string value) => value = "$blob";
}
"@ | Set-Content (Join-Path $secrets 'EmailSecrets.Local.cs') -Encoding UTF8
}
Assert-Secret 'EmailSecrets.Local.cs' 'служебная почта'

Write-Host 'Тесты LS Monitoring…'
dotnet test (Join-Path $root 'LsMonitoring.Core.Tests/LsMonitoring.Core.Tests.csproj') -c Release
Assert-Success 'Тесты'

$publish = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false', "-p:Version=$version")
dotnet publish $app @publish -o $OutputDirectory
Assert-Success 'Сборка программы'

$bot = Join-Path ([IO.Path]::GetTempPath()) "ls-bot-$version"
dotnet publish (Join-Path $root 'LsMonitoring.TelegramBot/LsMonitoring.TelegramBot.csproj') @publish -p:PublishSingleFile=true -o $bot
Assert-Success 'Сборка Telegram-бота'
Copy-Item (Join-Path $bot 'LsMonitoring.TelegramBot.exe') $OutputDirectory -Force
Remove-Item $bot -Recurse -Force

# Версия и хэши Gotify — одни на программу и сборку: берутся из LocalGotifyService.cs.
$gotifySource = Get-Content (Join-Path $root 'LsMonitoring.Core/LocalServices/LocalGotifyService.cs') -Raw
$constant = { param($name) [regex]::Match($gotifySource, "$name = `"([^`"]+)`"").Groups[1].Value }
$gotifyVersion = & $constant 'GotifyVersion'
$zipSha = & $constant 'GotifyZipSha256'
$exeSha = & $constant 'GotifyExeSha256'
# Копия, которую программа уже скачала и проверила на этой машине, годится, если хэш тот же: Защитник Windows
# часто не даёт скачать архив Gotify заново (считает Go-сборки нежелательными).
$cached = Join-Path $env:LOCALAPPDATA 'LS Monitoring/tools/gotify-server.exe'
if ((Test-Path $cached) -and (Get-FileHash $cached -Algorithm SHA256).Hash -eq $exeSha) {
    Copy-Item $cached (Join-Path $OutputDirectory 'gotify-server.exe') -Force
} else {
    $zip = Join-Path ([IO.Path]::GetTempPath()) "gotify-$gotifyVersion.zip"
    Invoke-WebRequest "https://github.com/gotify/server/releases/download/$gotifyVersion/gotify-windows-amd64.exe.zip" -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $zipSha) { throw "Архив Gotify $gotifyVersion не совпал по SHA-256." }
    $extract = "$zip.d"
    Expand-Archive $zip $extract -Force
    $exe = Get-ChildItem $extract -Filter '*.exe' -Recurse | Select-Object -First 1
    if (-not $exe -or (Get-FileHash $exe.FullName -Algorithm SHA256).Hash -ne $exeSha) { throw "gotify-server.exe $gotifyVersion не совпал по SHA-256." }
    Copy-Item $exe.FullName (Join-Path $OutputDirectory 'gotify-server.exe') -Force
    Remove-Item $zip, $extract -Recurse -Force
}

Write-Host "LS Monitoring $version собрана в $OutputDirectory"
