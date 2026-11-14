package cupoftea.tv.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.foundation.focusGroup
import androidx.compose.ui.focus.focusProperties
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import cupoftea.tv.data.Repository
import cupoftea.tv.vpn.CupVpnService
import cupoftea.tv.vpn.VpnState

enum class Tab(val title: String, val glyph: String) {
    Home("Главная", "◉"),
    Servers("Серверы", "☰"),
    Routes("Маршруты", "⇄"),
    Settings("Настройки", "⚙"),
    Log("Журнал", "≡"),
}

/** Действия, которые экранам нужны от активности (разрешение на VPN спрашивает она). */
class UiActions(
    val connect: (serverId: String?) -> Unit,
    val disconnect: () -> Unit,
    /** Настройки подключения изменились: если VPN включён — переподключиться с ними. */
    val reapply: () -> Unit,
    /** Скачать и поставить новую версию (с запросом разрешения на установку, если его нет). */
    val installUpdate: (cupoftea.core.TvRelease) -> Unit,
)

@Composable
fun AppRoot(repo: Repository, actions: UiActions) {
    var tab by rememberSaveable { mutableStateOf(Tab.Home) }
    val state by CupVpnService.state.collectAsState()
    val settings by repo.settings.collectAsState()
    Palette.winter = Season.winterFor(settings.winter)
    CupTheme {
        // Нажатия пульта видит пасхалка с секретным кодом; перехватываются только OK в конце кода.
        Box(Modifier.fillMaxSize().onPreviewKeyEvent { Eggs.onKey(it) }) {
            Row(Modifier.fillMaxSize().background(Palette.Bg)) {
                Rail(tab, state) { tab = it }
                Box(Modifier.fillMaxHeight().weight(1f).padding(start = 40.dp, end = 48.dp, top = 36.dp, bottom = 24.dp)) {
                    when (tab) {
                        Tab.Home -> HomeScreen(repo, actions) { tab = Tab.Servers }
                        Tab.Servers -> ServersScreen(repo, actions)
                        Tab.Routes -> RoutesScreen(repo, actions)
                        Tab.Settings -> SettingsScreen(repo, actions)
                        Tab.Log -> LogScreen(repo)
                    }
                }
            }
            RequiredUpdateScreen(actions)
        }
    }
}

/** Боковое меню: фокус на пункте сразу открывает страницу (как принято на ТВ), вправо — в саму страницу. */
@Composable
private fun Rail(current: Tab, state: VpnState, onSelect: (Tab) -> Unit) {
    val dot by animateColorAsState(stateColor(state), label = "rail-dot")
    val requesters = remember { Tab.entries.associateWith { FocusRequester() } }
    Column(
        Modifier.fillMaxHeight().width(230.dp).background(Palette.Side).padding(horizontal = 14.dp, vertical = 36.dp)
            // Вход в меню (влево со страницы) — на текущий пункт, а не на ближайший по высоте:
            // иначе фокус на соседнем пункте сразу переключил бы страницу.
            .focusProperties { onEnter = { requesters.getValue(current).requestFocus() } }
            .focusGroup(),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        // Название: пять нажатий OK — пасхалка; зимой — колпак.
        Surface(
            onClick = { Eggs.logoPressed() },
            modifier = Modifier.padding(bottom = 22.dp),
            shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(12.dp)),
            colors = ClickableSurfaceDefaults.colors(
                containerColor = Palette.Side, contentColor = Palette.Text1,
                focusedContainerColor = Palette.Surface3, focusedContentColor = Palette.Text1,
            ),
            scale = ClickableSurfaceDefaults.scale(focusedScale = 1.0f),
        ) {
            Box {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 10.dp, vertical = 8.dp)) {
                    Box(Modifier.size(12.dp).background(dot, CircleShape))
                    Spacer(Modifier.width(10.dp))
                    Text("The Cup of Tea", fontSize = 20.sp)
                }
                if (Palette.winter) SantaHat(Modifier.padding(start = 2.dp).size(22.dp, 18.dp))
            }
        }
        for (t in Tab.entries) {
            var focused by remember { mutableStateOf(false) }
            Surface(
                onClick = { onSelect(t) },
                modifier = Modifier.fillMaxWidth().height(52.dp).focusRequester(requesters.getValue(t)).onFocusChanged {
                    focused = it.isFocused
                    if (it.isFocused && current != t) onSelect(t)
                },
                shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(12.dp)),
                colors = ClickableSurfaceDefaults.colors(
                    containerColor = if (t == current) Palette.Surface2 else Palette.Side,
                    contentColor = if (t == current) Palette.Text1 else Palette.Text2,
                    focusedContainerColor = Palette.Text1,
                    focusedContentColor = Palette.Bg,
                ),
                scale = ClickableSurfaceDefaults.scale(focusedScale = 1.03f),
            ) {
                Row(Modifier.fillMaxSize().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(t.glyph, fontSize = 20.sp, color = if (focused) Palette.Bg else if (t == current) Palette.Accent else Palette.Text3)
                    Spacer(Modifier.width(14.dp))
                    Text(t.title, fontSize = 19.sp)
                }
            }
        }
        Spacer(Modifier.weight(1f))
        Text("All rights reserved\nby @kpenkuu4au ♥", fontSize = 12.sp, color = Palette.Text3, modifier = Modifier.padding(start = 10.dp))
    }
}

fun stateColor(state: VpnState) = when (state) {
    is VpnState.On -> Palette.Accent
    is VpnState.Connecting, is VpnState.Reconnecting -> Palette.Warn
    is VpnState.Failed -> Palette.Error
    VpnState.Off -> Palette.Text3
}
