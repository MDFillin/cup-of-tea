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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Surface
import androidx.tv.material3.Text
import cupoftea.tv.data.Repository
import cupoftea.tv.vpn.CupVpnService
import cupoftea.tv.vpn.VpnState
import kotlinx.coroutines.delay

@Composable
fun HomeScreen(repo: Repository, actions: UiActions, openServers: () -> Unit) {
    val state by CupVpnService.state.collectAsState()
    val stats by CupVpnService.stats.collectAsState()
    val data by repo.data.collectAsState()
    val pings by repo.pings.collectAsState()
    val selected = repo.find(data.selectedId)
    val current = (state as? VpnState.On)?.serverId?.let { repo.find(it) } ?: selected
    val sub = data.subscriptions.firstOrNull { it.id == current?.subscriptionId } ?: data.subscriptions.firstOrNull { it.announce != null }

    Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(56.dp), verticalAlignment = Alignment.CenterVertically) {
        // Кнопка питания
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            PowerButton(state) {
                when (state) {
                    is VpnState.On, is VpnState.Connecting -> actions.disconnect()
                    else -> actions.connect(current?.id)
                }
            }
            Text(
                when (val s = state) {
                    VpnState.Off -> "Отключено"
                    is VpnState.Connecting -> s.step + "…"
                    is VpnState.On -> "Подключено"
                    is VpnState.Failed -> "Не подключилось"
                },
                fontSize = 24.sp, color = stateColor(state), modifier = Modifier.padding(top = 24.dp),
            )
            (state as? VpnState.Failed)?.let { Hint(it.message, Palette.Error, Modifier.width(320.dp)) }
            if (state is VpnState.On) SessionTimer((state as VpnState.On).since)
        }

        // Сервер и статистика
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Text("Сервер", fontSize = 16.sp, color = Palette.Text2)
            Surface(
                onClick = openServers,
                modifier = Modifier.fillMaxWidth(),
                shape = ClickableSurfaceDefaults.shape(RoundedCornerShape(16.dp)),
                colors = ClickableSurfaceDefaults.colors(
                    containerColor = Palette.Surface, focusedContainerColor = Palette.Surface3,
                ),
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
                        sub?.let { Text(it.name, fontSize = 14.sp, color = Palette.Text3, modifier = Modifier.padding(top = 6.dp)) }
                    }
                }
            }

            if (state is VpnState.On) {
                Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    StatCard("Загрузка", formatSpeed(stats.downSpeed), Palette.Accent, Modifier.weight(1f))
                    StatCard("Отдача", formatSpeed(stats.upSpeed), Palette.Up, Modifier.weight(1f))
                    StatCard("За сеанс", formatBytes(stats.downBytes + stats.upBytes), Palette.Text1, Modifier.weight(1f))
                }
                StatCard("Внешний IP", stats.publicIp ?: "проверяю…", Palette.Text1, Modifier.fillMaxWidth())
                if (stats.serverDown) Hint("Сервер не отвечает: попробуйте другой в «Серверах»", Palette.Error)
            }

            UpdateBanner(actions)

            sub?.announce?.let { announce ->
                Box(Modifier.fillMaxWidth().padding(top = 6.dp)) {
                    Column(
                        Modifier.fillMaxWidth()
                            .then(Modifier.padding(0.dp))
                    ) {
                        Text("Объявление · ${sub.name}", fontSize = 14.sp, color = Palette.Warn)
                        Text(announce, fontSize = 17.sp, color = Palette.Text1, modifier = Modifier.padding(top = 4.dp))
                    }
                }
            }
        }
    }
}

/** Кнопка питания: цвет — состояние; при подключении вокруг крутится дуга. Фокус по умолчанию — на ней. */
@Composable
private fun PowerButton(state: VpnState, onClick: () -> Unit) {
    val color by animateColorAsState(stateColor(state), tween(400), label = "power")
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) {
        try {
            focus.requestFocus()
        } catch (_: Exception) {
        }
    }
    val spin = rememberInfiniteTransition(label = "spin")
    val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(1100, easing = LinearEasing), RepeatMode.Restart), label = "angle")
    Box(contentAlignment = Alignment.Center, modifier = Modifier.size(260.dp)) {
        if (state is VpnState.Connecting) {
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
            colors = ClickableSurfaceDefaults.colors(
                containerColor = Palette.Surface2, focusedContainerColor = Palette.Surface3,
            ),
            border = ClickableSurfaceDefaults.border(
                border = androidx.tv.material3.Border(androidx.compose.foundation.BorderStroke(4.dp, color.copy(alpha = 0.6f))),
                focusedBorder = androidx.tv.material3.Border(androidx.compose.foundation.BorderStroke(6.dp, color)),
            ),
            scale = ClickableSurfaceDefaults.scale(focusedScale = 1.05f),
        ) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Canvas(Modifier.size(90.dp)) {
                    val w = size.width
                    // Значок питания: дуга с разрывом сверху и черта
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
    Surface(modifier = modifier, shape = RoundedCornerShape(14.dp), colors = androidx.tv.material3.SurfaceDefaults.colors(containerColor = Palette.Surface)) {
        Column(Modifier.padding(horizontal = 18.dp, vertical = 14.dp)) {
            Text(label, fontSize = 14.sp, color = Palette.Text2)
            Text(value, fontSize = 20.sp, color = color, maxLines = 1, softWrap = false, modifier = Modifier.padding(top = 4.dp))
        }
    }
}
