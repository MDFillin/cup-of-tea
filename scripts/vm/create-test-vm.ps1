# Создаёт тестовую ВМ Windows 11 в VirtualBox и запускает автоматическую установку.
# Сеть — мост через физический адаптер хоста: ВМ выходит в интернет сама, мимо VPN на хосте.
# Пароли тестовых учётных записей генерируются и сохраняются в <BaseFolder>\<Name>.credentials.json
# (доступ только у текущего пользователя). Внутри ВМ:
#   pvpn       — администратор с UAC (как учётная запись на основном ПК), автовход;
#   pvpnadmin  — встроенный администратор (SID -500), для установки службы через guestcontrol.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Iso,
    [string]$Name = 'PVpn-Test-Win11',
    [string]$BaseFolder = 'D:\vm',
    [string]$BridgeAdapter,
    [int]$MemoryMB = 4096,
    [int]$Cpus = 4,
    [int]$DiskGB = 64,
    [string]$EditionPattern = 'Pro'
)
$ErrorActionPreference = 'Stop'
$vbm = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle\VirtualBox').InstallDir 'VBoxManage.exe'
function VBox { & $vbm @args; if ($LASTEXITCODE -ne 0) { throw "VBoxManage $($args[0]) завершился с кодом $LASTEXITCODE" } }

if ((& $vbm list vms) -match [regex]::Escape("`"$Name`"")) { throw "ВМ $Name уже существует" }

if (-not $BridgeAdapter) {
    # Физический адаптер с маршрутом по умолчанию (тот же выбор, что делает клиент).
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' |
        Where-Object { (Get-NetAdapter -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue).HardwareInterface } |
        Sort-Object { $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4).InterfaceMetric } |
        Select-Object -First 1
    $BridgeAdapter = (Get-NetAdapter -InterfaceIndex $route.ifIndex).InterfaceDescription
}
Write-Host "Мост через: $BridgeAdapter"

# Выпуск Windows из образа
$detect = & $vbm unattended detect --iso=$Iso --machine-readable
# VirtualBox 7.2: ImageIndex4="Windows 11 Pro (10.0.26300.9457 / x64 / ru-RU)"
$images = $detect | Select-String '^ImageIndex(\d+)(?:Name)?="(.*)"$' | ForEach-Object { [pscustomobject]@{ Index = [int]$_.Matches[0].Groups[1].Value; Name = $_.Matches[0].Groups[2].Value } }
$image = $images | Where-Object Name -match $EditionPattern | Select-Object -First 1
if (-not $image) { throw "В образе нет выпуска '$EditionPattern': $($images.Name -join '; ')" }
Write-Host "Выпуск: $($image.Name) (индекс $($image.Index))"

# Учётные данные тестовой ВМ
function New-Password { -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 20 | ForEach-Object { [char]$_ }) }
New-Item -ItemType Directory -Force $BaseFolder | Out-Null
$credFile = Join-Path $BaseFolder "$Name.credentials.json"
$cred = [pscustomobject]@{ user = 'pvpn'; userPassword = (New-Password); admin = 'pvpnadmin'; adminPassword = (New-Password) }
Remove-Item $credFile -Force -ErrorAction SilentlyContinue   # от прошлой неудачной попытки (ВМ при этом нет — проверено выше)
$cred | ConvertTo-Json | Set-Content $credFile -Encoding utf8
# По SID: $env:USERNAME бывает пустым, а имя SYSTEM локализовано.
$mySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $credFile /inheritance:r /grant:r "*${mySid}:F" /grant:r '*S-1-5-18:F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls не смог ограничить доступ к $credFile" }

$userPassFile = Join-Path $BaseFolder "$Name.userpass.tmp"
$adminPassFile = Join-Path $BaseFolder "$Name.adminpass.tmp"
[IO.File]::WriteAllText($userPassFile, $cred.userPassword)
[IO.File]::WriteAllText($adminPassFile, $cred.adminPassword)

try {
    VBox createvm --name $Name --ostype Windows11_64 --basefolder $BaseFolder --register
    VBox modifyvm $Name --memory $MemoryMB --cpus $Cpus --firmware efi --tpm-type 2.0 `
        --graphicscontroller vboxsvga --vram 128 --audio-enabled off --usb-ohci off `
        --nic1 bridged --bridge-adapter1 $BridgeAdapter --nic-type1 82540EM `
        --clipboard-mode disabled --drag-and-drop disabled
    $disk = Join-Path $BaseFolder "$Name\$Name.vdi"
    VBox createmedium disk --filename $disk --size ($DiskGB * 1024) --format VDI
    VBox storagectl $Name --name SATA --add sata --controller IntelAhci --portcount 3
    VBox storageattach $Name --storagectl SATA --port 0 --device 0 --type hdd --medium $disk

    # После установки (VBOXPOST.CMD, с правами администратора при первом входе): встроенный
    # администратор (SID -500) переименовывается в pvpnadmin и включается. Пароль ему задаёт сам
    # шаблон VirtualBox (AdministratorPassword из --admin-password-file), в командной строке его нет.
    # Команда вставляется в .cmd как есть, поэтому без двойных кавычек, | & < > и %.
    $post = 'powershell.exe -NoProfile -Command $a=(Get-LocalUser).Where({$_.SID.Value.EndsWith(''-500'')})[0]; ' +
            "Rename-LocalUser -SID `$a.SID -NewName $($cred.admin); Enable-LocalUser -SID `$a.SID"

    $unattended = @(
        'unattended', 'install', $Name, "--iso=$Iso", "--image-index=$($image.Index)",
        "--user=$($cred.user)", "--user-password-file=$userPassFile", "--admin-password-file=$adminPassFile",
        '--full-user-name=PVpn Test', '--locale=ru_RU', '--country=RU', '--language=ru-RU',
        '--time-zone=Russian Standard Time', '--hostname=pvpn-test.local', '--install-additions',
        "--post-install-command=$post", '--start-vm=headless'
    )
    # VBoxManage печатает пароли в сводке «Using values» (stdout) — в консоль их не выводим.
    # stderr не перенаправляем: в PS 5.1 при ErrorActionPreference=Stop это превращается в исключение.
    & $vbm @unattended | Where-Object { $_ -notmatch 'password\s*=' }
    if ($LASTEXITCODE -ne 0) { throw "VBoxManage unattended завершился с кодом $LASTEXITCODE" }
}
finally {
    Remove-Item $userPassFile, $adminPassFile -ErrorAction SilentlyContinue
}
Write-Host "Установка запущена в фоне (headless). Посмотреть: VirtualBox → $Name → Показать."
