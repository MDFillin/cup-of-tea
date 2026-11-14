package cupoftea.tv.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.focusProperties
import androidx.compose.foundation.focusGroup
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import androidx.tv.material3.SurfaceDefaults
import androidx.tv.material3.Text
import cupoftea.core.profiles.Profile
import cupoftea.tv.data.Repository
import cupoftea.tv.data.ServerGroup
import cupoftea.tv.vpn.CupVpnService
import cupoftea.tv.vpn.VpnState
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@Composable
fun HomeScreen(repo: Repository, actions: UiActions, openServers: () -> Unit) {
    val state by CupVpnService.state.collectAsState()
    val stats by CupVpnService.stats.collectAsState()
    val data by repo.data.collectAsState()
    val pings by repo.pings.collectAsState()
    var groupOpen by remember { mutableStateOf(false) }
    val power = remember { FocusRequester() }
    val connectedId = (state as? VpnState.On)?.serverId ?: (state as? VpnState.Reconnecting)?.serverId
    val current = connectedId?.let { repo.find(it) } ?: repo.find(data.selectedId)
    val group = current?.let { c -> repo.groups(data).firstOrNull { g -> g.servers.any { it.id == c.id } } }
    val sub = group?.subscription ?: data.subscriptions.firstOrNull { it.announce != null }
    val celebrations = Eggs.celebrations
    var recentCelebration by remember { mutableStateOf(false) }
    LaunchedEffect(celebrations) {
        if (celebrations == 0) return@LaunchedEffect
        recentCelebration = true
        delay(8_000)
        Eggs.clearMessage()
        delay(52_000)
        recentCelebration = false
    }

    Box(Modifier.fillMaxSize()) {
        // Снег — зимой всегда, в остальное время — минуту после пасхалки.
        if (Palette.winter || recentCelebration) Snowfall(burst = celebrations)

        // Вход на страницу (вправо из меню) — всегда на кнопку питания, а не на ближайшую по высоте карточку.
        Row(
            Modifier.fillMaxSize().focusProperties { onEnter = { power.requestFocus() } }.focusGroup(),
            horizontalArrangement = Arrangement.spacedBy(40.dp), verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.width(280.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Season.holidayGreeting()?.let { Text(it, fontSize = 22.sp, color = Palette.Accent, modifier = Modifier.padding(bottom = 16.dp)) }
                PowerButton(state, power) {
                    when (state) {
                        is VpnState.On, is VpnState.Connecting, is VpnState.Reconnecting -> actions.disconnect()
                        else -> actions.connect(current?.id)
                    }
                }
                Text(
                    when (val s = state) {
                        VpnState.Off -> "Отключено"
                        is VpnState.Connecting -> s.step + "…"
                        is VpnState.On -> "Подключено"
                        is VpnState.Reconnecting -> "Переподключение…"
                        is VpnState.Failed -> "Не подключилось"
                    },
                    fontSize = 24.sp, color = stateColor(state), modifier = Modifier.padding(top = 24.dp),
                )
                (state as? VpnState.Failed)?.let { Hint(it.message, Palette.Error, Modifier.fillMaxWidth()) }
                (state as? VpnState.Reconnecting)?.let {
                    Hint(
                        "Нет связи с сервером. Интернет закрыт, пока связь не вернётся" +
                            if (it.attempt > 0) " · попытка ${it.attempt}" else "",
                        Palette.Warn, Modifier.fillMaxWidth(),
                    )
                }
                if (state is VpnState.On) SessionTimer((state as VpnState.On).since)
            }

            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                Eggs.message?.let { Text(it, fontSize = 18.sp, color = Palette.Accent) }
                Text("Сервер", fontSize = 16.sp, color = Palette.Text2)
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    Surface(
                        onClick = openServers,
                        modifier = Modifier.weight(1f),
                        shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(16.dp)),
                        colors = ClickableSurfaceDefaults.colors(containerColor = Palette.Surface, focusedContainerColor = Palette.Surface3),
                        scale = ClickableSurfaceDefaults.scale(focusedScale = 1.02f),
                    ) {
                        Column(Modifier.padding(22.dp)) {
                            if (current == null) Text("Не выбран — откройте «Серверы»", fontSize = 22.sp, color = Palette.Text2)
                            else {
                                FlagName(current.name, fontSize = 26.sp)
                                val ping = pings[current.id]
                                Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                                    Text(current.protocolLabel, fontSize = 16.sp, color = Palette.Text2)
                                    if (ping != null) Text(
                                        if (ping.ok) "${ping.milliseconds} мс" else ping.error ?: "нет ответа",
                                        fontSize = 16.sp, color = pingColor(ping.milliseconds),
                                    )
                                }
                                group?.let { Text(it.title, fontSize = 14.sp, color = Palette.Text3, modifier = Modifier.padding(top = 6.dp)) }
                            }
                        }
                    }
                    // Серверы той же подписки (0.8.3 на ПК): выбрать или переключиться, не уходя с главной.
                    if (group != null) CupButton(serversCount(group.servers.size)) { groupOpen = true }
                }

                if (state is VpnState.On) {
                    Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        StatCard("Загрузка", formatSpeed(stats.downSpeed), Palette.Accent, Modifier.weight(1f))
                        StatCard("Отдача", formatSpeed(stats.upSpeed), Palette.Up, Modifier.weight(1f))
                        StatCard("За сеанс", formatBytes(stats.downBytes + stats.upBytes), Palette.Text1, Modifier.weight(1f))
                    }
                    StatCard("Внешний IP", stats.publicIp ?: "проверяю…", Palette.Text1, Modifier.fillMaxWidth())
                }

                UpdateBanner(actions)

                sub?.announce?.let { announce ->
                    Column(Modifier.fillMaxWidth().padding(top = 6.dp)) {
                        Text("Объявление · ${sub.name}", fontSize = 14.sp, color = Palette.Warn)
                        Text(announce, fontSize = 17.sp, color = Palette.Text1, modifier = Modifier.padding(top = 4.dp))
                    }
                }
            }
        }
    }

    if (groupOpen && group != null) {
        GroupServersDialog(repo, group, current, connectedId, actions) { groupOpen = false }
    }
}

private fun serversCount(n: Int): String {
    val word = when {
        n % 100 in 11..14 -> "серверов"
        n % 10 == 1 -> "сервер"
        n % 10 in 2..4 -> "сервера"
        else -> "серверов"
    }
    return "$n $word"
}

/** Панель с серверами группы текущего сервера: трафик и срок подписки, пинг группы, обновление, переключение. */
@Composable
private fun GroupServersDialog(
    repo: Repository,
    group: ServerGroup,
    current: Profile?,
    connectedId: String?,
    actions: UiActions,
    onDismiss: () -> Unit,
) {
    val settings by repo.settings.collectAsState()
    val pings by repo.pings.collectAsState()
    val pinging by repo.pinging.collectAsState()
    val scope = rememberCoroutineScope()
    var updating by remember { mutableStateOf(false) }
    val sub = group.subscription
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(shape = RoundedCornerShape(20.dp), colors = SurfaceDefaults.colors(containerColor = Palette.Surface2)) {
            Column(Modifier.fillMaxWidth(0.72f).heightIn(max = 480.dp).padding(28.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(group.title, fontSize = 26.sp, color = Palette.Text1)
                sub?.let { s -> subscriptionLine(s)?.let { Hint(it, Palette.Text2) } }
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    CupButton(if (pinging) "Пинг…" else "Пинг") { scope.launch { repo.pingAll(group.servers) } }
                    if (sub != null) CupButton(if (updating) "Обновляю…" else "Обновить подписку") {
                        if (!updating) scope.launch {
                            updating = true
                            try {
                                repo.say(repo.updateSubscription(sub))
                            } finally {
                                updating = false
                            }
                        }
                    }
                    CupButton("Закрыть") { onDismiss() }
                }
                LazyColumn(Modifier.padding(top = 6.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    items(sorted(group, settings.sortOrder, pings, repo), key = { it.id }) { p ->
                        ServerRow(
                            p, pings[p.id],
                            selected = p.id == current?.id,
                            connected = p.id == connectedId,
                            onClick = {
                                // VPN включён — переключение; выключен — только выбор (как в меню трея на ПК).
                                if (p.id != current?.id) {
                                    if (connectedId != null) actions.connect(p.id) else repo.select(p.id)
                                }
                                onDismiss()
                            },
                            onLongClick = {},
                        )
                    }
                }
            }
        }
    }
}

/** Кнопка питания: цвет — состояние; при подключении и переподключении вокруг крутится дуга. Фокус по умолчанию — на ней. */
@Composable
private fun PowerButton(state: VpnState, focus: FocusRequester, onClick: () -> Unit) {
    val color by animateColorAsState(stateColor(state), tween(400), label = "power")
    LaunchedEffect(Unit) { runCatching { focus.requestFocus() } }
    val spin = rememberInfiniteTransition(label = "spin")
    val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(1100, easing = LinearEasing), RepeatMode.Restart), label = "angle")
    Box(contentAlignment = Alignment.Center, modifier = Modifier.size(260.dp)) {
        if (state is VpnState.Connecting || state is VpnState.Reconnecting) {
            Canvas(Modifier.size(260.dp)) {
                drawArc(
                    color = color, startAngle = angle, sweepAngle = 90f, useCenter = false,
                    topLeft = Offset(6f, 6f), size = Size(size.width - 12f, size.height - 12f),
                    style = Stroke(width = 10f, cap = StrokeCap.Round),
                )
            }
        }
        Surface(
            onClick = onClick,
            modifier = Modifier.size(220.dp).focusRequester(focus),
            shape = ClickableSurfaceDefaults.shape(CircleShape),
            colors = ClickableSurfaceDefaults.colors(containerColor = Palette.Surface2, focusedContainerColor = Palette.Surface3),
            border = ClickableSurfaceDefaults.border(
                border = androidx.tv.material3.Border(androidx.compose.foundation.BorderStroke(4.dp, color.copy(alpha = 0.6f))),
                focusedBorder = androidx.tv.material3.Border(androidx.compose.foundation.BorderStroke(6.dp, color)),
            ),
            scale = ClickableSurfaceDefaults.scale(focusedScale = 1.05f),
        ) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Canvas(Modifier.size(90.dp)) {
                    val w = size.width
                    drawArc(
                        color = color, startAngle = -60f, sweepAngle = 300f, useCenter = false,
                        topLeft = Offset(w * 0.1f, w * 0.12f), size = Size(w * 0.8f, w * 0.8f),
                        style = Stroke(width = w * 0.09f, cap = StrokeCap.Round),
                    )
                    drawLine(color, Offset(w / 2, w * 0.02f), Offset(w / 2, w * 0.45f), strokeWidth = w * 0.09f, cap = StrokeCap.Round)
                }
            }
        }
    }
}

@Composable
private fun SessionTimer(since: Long) {
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(since) {
        while (true) {
            now = System.currentTimeMillis()
            delay(1000)
        }
    }
    Text(formatDuration(now - since), fontSize = 18.sp, color = Palette.Text2, modifier = Modifier.padding(top = 6.dp))
}

@Composable
private fun StatCard(label: String, value: String, color: androidx.compose.ui.graphics.Color, modifier: Modifier) {
    Surface(modifier = modifier, shape = RoundedCornerShape(14.dp), colors = SurfaceDefaults.colors(containerColor = Palette.Surface)) {
        Column(Modifier.padding(horizontal = 18.dp, vertical = 14.dp)) {
            Text(label, fontSize = 14.sp, color = Palette.Text2)
            Text(value, fontSize = 20.sp, color = color, maxLines = 1, softWrap = false, modifier = Modifier.padding(top = 4.dp))
        }
    }
}
