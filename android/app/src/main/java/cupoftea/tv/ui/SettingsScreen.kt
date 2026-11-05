package cupoftea.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.unit.dp
import cupoftea.core.config.BypassPreset
import cupoftea.core.config.DnsOptions
import cupoftea.core.config.DnsProvider
import cupoftea.core.geo.Geo
import cupoftea.tv.BuildConfig
import cupoftea.tv.data.Repository
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@Composable
fun SettingsScreen(repo: Repository, actions: UiActions) {
    val settings by repo.settings.collectAsState()
    val data by repo.data.collectAsState()
    val scope = rememberCoroutineScope()
    var geoStatus by remember { mutableStateOf<String?>(null) }
    var subStatus by remember { mutableStateOf<String?>(null) }
    var phone by remember { mutableStateOf(false) }
    if (phone) PhoneDialog { phone = false }
    val dateFormat = remember { SimpleDateFormat("dd.MM.yyyy HH:mm", Locale.getDefault()) }

    fun change(apply: (cupoftea.tv.data.AppSettings) -> cupoftea.tv.data.AppSettings) {
        repo.updateSettings(apply)
        actions.reapply()
    }

    LazyColumn(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        item { ScreenTitle("Настройки") }
        item {
            SettingRow(
                "Добавить с телефона",
                "Подписка, ссылки серверов, профиль маршрутизации, свой DNS, диагностика — через страницу в домашней сети",
            ) { phone = true }
        }

        item { SectionTitle("Обход блокировок — для серверов по ссылкам") }
        item { Hint("У серверов из подписки свои настройки обхода от панели.", Palette.Text2) }
        items(BypassPreset.entries) { p ->
            val note = when (p) {
                BypassPreset.Off -> "Без фрагментации, mux и шумов"
                BypassPreset.Standard -> "Фрагментация TLS, mux, шумы перед Hysteria2 — как на десктопе"
                BypassPreset.Strong -> "Режутся первые записи соединения, шумы длиннее — если стандарт не помогает"
            }
            RadioRow(p.title, note, selected = settings.bypass == p) { change { it.copy(bypass = p) } }
        }

        item { SectionTitle("DNS — через туннель, по DNS-over-HTTPS") }
        item { Hint("Если у сервера из подписки свой DNS, используется он.", Palette.Text2) }
        val providers = DnsProvider.entries.filter { it != DnsProvider.Custom || settings.dns.customUrl != null }
        items(providers) { p ->
            RadioRow(
                if (p == DnsProvider.Custom) "Свой: ${settings.dns.customUrl}" else p.title,
                selected = settings.dns.provider == p,
            ) { change { it.copy(dns = DnsOptions(p, it.dns.customUrl)) } }
        }

        item { SectionTitle("Подключение") }
        item {
            SwitchRow("Подключаться при включении ТВ", "Если перед выключением VPN был включён", settings.connectOnBoot) { v ->
                repo.updateSettings { it.copy(connectOnBoot = v) }
            }
        }
        item {
            Hint(
                "Защита от утечек при обрыве — системный «Постоянный VPN» с блокировкой без VPN. На многих ТВ этого меню нет; " +
                    "включается с компьютера: adb shell settings put secure always_on_vpn_app ${BuildConfig.APPLICATION_ID} " +
                    "и adb shell settings put secure always_on_vpn_lockdown 1",
                Palette.Text3,
            )
        }

        item { SectionTitle("Подписки") }
        if (data.subscriptions.isEmpty()) item { Hint("Подписок нет. Добавить — «Добавить с телефона» в начале страницы.", Palette.Text2) }
        items(data.subscriptions, key = { it.id }) { s ->
            val updated = s.lastUpdated?.let { "обновлена " + dateFormat.format(Date(it * 1000)) } ?: "не обновлялась"
            SettingRow(
                s.name,
                listOfNotNull(updated, s.lastError?.let { "ошибка: $it" }, "OK — обновить, долгое нажатие — удалить").joinToString(" · "),
                onLongClick = {
                    repo.removeSubscription(s.id)
                    subStatus = "Подписка «${s.name}» удалена вместе с её серверами"
                },
            ) {
                scope.launch {
                    subStatus = "Обновляю «${s.name}»…"
                    subStatus = repo.updateSubscription(s)
                }
            }
        }
        subStatus?.let { item { Hint(it, Palette.Warn) } }
        item {
            SwitchRow(
                "Сообщать панели идентификатор устройства",
                "x-hwid и модель ТВ — по ним панели считают лимит устройств; без них некоторые не отдают подписку",
                settings.sendHwid,
            ) { v -> repo.updateSettings { it.copy(sendHwid = v) } }
        }

        item { SectionTitle("Базы geoip / geosite") }
        item {
            val dir = repo.geoDir
            val file = File(dir, "geosite.dat")
            val installed = if (Geo.installed(dir))
                "Установлены: ${Geo.sourceOf(Geo.installedSource(dir)).title}, ${dateFormat.format(Date(file.lastModified()))}"
            else "Не скачаны — скачаются сами, когда понадобятся"
            Hint(installed, Palette.Text2)
        }
        items(Geo.sources) { src ->
            RadioRow(src.title, src.note, selected = settings.geoSource == src.key) {
                repo.updateSettings { it.copy(geoSource = src.key) }
            }
        }
        item {
            SettingRow("Скачать базы сейчас", geoStatus ?: "С GitHub, с проверкой SHA-256; новые базы — со следующего подключения") {
                scope.launch {
                    geoStatus = "Скачиваю…"
                    geoStatus = withContext(Dispatchers.IO) {
                        try {
                            Geo.download(Geo.sourceOf(settings.geoSource), repo.geoDir) { geoStatus = it }
                            "Базы обновлены"
                        } catch (e: Exception) {
                            "Не скачались: ${e.message}"
                        }
                    }
                    actions.reapply()
                }
            }
        }

        item { SectionTitle("Обновления") }
        item { UpdateSettings(actions) }

        item { SectionTitle("О программе") }
        item {
            Hint(
                "The Cup of Tea для Android TV ${BuildConfig.VERSION_NAME} · xray ${libcup.Libcup.version()}\n" +
                    "Флаги — Twemoji, CC-BY 4.0",
                Palette.Text2,
            )
        }
    }
}
