package cupoftea.tv.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.foundation.focusGroup
import androidx.compose.ui.focus.focusProperties
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Text
import kotlinx.coroutines.delay
import cupoftea.tv.BuildConfig
import cupoftea.tv.update.UpdateState
import cupoftea.tv.update.Updates
import cupoftea.tv.vpn.VpnState
import kotlinx.coroutines.launch

/** Плашка на «Главной», пока есть новая версия (или идёт её установка). */
@Composable
fun UpdateBanner(actions: UiActions) {
    val state by Updates.state.collectAsState()
    when (val s = state) {
        is UpdateState.Available -> SettingRow(
            "Доступна версия ${s.release.version}",
            "OK — скачать и установить" + (s.release.notes?.let { " · ${it.lineSequence().first().take(80)}" } ?: ""),
        ) { actions.installUpdate(s.release) }
        is UpdateState.Downloading -> Hint("Скачиваю версию ${s.release.version}: ${s.percent}%", Palette.Warn)
        is UpdateState.Installing -> Hint("Устанавливаю версию ${s.release.version}…", Palette.Warn)
        else -> {}
    }
}

/** Описание выпуска без разметки Markdown (как PlainNotes на ПК). */
fun plainNotes(markdown: String): String = markdown.replace("\r\n", "\n").lines().joinToString("\n") { line ->
    line.trimEnd()
        .replace(Regex("^#{1,6}\\s+"), "")
        .replace(Regex("^(\\s*)[-*]\\s+"), "$1• ")
        .replace(Regex("\\[([^\\]]+)]\\([^)]+\\)"), "$1")
        .replace("**", "").replace("__", "").replace("`", "")
}.trim()

/**
 * Экран «Нужно обновить» поверх приложения (0.8.0 на ПК): пока новая версия не поставлена, подключиться и сменить
 * сервер нельзя. «Позже» прячет экран на 15 минут; отключиться можно и без обновления.
 */
@Composable
fun RequiredUpdateScreen(actions: UiActions) {
    val state by Updates.state.collectAsState()
    val hiddenUntil by Updates.hiddenUntil.collectAsState()
    val vpn by cupoftea.tv.vpn.CupVpnService.state.collectAsState()
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(hiddenUntil) {
        while (true) {
            now = System.currentTimeMillis()
            if (now >= hiddenUntil) break
            delay((hiddenUntil - now).coerceAtMost(60_000))
        }
    }
    val release = Updates.required ?: return
    if (now < hiddenUntil) return
    val focus = remember { FocusRequester() }
    LaunchedEffect(release.version) { runCatching { focus.requestFocus() } }
    Box(
        Modifier.fillMaxSize().background(Palette.Bg),
        contentAlignment = Alignment.Center,
    ) {
        // Фокус не уходит с экрана обновления на меню и страницы под ним.
        Column(
            Modifier.width(900.dp).focusProperties { onExit = { cancelFocusChange() } }.focusGroup(),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Text("Нужно обновить", fontSize = 36.sp, color = Palette.Text1)
            Text(
                "Вышла версия ${release.version}, у вас ${BuildConfig.VERSION_NAME}. Чтобы пользоваться The Cup of Tea дальше, " +
                    "обновите программу: скачается с GitHub, Android спросит подтверждение. Серверы и настройки сохранятся.",
                fontSize = 19.sp, color = Palette.Text2,
            )
            release.notes?.let { Text(plainNotes(it), fontSize = 16.sp, color = Palette.Text1, maxLines = 12) }
            when (val s = state) {
                is UpdateState.Downloading -> Hint("Скачиваю: ${s.percent}%", Palette.Warn)
                is UpdateState.Installing -> Hint("Устанавливаю…", Palette.Warn)
                else -> {}
            }
            Row(horizontalArrangement = Arrangement.spacedBy(14.dp), modifier = Modifier.padding(top = 10.dp)) {
                CupButton("Обновить", Modifier.focusRequester(focus)) { actions.installUpdate(release) }
                CupButton("Позже") { Updates.hideScreen() }
                if (vpn != VpnState.Off) CupButton("Отключить VPN") { actions.disconnect() }
            }
        }
    }
}

/** Раздел «Обновления» в настройках. */
@Composable
fun UpdateSettings(actions: UiActions) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val state by Updates.state.collectAsState()
    val status = when (val s = state) {
        UpdateState.Idle -> "Проверяется само раз в 3 часа"
        UpdateState.Checking -> "Проверяю…"
        UpdateState.UpToDate -> "Установлена последняя версия"
        is UpdateState.Available -> "Доступна версия ${s.release.version}"
        is UpdateState.Downloading -> "Скачиваю ${s.release.version}: ${s.percent}%"
        is UpdateState.Installing -> "Устанавливаю ${s.release.version}…"
        is UpdateState.Failed -> "Ошибка: ${s.message}"
    }
    SettingRow("Версия ${BuildConfig.VERSION_NAME} — проверить обновления", status) {
        scope.launch { Updates.check(context, notify = false) }
    }
    (state as? UpdateState.Available)?.let { s ->
        SettingRow("Установить версию ${s.release.version}", "Скачается с GitHub, SHA-256 сверяется; Android спросит подтверждение") {
            actions.installUpdate(s.release)
        }
    }
}
