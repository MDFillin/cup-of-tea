# Управление тестовой ВМ через VirtualBox guestcontrol (нужны Guest Additions в ВМ).
#   vm.ps1 run  -Command '<PowerShell>' [-AsUser] [-NoWait]   выполнить команду (по умолчанию от pvpnadmin);
#                                                            -NoWait — запустить в фоне и сразу вернуться
#   vm.ps1 copy -Source <путь на хосте> -Dest <папка в ВМ>
#   vm.ps1 state | start | stop | snapshot -Snapshot <имя> | restore -Snapshot <имя>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)] [ValidateSet('run', 'copy', 'state', 'start', 'stop', 'snapshot', 'restore', 'ready')] [string]$Action,
    [string]$Command,
    [string]$Source,
    [string]$Dest,
    [string]$Snapshot,
    [switch]$AsUser,
    [switch]$NoWait,
    [int]$TimeoutSec = 600,
    [string]$Name = 'PVpn-Test-Win11',
    [string]$BaseFolder = 'D:\vm'
)
$ErrorActionPreference = 'Stop'
$vbm = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle\VirtualBox').InstallDir 'VBoxManage.exe'
$cred = Get-Content (Join-Path $BaseFolder "$Name.credentials.json") -Raw | ConvertFrom-Json
$login = if ($AsUser) { $cred.user } else { $cred.admin }
$password = if ($AsUser) { $cred.userPassword } else { $cred.adminPassword }

function With-PasswordFile([scriptblock]$body) {
    $file = Join-Path $env:TEMP ("pvpn-vm-" + [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($file, $password)
    try { & $body $file } finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

switch ($Action) {
    'state' { (& $vbm showvminfo $Name --machinereadable | Select-String '^VMState=') -replace 'VMState=', '' }
    'start' { & $vbm startvm $Name --type headless }
    'stop' { & $vbm controlvm $Name acpipowerbutton }
    'snapshot' { & $vbm snapshot $Name take $Snapshot }
    'restore' { & $vbm snapshot $Name restore $Snapshot }
    'ready' {
        # Готовы ли Guest Additions (тогда работает guestcontrol).
        (& $vbm guestproperty get $Name '/VirtualBox/GuestAdd/Version') -join ''
        (& $vbm guestproperty get $Name '/VirtualBox/GuestInfo/OS/LoggedInUsers') -join ''
    }
    'copy' {
        With-PasswordFile {
            param($pf)
            # [string[]]: иначе if развернёт массив из одного элемента в строку, и @recurse уйдёт по символам.
            [string[]]$recurse = if (Test-Path $Source -PathType Container) { '--recursive' } else { @() }
            # Для одиночного файла VBoxManage 7.2 считает назначение именем файла, если нет завершающего «\».
            $target = if ($recurse) { $Dest } else { $Dest.TrimEnd('\') + '\' }
            & $vbm guestcontrol $Name copyto --username $login --passwordfile $pf @recurse --target-directory $target $Source
            if ($LASTEXITCODE -ne 0) { throw "copyto завершился с кодом $LASTEXITCODE" }
        }
    }
    'run' {
        With-PasswordFile {
            param($pf)
            # --profile: профиль и окружение пользователя (%APPDATA%, DPAPI для хранилища ссылок).
            # В $Command не используйте двойные кавычки: PowerShell 5.1 не экранирует их для VBoxManage.
            $verb = if ($NoWait) { 'start' } else { 'run' }
            & $vbm guestcontrol $Name $verb --username $login --passwordfile $pf --profile --timeout ($TimeoutSec * 1000) `
                --exe 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -- powershell.exe -NoProfile -NonInteractive `
                -Command "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $Command"
            $code = $LASTEXITCODE
            if ($code -ne 0) { Write-Host "(код выхода $code)" }
        }
    }
}
