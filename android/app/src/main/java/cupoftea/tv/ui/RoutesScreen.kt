package cupoftea.tv.ui

import android.content.Intent
import android.content.pm.PackageManager
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.graphics.drawable.toBitmap
import androidx.tv.material3.Checkbox
import cupoftea.core.routing.RoutingProfiles
import cupoftea.tv.data.Repository
import cupoftea.tv.data.SplitMode
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** Установленное приложение, которое можно запустить с ТВ. */
data class AppEntry(val pkg: String, val label: String, val icon: ImageBitmap?)

@Composable
fun RoutesScreen(repo: Repository, actions: UiActions) {
    val settings by repo.settings.collectAsState()
    val data by repo.data.collectAsState()
    val context = LocalContext.current
    var apps by remember { mutableStateOf<List<AppEntry>?>(null) }
    LaunchedEffect(Unit) { apps = withContext(Dispatchers.IO) { loadApps(context.packageManager, context.packageName) } }

    fun change(apply: (cupoftea.tv.data.AppSettings) -> cupoftea.tv.data.AppSettings) {
        repo.updateSettings(apply)
        actions.reapply()
    }

    val current = settings.routing
    LazyColumn(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        item { ScreenTitle("Маршруты", "Что идёт через VPN, а что — напрямую") }

        item { SectionTitle("Профиль маршрутизации — для серверов по ссылкам и автовыбора") }
        item { RadioRow("Всё через VPN", selected = current == null) { change { it.copy(routing = null) } } }
        items(RoutingProfiles.presets) { p ->
            RadioRow(p.name, "${p.ruleCount} правил · нужны базы geoip/geosite", selected = current?.name == p.name) {
                change { it.copy(routing = RoutingProfiles.validate(p)) }
            }
        }
        val fromSubs = data.subscriptions.mapNotNull { s -> s.routing?.let { s to it } }
        items(fromSubs) { (s, p) ->
            RadioRow("От подписки «${s.name}»: ${p.name}", "${p.ruleCount} правил", selected = current == p) {
                change { it.copy(routing = p) }
            }
        }
        item {
            SwitchRow(
                "Маршруты серверов из подписки",
                "У серверов с полным конфигом свои маршруты (российские сайты напрямую и т. п.). Выключено — всё через сервер.",
                settings.useServerRouting,
            ) { v -> change { it.copy(useServerRouting = v) } }
        }

        item { SectionTitle("Приложения") }
        item {
            Hint("Онлайн-кинотеатры, IPTV и банки часто не работают через VPN — их удобно пустить напрямую.", Palette.Text2)
        }
        item { RadioRow("Все приложения через VPN", selected = settings.splitMode == SplitMode.Off) { change { it.copy(splitMode = SplitMode.Off) } } }
        item {
            RadioRow("Отмеченные — мимо VPN", selected = settings.splitMode == SplitMode.Exclude) {
                change { it.copy(splitMode = SplitMode.Exclude) }
            }
        }
        item {
            RadioRow("Через VPN только отмеченные", selected = settings.splitMode == SplitMode.Only) {
                change { it.copy(splitMode = SplitMode.Only) }
            }
        }
        if (settings.splitMode != SplitMode.Off) {
            val list = apps
            if (list == null) item { Hint("Загружаю список приложений…") }
            else items(list, key = { it.pkg }) { app ->
                val checked = app.pkg in settings.splitApps
                SettingRow(
                    app.label, app.pkg,
                    leading = { AppIcon(app.icon) },
                    trailing = { Checkbox(checked = checked, onCheckedChange = null) },
                ) {
                    change { s -> s.copy(splitApps = if (checked) s.splitApps - app.pkg else s.splitApps + app.pkg) }
                }
            }
        }
    }
}

/** Приложения с ярлыком на ТВ (leanback) или в обычном лаунчере, кроме нас самих. */
private fun loadApps(pm: PackageManager, self: String): List<AppEntry> {
    val intents = listOf(
        Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LEANBACK_LAUNCHER),
        Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER),
    )
    val seen = LinkedHashMap<String, AppEntry>()
    for (intent in intents) {
        for (ri in pm.queryIntentActivities(intent, 0)) {
            val pkg = ri.activityInfo.packageName
            if (pkg == self || pkg in seen) continue
            val icon = try {
                ri.loadIcon(pm)?.toBitmap(48, 48)?.asImageBitmap()
            } catch (e: Exception) {
                null
            }
            seen[pkg] = AppEntry(pkg, ri.loadLabel(pm).toString(), icon)
        }
    }
    return seen.values.sortedBy { it.label.lowercase() }
}

@Composable
private fun AppIcon(icon: ImageBitmap?) {
    if (icon != null) Image(icon, null, Modifier.size(32.dp))
}
