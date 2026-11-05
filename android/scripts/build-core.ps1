# Собирает ядро (xray внутри обёртки android\core-go) в app\libs\libcup.aar через gomobile.
# Нужны Go, gomobile (go install golang.org/x/mobile/cmd/gomobile@<версия из go.mod>), Android SDK и NDK.
#Requires -Version 5.1
[CmdletBinding()]
param(
    # Эмулятор — android/amd64; приставки — android/arm64 и android/arm
    [string[]]$Targets = @('android/arm64', 'android/arm', 'android/amd64')
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$android = Resolve-Path (Join-Path $scriptDir '..')
$coreGo = Join-Path $android 'core-go'
$out = Join-Path $android 'app\libs\libcup.aar'

if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
if (-not $env:ANDROID_NDK_HOME) {
    $ndk = Get-ChildItem (Join-Path $env:ANDROID_HOME 'ndk') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } | Select-Object -Last 1
    if (-not $ndk) { throw "NDK не найден в $env:ANDROID_HOME\ndk (sdkmanager ""ndk;<версия>"")" }
    $env:ANDROID_NDK_HOME = $ndk.FullName
}
$goBin = Join-Path $env:USERPROFILE 'go\bin'
if (Test-Path 'C:\Program Files\Go\bin') { $env:Path = "C:\Program Files\Go\bin;$env:Path" }
$env:Path = "$goBin;$env:Path"
if (-not $env:JAVA_HOME) {
    $jdk = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -Filter 'jdk-21*' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($jdk) { $env:JAVA_HOME = $jdk.FullName }
}

New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
Write-Host "NDK: $env:ANDROID_NDK_HOME"
Write-Host "Цели: $($Targets -join ', ')"
Push-Location $coreGo
try {
    $targetArg = '-target=' + ($Targets -join ',')
    # -checklinkname=0: github.com/wlynxg/anet (сетевые интерфейсы на Android, его тянет xray) ссылается на
    # net.zoneCache через linkname, а Go с 1.23 такие ссылки без флага не пропускает.
    $ldflags = '-ldflags=-s -w -buildid= -checklinkname=0'
    & gomobile bind $targetArg -androidapi 24 -trimpath $ldflags -o $out .
    if ($LASTEXITCODE -ne 0) { throw "gomobile bind завершился с кодом $LASTEXITCODE" }
}
finally {
    Pop-Location
}
# Исходники Java-обёртки gomobile кладёт рядом — для сборки не нужны
Remove-Item (Join-Path (Split-Path $out) 'libcup-sources.jar') -ErrorAction SilentlyContinue
$size = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "Готово: $out ($size МБ)"
