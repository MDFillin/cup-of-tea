package cupoftea.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ListItem
import androidx.tv.material3.Text
import cupoftea.tv.data.Repository
import cupoftea.tv.vpn.CupVpnService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

/** Журнал ядра (предупреждения и ошибки xray) и последние сообщения программы. Обновляется раз в 2 с. */
@Composable
fun LogScreen(repo: Repository) {
    val context = LocalContext.current
    val message by repo.message.collectAsState()
    var lines by remember { mutableStateOf(emptyList<String>()) }
    LaunchedEffect(Unit) {
        while (true) {
            lines = withContext(Dispatchers.IO) {
                try {
                    CupVpnService.logFile(context).takeIf { it.exists() }?.readLines()?.takeLast(200)?.reversed() ?: emptyList()
                } catch (e: Exception) {
                    emptyList()
                }
            }
            delay(2000)
        }
    }
    LazyColumn(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        item { ScreenTitle("Журнал", "Новые записи сверху") }
        message?.let { item { Hint(it, Palette.Warn) } }
        if (lines.isEmpty()) item { Hint("Записей пока нет: журнал пишется, пока VPN включён.", Palette.Text2) }
        // Строки фокусируемые — иначе пультом длинный журнал не прокрутить.
        items(lines) { line ->
            ListItem(
                selected = false, onClick = {},
                headlineContent = { Text(line, fontSize = 13.sp) },
                colors = rowColors(),
            )
        }
    }
}
