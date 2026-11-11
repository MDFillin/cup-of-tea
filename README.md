# The Cup of Tea

VPN-клиент для Windows и Android TV. Ядра: Xray 26.10.21, sing-box 1.14.4 (только Windows, TUN).
Android TV — [`android/`](android/README.md).

Имена в коде — `PVpn.*`; снаружи — The Cup of Tea: `CupOfTea.exe`, служба и адаптер `CupOfTea`, `cupoftea-cli.exe`,
`%APPDATA%\CupOfTea`, `C:\ProgramData\CupOfTea` (`src/PVpn.Core/Product.cs`).

## Состав
| Проект | Назначение |
|---|---|
| `src/PVpn.Core` | Ссылки, подписки, генераторы конфигов xray и sing-box, запуск ядер (stdin, Job Object, Ctrl+C), kill switch (WFP), маршруты, системный прокси (WinINET), профили (DPAPI), клиент службы |
| `src/PVpn.Service` | Служба `CupOfTea` (LocalSystem): канал `\\.\pipe\cupoftea`, TUN-сессия, kill switch, сторож, смена сети, `--repair` |
| `src/PVpn.App` | WPF (.NET 10, WPF-UI, MVVM Toolkit), трей, один экземпляр, работает без прав администратора |
| `src/PVpn.Cli` | `cupoftea-cli.exe` |

## Сборка
```powershell
scripts\fetch-cores.ps1      # ядра с GitHub, SHA256 → cores\
dotnet build
dotnet test                  # юнит-тесты и проверка конфигов ядрами
scripts\build.ps1            # out\CupOfTea
scripts\build-setup.ps1      # out\CupOfTea-Setup-<версия>.exe (Inno Setup 6)
scripts\build-portable.ps1   # out\CupOfTea-Portable-<версия>.zip
scripts\make-icons.ps1       # значки src\PVpn.App\Assets
```
.NET 10 SDK, Inno Setup 6. Скрипты `.ps1` — UTF-8 с BOM (Windows PowerShell 5.1).

## Возможности
**Протоколы.** VLESS (raw/Vision, XHTTP, WS, gRPC, HTTPUpgrade; REALITY, TLS), Trojan, VMess (base64-JSON и обычная
ссылка), Shadowsocks (SIP002, 2022, старый base64; без плагинов), Hysteria2 (salamander, диапазон портов `host:a-b`,
`mport=`). `allowInsecure=1` — только с `pcs` (отпечаток сертификата), `vcn` — имя для проверки.

**Режимы.** TUN — через службу: sing-box TUN → SOCKS с паролем → xray, kill switch (WFP). Прокси — xray из программы,
SOCKS/HTTP на 127.0.0.1 и системный прокси; восстанавливается при выходе и после сбоя. Портативная версия — только прокси,
данные в `data\` рядом с программой.

**Подписки** (Remnawave, 3x-ui, Marzban). Base64 или список ссылок; полные конфиги xray (User-Agent `INCY/2.0.0`) со своими
маршрутами, DNS, `finalmask`, балансировщиками и цепочками — из конфига берутся только outbounds, routing, dns,
observatory. Заголовки: `subscription-userinfo`, `profile-title`, `profile-update-interval` (12 ч по умолчанию, после ошибки
30 мин), `announce`, `announce-url`, `profile-description`, `support-url`, `support-email`, `profile-web-page-url`,
`hide-url`, `sort-order`, `routing`, `autorouting`, `new-url`/`new-domain`, `fragmentation-*`, `noises-*`,
`routing-geo-update-interval`; те же строки `#имя: значение` в теле. HWID: `x-hwid` (хэш MachineGuid), `x-device-os`,
`x-ver-os`, `x-device-model`, отключается. При обновлении серверы с тем же именем сохраняют Id.

**Маршруты.** Профили маршрутизации (`DirectSites`, `ProxyIp`, `BlockSites`…, `GlobalProxy`, `DomainStrategy`;
`geosite:`/`geoip:`, `domain:`/`full:`/`keyword:`/`regexp:`, CIDR): готовые «Россия напрямую», «… без рекламы»,
«Без рекламы»; от подписки; по ссылке `cupoftea://routing/onadd/…`; по адресу с обновлением раз в сутки. У серверов
с полным конфигом — их маршруты (отключаются). Раздельное туннелирование: «всё, кроме выбранного» и «только выбранное»;
программы по exe или папке, сайты по домену; в TUN — правила sing-box, применяются без переподключения.

**Обход блокировок.** Отпечаток TLS, режим XHTTP, XMUX, фрагментация ClientHello (`tlshello`, 100–200 байт, 10–20 мс),
mux для TCP (8 потоков, xudp 16; у Vision через mux только UDP), шумы Hysteria2 (`finalmask` noise, 50–100 байт),
смена портов hy2 (`finalmask` udphop). Значения от панели (`fragmentation-*`, `noises-*`) заменяют свои.

**DNS (TUN).** DoH через туннель: Cloudflare, Google, Quad9, AdGuard или свой адрес. Проверка утечки — bash.ws
(`/id.php`). Если у сервера из подписки свой DNS — используется он.

**Базы geoip/geosite.** v2fly или runetfreedom; обновление службой с GitHub, SHA256 из `.sha256sum`, проверка
маршрутов на новых базах, прежние — в `cores-previous`.

**Серверы.** Группы по подпискам (сворачиваются), поиск, сортировка (подписка / пинг / имя), пинг всех серверов одним
xray (8 параллельно, лучшее из двух замеров), «Автовыбор» для групп серверов по ссылкам (balancer leastPing), QR (показ,
чтение с экрана, из файла, из буфера), ссылки `cupoftea://add|onadd|import/<подписка или base64>`.

**Прочее.** Скорость и трафик (metrics xray на 127.0.0.1), уведомления, горячая клавиша (Ctrl+Alt+P), автоподключение
и доверенные сети (Network List Manager), проверка страницы входа (NCSI), диагностика zip без ключей и ссылок, сторож
режима прокси (2 неудачные проверки → перезапуск, до 3 раз), к IP текущего сервера пускаются все программы при kill switch.
Подсказка про сторонний антивирус (Центр безопасности Windows): какие файлы ядер добавить в исключения.

**Восстановление после перезагрузки.** VPN был включён при выключении ПК (не выключен кнопкой и не через «Выход») —
после входа в Windows программа подключается к тому же серверу (оба режима, включено по умолчанию, `WasConnected`).
«Защита до входа в Windows» (TUN, выключено по умолчанию): служба хранит запрос подключения в
`C:\ProgramData\CupOfTea\resume.dat` (DPAPI от имени SYSTEM) и поднимает его при загрузке — ждёт сеть, повторяет
от 5 с до раза в минуту; забывает по «Отключить», «Восстановить сеть» и при тестовом подключении.

**Память.** Окно создаётся при показе и освобождается через 30 с после того, как его спрятали в трей (`MemoryTrim`);
запуск с `--tray` окна не создаёт.

**Обновления.** Программа — с GitHub (`Product.UpdateRepository`), последний релиз `v<версия>` с
`CupOfTea-Setup-<версия>.exe`, SHA256 из `digest`, тихая установка `/SILENT … /RELAUNCH=1`. Обновление обязательное:
проверка при запуске (повторы через 10 с, 30 с, 2 мин) и каждые 3 ч; есть новая версия — окно перекрывается экраном
«Нужно обновить», подключиться и сменить сервер нельзя (отключиться можно; автоподключение и восстановление работают),
закрытое окно возвращается через 15 мин. Ядра — службой: официальные релизы XTLS/Xray-core и SagerNet/sing-box, SHA256
по GitHub API, проверка версии и конфигов, откат (`cores-previous`).

## Установка
`CupOfTea-Setup-<версия>.exe`, один запрос UAC. `install.ps1`: служба, правило брандмауэра для sing-box, ярлык,
доступ к службе для пользователя; ядра из комплекта ставятся, только если новее установленных. Удаление —
`uninstall.ps1` (служба возвращает сеть, прокси и автозапуск снимаются, серверы пользователя остаются).
Журнал — `install.log` в папке программы. Чужие программы установщик и программа не трогают: свои AppId
`{6271B140-7746-4A79-9A65-4A5E040BD4C7}` и ключи WFP; запись прежнего AppId (до 0.7.3) снимается, только если это
The Cup of Tea в той же папке.

## CLI
```powershell
cupoftea-cli import <файл|-> [--filter P]      # ссылки или JSON с конфигами xray
cupoftea-cli export [--filter P] [--configs]   # ссылки / конфиги подписки JSON-массивом
cupoftea-cli list | remove <имя> | config <имя> [--tun] | check [<имя>]
cupoftea-cli test [<имя>|--all] [--bypass F]   # внешний IP и задержка
cupoftea-cli proxy <имя> [--system-proxy] [--seconds N]   # SOCKS 10828, HTTP 10829
cupoftea-cli ping [<имя>] | netinfo | restore-proxy | killswitch [off] | dnsleak [--dns D]
cupoftea-cli sub add <url> [--name N] | list | update [<имя>|--all] | remove <имя>|--all
cupoftea-cli svc connect <имя> [--test N] [--no-killswitch] [--dns D] [--split F] [--bypass F]
cupoftea-cli svc confirm | status | disconnect | repair | logs | reconfigure
cupoftea-cli svc cores [check|update|rollback]
```
`CupOfTea.exe --tray` — запуск в трей; `--snapshot ui.png --tab N [--sub S] [--width W --height H]` — снимок окна.
`e2e-check.ps1` в папке программы — проверки при подключённом TUN.

## Тестовая ВМ
`scripts\vm\` (VirtualBox): `create-test-vm.ps1`, `deploy-and-test.ps1`, `fault-test.ps1`, `reboot-test.ps1`,
`ui-test.ps1`, `split-test.ps1`, `split-ui-test.ps1`, `bypass-test.ps1`, `dns-test.ps1`, `sub-test.ps1`, `e-test.ps1`,
`f-test.ps1`, `f-portal-vpn-test.ps1`, `g-test.ps1`, `switch-test.ps1`, `h-test.ps1`.

## История версий
- **0.7.3** — самостоятельность: свой AppId установщика и свои ключи WFP; установка больше не снимает другие клиенты
  и не забирает их папки, данные и автозапуск (перенос с прежних имён убран).
- **0.7.2** — уведомление о новой версии; проверка через 15 с после запуска и каждые 3 ч.
- **0.7.1** — kill switch пускает любые программы к IP текущего сервера (фильтр WFP «VPN-сервер»).
- **0.7.0** — сворачиваемые группы серверов (`CollapsedGroups`), анимации (`Motion`).
- **0.6.1** — проверка утечки DNS: номер проверки с `bash.ws/id.php`.
- **0.6.0** — пинг одним xray, автовыбор для серверов по ссылкам, сторож режима прокси, обновление из программы,
  kill switch пропускает DHCP-сервер (Hyper-V, WSL, хот-спот), CLI `import` JSON и `export --configs`.
- **0.5.0** — trojan, vmess, ss; WS, gRPC, HTTPUpgrade; обход от панели; автообновление маршрутов; базы geoip/geosite.
- **0.4.0** — переименование в The Cup of Tea, профили маршрутизации, заголовки подписки, HWID, сортировка и автопинг,
  ссылки `cupoftea://`, портативная версия.
- **0.3.0** — полные конфиги xray из подписки, флаги (Twemoji, CC-BY 4.0), маршруты подписки.
- **0.2.0** — Hysteria2 salamander, сторож, смена сети.
