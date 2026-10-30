# Выполняется В ВМ, в интерактивном сеансе pvpn (через задачу планировщика): действие в окне Tempest VPN
# через UI Automation и снимок всех текстов окна в журнал.
#   -Action dump | connect | check | proxy | tun | dnsleak | select -Server <имя> | tab -Tab <номер> | open -Server <страница настроек>
# Страницы: 0 Главная, 1 Серверы, 2 Маршруты, 3 Настройки (dns, bypass, tun, trusted, subs, cores), 4 Журнал.
# Действие само открывает нужную страницу: скрытые страницы не видны UI Automation.
param(
    [ValidateSet('dump', 'connect', 'select', 'tab', 'proxy', 'tun', 'check', 'splitOn', 'onlyMode', 'excludeMode', 'domain', 'running', 'apply', 'dnsleak', 'subAdd', 'paste', 'hotkey', 'qrShow', 'qrToClipboard', 'pasteClipboard', 'edit', 'diag', 'hide', 'trustHere', 'untrust', 'login', 'dblclick', 'open')] [string]$Action = 'dump',
    [string]$Server,
    [string]$Tab,
    [string]$Log = 'C:\PVpnSetup\ui.log'
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [Windows.Automation.AutomationElement]
function Log([string]$text) { Add-Content -Path $Log -Value $text -Encoding UTF8 }
function NameCondition([string]$name) { New-Object Windows.Automation.PropertyCondition ($AE::NameProperty, $name) }

# Горячая клавиша не требует окна: оно может быть спрятано в трей.
if ($Action -eq 'hotkey') {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.SendKeys]::SendWait('^%p')
    Start-Sleep 2
    Log "--- $(Get-Date -Format HH:mm:ss) hotkey"
    exit 0
}
$window = $null
for ($i = 0; $i -lt 40 -and -not $window; $i++) {
    $window = $AE::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, (NameCondition 'Tempest VPN'))
    if (-not $window) { Start-Sleep -Milliseconds 500 }
}
if (-not $window) { Log "--- $(Get-Date -Format HH:mm:ss) ${Action}: окно Tempest VPN не найдено"; exit 1 }

function Find([string]$name) { $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (NameCondition $name)) }
function Ancestor($element, $type) {
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    $e = $element
    while ($e -and $e.Current.ControlType -ne $type) { $e = $walker.GetParent($e) }
    return $e
}

# Пункт бокового меню по номеру (имена кириллицей через guestcontrol передавать ненадёжно).
function Page([int]$index) {
    $nav = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition ($AE::AutomationIdProperty, 'Nav')))
    $items = $nav.FindAll([Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
    $items[$index].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}
# Страница настроек (dns, bypass, tun, trusted, subs, cores).
function SettingsPage([string]$name) {
    Page 3
    $back = Find 'Назад к настройкам'
    if ($back) { $back.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition ($AE::AutomationIdProperty, "Open_$name"))).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
}
switch ($Action) {
    { $_ -in 'connect', 'proxy', 'tun', 'login' } { Page 0 }
    { $_ -in 'select', 'check', 'paste', 'pasteClipboard', 'qrShow', 'edit', 'dblclick' } { Page 1 }
    { $_ -in 'splitOn', 'onlyMode', 'excludeMode', 'domain', 'running', 'apply' } { Page 2 }
    'dnsleak' { SettingsPage 'dns' }
    'subAdd' { SettingsPage 'subs' }
    { $_ -in 'trustHere', 'untrust' } { SettingsPage 'trusted' }
    'diag' { Page 4 }
}
switch ($Action) {
    'open' { SettingsPage $Server }
    'connect' {
        # Кнопка питания: имя меняется («Подключить», «Отключить», «Отменить»), ищем по AutomationId.
        $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition ($AE::AutomationIdProperty, 'PowerButton')))
        Log "  [кнопка: $($button.Current.Name), доступна: $($button.Current.IsEnabled)]"
        $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    'select' {
        $item = Ancestor (Find $Server) ([Windows.Automation.ControlType]::ListItem)
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    'tab' { Page ([int]$Tab) }
    'check' { (Find 'Пинг').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    'splitOn' {
        # «Раздельное туннелирование» — и заголовок, и переключатель: нужен тот, что умеет Toggle.
        $all = $window.FindAll([Windows.Automation.TreeScope]::Descendants, (NameCondition 'Раздельное туннелирование'))
        $toggle = $all | Where-Object { $_.GetSupportedPatterns() -contains [Windows.Automation.TogglePattern]::Pattern } | Select-Object -First 1
        $p = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if ($p.Current.ToggleState -ne 'On') { $p.Toggle() }
    }
    'onlyMode' { (Find 'Через VPN только выбранное').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    'excludeMode' { (Find 'Всё через VPN, кроме выбранного').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    'apply' { (Find 'Применить сейчас').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    'domain' {
        $box = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition ($AE::AutomationIdProperty, 'DomainBox')))
        $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Server)
        (Find 'Добавить сайт').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    'running' {
        # Кнопка открывает модальное окно (WPF выполняет Invoke асинхронно), в нём выбираем программу по части пути.
        (Find 'Из запущенных…').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $picker = $null
        for ($i = 0; $i -lt 40 -and -not $picker; $i++) {
            Start-Sleep -Milliseconds 500
            # Диалог — окно верхнего уровня: поиск по всему дереву рабочего стола занимал десятки секунд.
            $picker = $AE::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, (NameCondition 'Запущенные программы'))
            if (-not $picker) { $picker = $window.FindFirst([Windows.Automation.TreeScope]::Children, (NameCondition 'Запущенные программы')) }
        }
        $item = $null
        for ($i = 0; $i -lt 40 -and -not $item; $i++) {
            Start-Sleep -Milliseconds 500
            $items = $picker.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
            $item = $items | Where-Object { $_.Current.Name -like "*$Server*" } | Select-Object -First 1
        }
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $picker.FindFirst([Windows.Automation.TreeScope]::Descendants, (NameCondition 'Добавить')).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    'proxy' { (Find 'Только прокси').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    'tun' { (Find 'Весь трафик (TUN)').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    'subAdd' {
        # Подписка через поле на странице «Подписки»: адрес в -Server.
        $box = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition ($AE::AutomationIdProperty, 'SubscriptionBox')))
        $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Server)
        Start-Sleep -Milliseconds 300
        (Find 'Добавить').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 4
    }
    'paste' {
        # Текст из -Server в буфер обмена (сеанс интерактивный) и «Вставить ссылки».
        Set-Clipboard -Value $Server
        (Find 'Вставить ссылки').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 4
    }
    'hotkey' {
        # Глобальное сочетание программы (по умолчанию Ctrl+Alt+P) — нажатие через SendInput, как с клавиатуры.
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.SendKeys]::SendWait('^%p')
        Start-Sleep 2
    }
    'qrShow' {
        # Окно с QR выбранного сервера (модальное: Invoke возвращается сразу).
        (Find 'QR сервера').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 2
    }
    'qrToClipboard' {
        # Снимок окна с QR — в буфер обмена картинкой, окно закрыть.
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $qr = $AE::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition ($AE::NameProperty, 'Скопировать ссылку')))
        $win = Ancestor $qr ([Windows.Automation.ControlType]::Window)
        $r = $win.Current.BoundingRectangle
        $bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
        [Windows.Forms.Clipboard]::SetImage($bmp)
        $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep 1
    }
    'pasteClipboard' {
        (Find 'Вставить ссылки').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 3
    }
    'edit' {
        # «Изменить сервер»: новое имя из -Server, «Сохранить».
        (Find 'Изменить сервер').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $dialog = $null
        for ($i = 0; $i -lt 20 -and -not $dialog; $i++) {
            Start-Sleep -Milliseconds 500
            # Окно-диалог — верхнего уровня (кнопка с тем же именем в главном окне глубже).
            $dialog = $AE::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, (NameCondition 'Изменить сервер'))
            if (-not $dialog) { $dialog = $window.FindFirst([Windows.Automation.TreeScope]::Children, (NameCondition 'Изменить сервер')) }
        }
        $box = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)))
        $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Server)
        $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants, (NameCondition 'Сохранить')).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 2
    }
    'dblclick' {
        # Двойной щелчок мышью по строке сервера (у UI Automation нет двойного щелчка).
        Add-Type -Namespace PVpnTest -Name Mouse -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);'
        $item = Ancestor (Find $Server) ([Windows.Automation.ControlType]::ListItem)
        $r = $item.Current.BoundingRectangle
        [PVpnTest.Mouse]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
        foreach ($i in 1..2) { [PVpnTest.Mouse]::mouse_event(2, 0, 0, 0, 0); [PVpnTest.Mouse]::mouse_event(4, 0, 0, 0, 0); Start-Sleep -Milliseconds 60 }
        Start-Sleep 1
    }
    'trustHere' { (Find 'Доверять этой сети').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2 }
    'untrust' { (Find 'Убрать из доверенных').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2 }
    'login' { (Find 'Войти в сеть (гостиница, кафе)').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 3 }
    'hide' { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
    'diag' {
        (Find 'Сохранить диагностику').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 4
    }
    'dnsleak' {
        # Проверка утечки DNS (страница «DNS» в настройках): ждём, пока уйдёт «Проверяю…».
        (Find 'Проверить утечку DNS').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Milliseconds 500
            $texts = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
            if ($i -gt 2 -and -not ($texts | Where-Object { $_.Current.Name -like 'Проверяю*' })) { break }
        }
    }
}
Start-Sleep -Milliseconds 500

$texts = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
Log "--- $(Get-Date -Format HH:mm:ss) $Action"
foreach ($t in $texts) { if ($t.Current.Name) { Log "  $($t.Current.Name)" } }
