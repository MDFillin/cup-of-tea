package cupoftea.tv.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.platform.LocalContext
import cupoftea.tv.BuildConfig
import cupoftea.tv.update.UpdateState
import cupoftea.tv.update.Updates
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
