# The Cup of Tea для Android TV

Kotlin, Compose for TV, Xray 26.10.21 внутри приложения. Android 7+ (API 24). ABI: armeabi-v7a, arm64-v8a, x86_64.

## Устройство
| Модуль | Назначение |
|---|---|
| `core-go/` | Обёртка `libcup` над xray для gomobile: запуск по конфигу, TUN через `xray.tun.fd`, счётчики, проверки через `core.Dial` |
| `core/` | Логика без Android (порт `src/PVpn.Core`): ссылки, подписки, конфиги xray, маршруты, обход, DNS, пинг, сторож, базы geo, обновления; тесты JUnit |
| `app/` | `VpnService`, хранилище (AES-GCM, ключ в Android Keystore), интерфейс, страница для телефона, обновления |

- TUN из `VpnService` передаётся во вход `tun` xray, без sing-box. Приложение исключено из VPN (`addDisallowedApplication`).
- Пинг, внешний IP и сторож — через ядро (`core.Dial`, выход по тегу), без локальных портов.
- Маршруты TUN: весь IPv4, кроме локальных сетей и IP текущего сервера (`excludeRoute` на API 33+, иначе подсети-дополнения); IPv6 `2000::/3`; DNS — `198.18.0.2`, во внутренний DNS xray.
- Перезапуск ядра (сторож, смена сети) не закрывает TUN.

## Отличия от Windows
- Только TUN. Kill switch — системный «Постоянный VPN»: `adb shell settings put secure always_on_vpn_app io.github.mdfillin.cupoftea.tv`, `always_on_vpn_lockdown 1`.
- Раздельное туннелирование — по приложениям («мимо VPN» / «только через VPN»).
- Обход блокировок — пресеты: Выкл, Стандарт, Усиленный.
- Базы geoip/geosite не в APK: скачиваются с GitHub (SHA256), когда нужны.
- Подключение после включения ТВ, если VPN был включён.
- Ядро обновляется вместе с приложением.

## Добавление с телефона
«Серверы → Добавить»: QR-код страницы `http://<ip ТВ>:<порт>/<ключ>` в локальной сети. Принимает ссылку подписки,
ссылки серверов, `cupoftea://…`, профиль маршрутизации, адрес DoH; отдаёт диагностику без ключей и адресов.
Работает, пока открыт экран (до 10 мин); только локальные адреса; после 20 неверных ключей закрывается.

## Обновления
Список релизов репозитория, самый новый `tv-v<версия>` с `CupOfTea-TV-<версия>-<abi>.apk`; SHA256 из `digest`.
Проверка через 15 с после запуска и каждые 3 ч; установка через `PackageInstaller`.

## Сборка
JDK 21, Android SDK (platform 37), NDK, Go, gomobile.
```powershell
scripts\build-core.ps1        # app\libs\libcup.aar
.\gradlew.bat :core:test
.\gradlew.bat :app:assembleDebug
scripts\new-release-key.ps1   # один раз: %USERPROFILE%\.cupoftea\release.jks + keystore.properties
scripts\build-apk.ps1         # ..\out\CupOfTea-TV-<версия>-<abi>.apk
scripts\make-banner.ps1       # баннер и значок
```
Версия — `tvVersion` в `app/build.gradle.kts`. Отладочные сборки подписываются тем же ключом.

## Выпуск
`--latest=false` обязателен: десктоп берёт `releases/latest`.
```powershell
gh release create tv-v<версия> ..\out\CupOfTea-TV-<версия>-*.apk --repo MDFillin/cup-of-tea `
  --title "The Cup of Tea для Android TV <версия>" --notes-file <файл> --latest=false
```

## Установка
APK под ABI устройства, через файловый менеджер или браузер ТВ. По adb — `adb install -r -i com.android.packageinstaller <apk>`
(без установщика лаунчер SberBox не показывает значок).

Флаги — Twemoji, CC-BY 4.0.
