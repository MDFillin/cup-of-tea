package cupoftea.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.ListItem
import androidx.tv.material3.Surface
import androidx.tv.material3.SurfaceDefaults
import androidx.tv.material3.Text
import cupoftea.core.profiles.Profile
import cupoftea.core.profiles.Subscription
import cupoftea.tv.data.Repository
import cupoftea.tv.data.ServerGroup
import cupoftea.tv.vpn.CupVpnService
import cupoftea.tv.vpn.VpnState
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

private val sortTitles = mapOf("subscription" to "как в подписке", "ping" to "по пингу", "name" to "по имени")
private val sortShort = mapOf("subscription" to "↕ Подписка", "ping" to "↕ Пинг", "name" to "↕ Имя")

@Composable
fun ServersScreen(repo: Repository, actions: UiActions) {
    val data by repo.data.collectAsState()
    val settings by repo.settings.collectAsState()
    val pings by repo.pings.collectAsState()
    val pinging by repo.pinging.collectAsState()
    val message by repo.message.collectAsState()
    val state by CupVpnService.state.collectAsState()
    val scope = rememberCoroutineScope()
    var menuFor by remember { mutableStateOf<Profile?>(null) }
    var updating by remember { mutableStateOf(false) }
    var phone by remember { mutableStateOf(false) }
    val groups = repo.groups(data)
    val connectedId = (state as? VpnState.On)?.serverId

    Column {
        ScreenTitle("Серверы", "OK — подключиться, долгое нажатие — меню сервера · порядок: ${sortTitles[settings.sortOrder]}")
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            CupButton("Добавить") { phone = true }
            CupButton(if (pinging) "Пинг…" else "Пинг") { scope.launch { repo.pingAll() } }
            CupButton(if (updating) "Обновляю…" else "Обновить") {
                if (!updating) scope.launch {
                    updating = true
                    try {
                        repo.say(data.subscriptions.map { repo.updateSubscription(it) }.joinToString("; ").ifEmpty { "Подписок нет" })
                        repo.pingAll()
                    } finally {
                        updating = false
                    }
                }
            }
            CupButton(sortShort[settings.sortOrder] ?: "↕ Подписка") {
                val keys = sortTitles.keys.toList()
                repo.updateSettings { it.copy(sortOrder = keys[(keys.indexOf(it.sortOrder) + 1) % keys.size]) }
            }
            val allCollapsed = groups.isNotEmpty() && groups.all { it.id in settings.collapsedGroups }
            CupButton(if (allCollapsed) "Развернуть" else "Свернуть") {
                repo.updateSettings { s -> s.copy(collapsedGroups = if (allCollapsed) emptySet() else groups.map { it.id }.toSet()) }
            }
        }
        message?.let { Hint(it, Palette.Warn, Modifier.padding(top = 8.dp)) }
        if (groups.isEmpty()) {
            Hint("Серверов пока нет. Нажмите «Добавить» — ссылку подписки можно вставить с телефона.", Palette.Text2, Modifier.padding(top = 24.dp))
        }

        LazyColumn(Modifier.padding(top = 12.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            for (group in groups) {
                val collapsed = group.id in settings.collapsedGroups
                item(key = "g-" + group.id) {
                    GroupHeader(group, collapsed) {
                        repo.updateSettings { s ->
                            s.copy(collapsedGroups = if (collapsed) s.collapsedGroups - group.id else s.collapsedGroups + group.id)
                        }
                    }
                }
                if (!collapsed) {
                    items(sorted(group, settings.sortOrder, pings, repo), key = { group.id + "/" + it.id }) { p ->
                        ServerRow(
                            p,
                            ping = pings[p.id],
                            selected = p.id == data.selectedId,
                            connected = p.id == connectedId,
                            onClick = { actions.connect(p.id) },
                            onLongClick = { menuFor = p },
                        )
                    }
                }
            }
        }
    }

    if (phone) PhoneDialog { phone = false }

    menuFor?.let { p ->
        ServerMenu(
            p,
            canDelete = p.subscriptionId == null && !repo.isAuto(p),
            onDismiss = { menuFor = null },
            onPing = { scope.launch { repo.pingAll(listOf(p)) } },
            onSelect = { repo.select(p.id) },
            onDelete = { repo.removeServer(p.id) },
        )
    }
}

/** Автовыбор — всегда первым; остальные — по выбранной сортировке. */
private fun sorted(group: ServerGroup, order: String, pings: Map<String, cupoftea.core.sessions.DelayResult>, repo: Repository): List<Profile> {
    val (auto, rest) = group.servers.partition(repo::isAuto)
    val effective = if (order == "subscription") group.subscription?.sortOrder ?: "none" else order
    val sorted = when (effective) {
        "ping" -> rest.sortedBy { pings[it.id]?.milliseconds ?: Int.MAX_VALUE }
        "name" -> rest.sortedBy { cupoftea.core.profiles.Flags.strip(it.name).lowercase() }
        else -> rest
    }
    return auto + sorted
}

@Composable
private fun GroupHeader(group: ServerGroup, collapsed: Boolean, onToggle: () -> Unit) {
    val sub = group.subscription
    ListItem(
        selected = false,
        onClick = onToggle,
        modifier = Modifier.padding(top = 14.dp, bottom = 4.dp),
        headlineContent = {
            Text((if (collapsed) "▸  " else "▾  ") + group.title + "  ·  " + group.servers.count { true }, fontSize = 20.sp)
        },
        supportingContent = sub?.let { s -> subscriptionLine(s)?.let { { Text(it, fontSize = 14.sp) } } },
        colors = androidx.tv.material3.ListItemDefaults.colors(
            containerColor = Palette.Bg, contentColor = Palette.Accent,
            focusedContainerColor = Palette.Surface3, focusedContentColor = Palette.Accent,
        ),
    )
}

/** «Использовано 12.3 ГБ из 60 ГБ · до 01.12.2026 · ошибка: …» */
private fun subscriptionLine(s: Subscription): String? {
    val parts = mutableListOf<String>()
    val used = s.upload + s.download
    if (s.total > 0) parts.add("использовано ${formatBytes(used)} из ${formatBytes(s.total)}")
    else if (used > 0) parts.add("использовано ${formatBytes(used)}")
    s.expire?.let { parts.add("до " + SimpleDateFormat("dd.MM.yyyy", Locale.getDefault()).format(Date(it * 1000))) }
    parts.addAll(s.notes.take(2))
    s.lastError?.let { parts.add("ошибка: $it") }
    return parts.takeIf { it.isNotEmpty() }?.joinToString("  ·  ")
}

@Composable
private fun ServerRow(
    p: Profile,
    ping: cupoftea.core.sessions.DelayResult?,
    selected: Boolean,
    connected: Boolean,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
) {
    ListItem(
        selected = selected,
        onClick = onClick,
        onLongClick = onLongClick,
        modifier = Modifier.padding(start = 20.dp, top = 2.dp, bottom = 2.dp),
        headlineContent = { FlagName(p.name, fontSize = 19.sp, color = androidx.compose.ui.graphics.Color.Unspecified) },
        supportingContent = { Text(p.protocolLabel + ((p as? cupoftea.core.profiles.XrayConfigProfile)?.description?.let { " · $it" } ?: ""), fontSize = 14.sp) },
        leadingContent = {
            Text(if (connected) "●" else if (selected) "○" else " ", fontSize = 16.sp, color = if (connected) Palette.Accent else Palette.Text2)
        },
        trailingContent = {
            if (ping != null) Text(
                if (ping.ok) "${ping.milliseconds} мс" else "нет ответа",
                fontSize = 16.sp, color = pingColor(ping.milliseconds),
            )
        },
        colors = rowColors(),
    )
}

@Composable
private fun ServerMenu(
    p: Profile,
    canDelete: Boolean,
    onDismiss: () -> Unit,
    onPing: () -> Unit,
    onSelect: () -> Unit,
    onDelete: () -> Unit,
) {
    Dialog(onDismissRequest = onDismiss) {
        Surface(shape = RoundedCornerShape(18.dp), colors = SurfaceDefaults.colors(containerColor = Palette.Surface2)) {
            Column(Modifier.width(520.dp).padding(24.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                FlagName(p.name, fontSize = 22.sp)
                Hint(p.protocolLabel, Palette.Text2)
                SettingRow("Проверить задержку") { onPing(); onDismiss() }
                SettingRow("Выбрать без подключения") { onSelect(); onDismiss() }
                if (canDelete) SettingRow("Удалить сервер") { onDelete(); onDismiss() }
                SettingRow("Закрыть") { onDismiss() }
            }
        }
    }
}
