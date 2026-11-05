# Релизная сборка ТВ-версии: подписанные APK по архитектурам в client\out\CupOfTea-TV-<версия>-<abi>.apk.
# Перед первой сборкой — scripts\new-release-key.ps1 (ключ подписи) и scripts\build-core.ps1 (ядро xray).
#Requires -Version 5.1
[CmdletBinding()]
param(
    # Пересобрать ядро xray (иначе берётся готовое app\libs\libcup.aar)
    [switch]$Core
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$android = (Resolve-Path (Join-Path $scriptDir '..')).Path
$out = Join-Path (Split-Path $android) 'out'

if (-not (Test-Path (Join-Path $env:USERPROFILE '.cupoftea\keystore.properties'))) {
    throw 'Нет ключа подписи: запустите scripts\new-release-key.ps1'
}
if ($Core -or -not (Test-Path (Join-Path $android 'app\libs\libcup.aar'))) {
    & (Join-Path $scriptDir 'build-core.ps1')
}
if (-not $env:JAVA_HOME) {
    $jdk = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -Filter 'jdk-21*' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($jdk) { $env:JAVA_HOME = $jdk.FullName }
}

$version = (Select-String -Path (Join-Path $android 'app\build.gradle.kts') -Pattern 'val tvVersion = "(.+)"').Matches[0].Groups[1].Value
Write-Host "The Cup of Tea для Android TV $version"

Push-Location $android
try {
    & .\gradlew.bat :core:test :app:assembleRelease --console=plain
    if ($LASTEXITCODE -ne 0) { throw "Gradle завершился с кодом $LASTEXITCODE" }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force $out | Out-Null
$apks = Get-ChildItem (Join-Path $android 'app\build\outputs\apk\release') -Filter 'app-*-release.apk'
if (-not $apks) { throw 'Подписанных APK нет — проверьте keystore.properties' }
foreach ($apk in $apks) {
    $abi = $apk.Name -replace '^app-(.+)-release\.apk$', '$1'
    $target = Join-Path $out "CupOfTea-TV-$version-$abi.apk"
    Copy-Item $apk.FullName $target -Force
    $sha = (Get-FileHash $target -Algorithm SHA256).Hash.ToLower()
    Write-Host ("{0}  {1:N1} МБ  sha256 {2}" -f (Split-Path $target -Leaf), ((Get-Item $target).Length / 1MB), $sha)
}
