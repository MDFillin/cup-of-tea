package cupoftea.tv

import android.net.VpnService
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import androidx.tv.material3.darkColorScheme
import cupoftea.core.profiles.Flags
import cupoftea.tv.data.Repository
import cupoftea.tv.vpn.CupVpnService
import cupoftea.tv.vpn.VpnState
import kotlinx.coroutines.launch

/**
 * Временный экран этапа 2 (служба VPN): серверы из хранилища по группам, пинг, подключение, скорость и IP.
 * Настоящий интерфейс под пульт — следующий этап.
 */
class MainActivity : ComponentActivity() {
    private var pendingServer: String? = null
    private lateinit var repo: Repository

    private val askVpn = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (it.resultCode == RESULT_OK) CupVpnService.connect(this, pendingServer)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        repo = Repository.get(this)
        setContent { MaterialTheme(colorScheme = darkColorScheme()) { Screen() } }
    }

    private fun connect(serverId: String) {
        val ask = VpnService.prepare(this)
        if (ask != null) {
            pendingServer = serverId
            askVpn.launch(ask)
        } else CupVpnService.connect(this, serverId)
    }

    @Composable
    private fun Screen() {
        val state by CupVpnService.state.collectAsState()
        val stats by CupVpnService.stats.collectAsState()
        val data by repo.data.collectAsState()
        val pings by repo.pings.collectAsState()
        val pinging by repo.pinging.collectAsState()
        val message by repo.message.collectAsState()
        val scope = rememberCoroutineScope()
        val groups = repo.groups(data)

        Row(Modifier.fillMaxSize().background(Color(0xFF16181A)).padding(40.dp), horizontalArrangement = Arrangement.spacedBy(32.dp)) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("The Cup of Tea", fontSize = 28.sp, color = Color(0xFFB5E853))
                Text(
                    when (val s = state) {
                        VpnState.Off -> "Отключено"
                        is VpnState.Connecting -> "${s.server}: ${s.step}…"
                        is VpnState.On -> "Подключено: ${s.server}"
                        is VpnState.Failed -> "Ошибка: ${s.message}"
                    },
                    fontSize = 20.sp, color = Color.White,
                )
                if (state is VpnState.On) {
                    Text(
                        "↓ ${speed(stats.downSpeed)}  ↑ ${speed(stats.upSpeed)}   всего ${mb(stats.downBytes + stats.upBytes)}" +
                            (stats.publicIp?.let { "   IP $it" } ?: "") + if (stats.serverDown) "   сервер не отвечает" else "",
                        fontSize = 16.sp, color = Color(0xFFB0B0B0),
                    )
                }
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Button(onClick = { CupVpnService.disconnect(this@MainActivity) }) { Text("Отключить") }
                    Button(onClick = { scope.launch { repo.pingAll() } }) { Text(if (pinging) "Пинг…" else "Пинг всех") }
                    Button(onClick = { scope.launch { repo.updateDueSubscriptions() } }) { Text("Обновить подписки") }
                }
                message?.let { Text(it, fontSize = 14.sp, color = Color(0xFFE0C060)) }
                if (groups.isEmpty()) Text("Серверов нет. Для отладки: adb push import.txt в папку программы и перезапуск.", color = Color(0xFFFF8080))
                LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (g in groups) {
                        item(key = "g-" + g.id) {
                            Text(
                                g.title + (g.subscription?.lastError?.let { " — ошибка: $it" } ?: ""),
                                fontSize = 18.sp, color = Color(0xFFB5E853), modifier = Modifier.padding(top = 8.dp),
                            )
                        }
                        items(g.servers, key = { g.id + it.id }) { p ->
                            val ping = pings[p.id]
                            val selected = p.id == data.selectedId
                            Button(onClick = { connect(p.id) }) {
                                Text(
                                    (if (selected) "● " else "") + Flags.strip(p.name) + "  ·  " + p.protocolLabel +
                                        (ping?.let { if (it.ok) "  ·  ${it.milliseconds} мс" else "  ·  ${it.error}" } ?: ""),
                                    fontSize = 15.sp,
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    private fun speed(bytesPerSecond: Long) = when {
        bytesPerSecond >= 1_000_000 -> "%.1f Мбит/с".format(bytesPerSecond * 8 / 1_000_000.0)
        else -> "%d Кбит/с".format(bytesPerSecond * 8 / 1000)
    }

    private fun mb(bytes: Long) = "%.1f МБ".format(bytes / 1_048_576.0)
}
