# Скачивает официальные сборки ядер с GitHub, проверяет SHA256 и кладёт exe в cores\.
# Версии и хэши закреплены здесь: обновление ядра = правка этого файла.
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Destination
)
$ErrorActionPreference = 'Stop'
# При запуске через powershell -File значение по умолчанию из $PSScriptRoot бывает пустым.
if (-not $Destination) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Destination = Join-Path $scriptDir '..\cores'
}
$ProgressPreference = 'SilentlyContinue'

$cores = @(
    @{
        Name    = 'sing-box'
        Version = '1.14.4'
        Url     = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip'
        Sha256  = '99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df'
        Files   = @('sing-box.exe', 'LICENSE')
    },
    @{
        Name    = 'xray'
        Version = '26.10.21'
        Url     = 'https://github.com/XTLS/Xray-core/releases/download/v26.10.21/Xray-windows-64.zip'
        Sha256  = 'fc58c39ee2f2717b4c54d28d374362389e5319b20ae44aea76ea45a5243ec827'
        Files   = @('xray.exe', 'LICENSE')
    }
)

New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("pvpn-cores-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null

try {
    $versions = @()
    foreach ($c in $cores) {
        $zip = Join-Path $work "$($c.Name).zip"
        Write-Host "Скачиваю $($c.Name) $($c.Version)..."
        Invoke-WebRequest -Uri $c.Url -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $c.Sha256) {
            throw "SHA256 не совпадает для $($c.Name): ожидалось $($c.Sha256), получено $hash"
        }
        $unpacked = Join-Path $work $c.Name
        Expand-Archive $zip -DestinationPath $unpacked
        foreach ($f in $c.Files) {
            $src = Get-ChildItem $unpacked -Recurse -File -Filter $f | Select-Object -First 1
            if (-not $src) { throw "В архиве $($c.Name) нет файла $f" }
            $target = if ($f -eq 'LICENSE') { "LICENSE-$($c.Name).txt" } else { $f }
            Copy-Item $src.FullName (Join-Path $Destination $target) -Force
        }
        $versions += "$($c.Name) $($c.Version) sha256(zip)=$($c.Sha256)"
        Write-Host "  OK, SHA256 совпадает"
    }
    $versions | Set-Content (Join-Path $Destination 'VERSIONS.txt') -Encoding utf8
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Ядра в $Destination"
