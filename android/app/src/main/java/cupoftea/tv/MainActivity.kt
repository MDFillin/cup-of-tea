package cupoftea.tv

import android.content.Intent
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
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import androidx.tv.material3.darkColorScheme
import cupoftea.tv.vpn.CupVpnService
import cupoftea.tv.vpn.SpikeConfig
import cupoftea.tv.vpn.VpnState
import kotlinx.coroutines.delay

/** Проба (этап 0): список серверов из spike.json, подключение и журнал xray. */
class MainActivity : ComponentActivity() {
    private var pendingIndex = 0

    private val askVpn = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (it.resultCode == RESULT_OK) connect(pendingIndex)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                Spike(
                    onConnect = { index ->
                        val ask = VpnService.prepare(this)
                        if (ask != null) {
                            pendingIndex = index
                            askVpn.launch(ask)
                        } else connect(index)
                    },
                    onDisconnect = {
                        startService(Intent(this, CupVpnService::class.java).setAction(CupVpnService.ACTION_DISCONNECT))
                    }
                )
            }
        }
    }

    private fun connect(index: Int) {
        val intent = Intent(this, CupVpnService::class.java)
            .setAction(CupVpnService.ACTION_CONNECT)
            .putExtra(CupVpnService.EXTRA_INDEX, index)
        if (android.os.Build.VERSION.SDK_INT >= 26) startForegroundService(intent) else startService(intent)
    }

    @Composable
    private fun Spike(onConnect: (Int) -> Unit, onDisconnect: () -> Unit) {
        val state by CupVpnService.state.collectAsState()
        val names = remember {
            runCatching { SpikeConfig.names(CupVpnService.spikeFile(this).readText()) }.getOrElse { emptyList() }
        }
        var log by remember { mutableStateOf("") }
        LaunchedEffect(Unit) {
            while (true) {
                log = runCatching { CupVpnService.logFile(this@MainActivity).readLines().takeLast(12).joinToString("\n") }
                    .getOrDefault("")
                delay(1000)
            }
        }
        Row(
            Modifier.fillMaxSize().background(Color(0xFF16181A)).padding(48.dp),
            horizontalArrangement = Arrangement.spacedBy(32.dp)
        ) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("The Cup of Tea — проба", fontSize = 28.sp, color = Color(0xFFB5E853))
                Text(
                    when (val s = state) {
                        VpnState.Off -> "Отключено"
                        VpnState.Connecting -> "Подключение…"
                        is VpnState.On -> "Подключено: ${s.server}"
                        is VpnState.Failed -> "Ошибка: ${s.message}"
                    },
                    fontSize = 20.sp, color = Color.White
                )
                Button(onClick = onDisconnect) { Text("Отключить") }
                if (names.isEmpty())
                    Text("Нет ${CupVpnService.spikeFile(this@MainActivity)}", color = Color(0xFFFF8080))
                LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    itemsIndexed(names) { i, name ->
                        Button(onClick = { onConnect(i) }) { Text(name) }
                    }
                }
            }
            Text(log, Modifier.weight(1f), fontSize = 12.sp, color = Color(0xFFB0B0B0))
        }
    }
}
