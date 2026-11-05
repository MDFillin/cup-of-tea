# Создаёт ключ подписи релизов ТВ-версии: %USERPROFILE%\.cupoftea\release.jks и keystore.properties со случайным паролем.
# Один раз на всю жизнь программы. Сделайте резервную копию папки .cupoftea: без этого ключа обновления на ТВ
# поверх установленной версии не встанут (Android требует ту же подпись).
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:USERPROFILE '.cupoftea'
$jks = Join-Path $dir 'release.jks'
$props = Join-Path $dir 'keystore.properties'
if (Test-Path $jks) { throw "Ключ уже есть: $jks — второй создавать нельзя (старые установки не обновятся)" }
New-Item -ItemType Directory -Force $dir | Out-Null

if (-not $env:JAVA_HOME) {
    $jdk = Get-ChildItem 'C:\Program Files\Eclipse Adoptium' -Directory -Filter 'jdk-21*' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($jdk) { $env:JAVA_HOME = $jdk.FullName }
}
$keytool = Join-Path $env:JAVA_HOME 'bin\keytool.exe'

$bytes = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B').Replace('=', '')

& $keytool -genkeypair -v -keystore $jks -storetype PKCS12 -alias cupoftea-tv -keyalg RSA -keysize 4096 -validity 36500 `
    -storepass $password -keypass $password -dname 'CN=The Cup of Tea, O=MDFillin' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "keytool завершился с кодом $LASTEXITCODE" }

@(
    "storeFile=$($jks.Replace('\', '/'))"
    "storePassword=$password"
    'keyAlias=cupoftea-tv'
    "keyPassword=$password"
) | Set-Content $props -Encoding ascii

Write-Host "Ключ создан: $jks"
Write-Host "Пароль — в $props (не публикуйте и не коммитьте)."
Write-Host "Сделайте резервную копию папки $dir (например, на флешку или в менеджер паролей)."
