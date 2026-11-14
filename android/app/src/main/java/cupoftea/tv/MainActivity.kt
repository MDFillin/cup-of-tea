package cupoftea.tv

import android.content.Intent
import android.net.VpnService
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.lifecycleScope
import cupoftea.core.TvRelease
import cupoftea.tv.data.Repository
import cupoftea.tv.ui.AppRoot
import cupoftea.tv.ui.UiActions
import cupoftea.tv.update.UpdateState
import cupoftea.tv.update.Updates
import cupoftea.tv.vpn.CupVpnService
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private var pendingServer: String? = null
    private lateinit var repo: Repository

    /** Системный запрос «Разрешить VPN» — при первом подключении и после того, как разрешение отозвали. */
    private val askVpn = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (it.resultCode == RESULT_OK) CupVpnService.connect(this, pendingServer)
        else repo.say("Без разрешения на VPN подключиться нельзя")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        repo = Repository.get(this)
        val actions = UiActions(
            connect = ::connect,
            disconnect = { CupVpnService.disconnect(this) },
            reapply = { CupVpnService.reconnectIfOn(this) },
            installUpdate = ::installUpdate,
        )
        setContent { AppRoot(repo, actions) }
        handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /** Нажали на уведомление о новой версии — сразу ставим её. */
    private fun handleIntent(intent: Intent?) {
        if (intent?.action != Updates.ACTION_INSTALL) return
        lifecycleScope.launch {
            val state = Updates.state.value as? UpdateState.Available ?: Updates.check(this@MainActivity, notify = false)
            (state as? UpdateState.Available)?.let { installUpdate(it.release) }
        }
    }

    private fun installUpdate(release: TvRelease) {
        if (!Updates.canInstall(this)) {
            val opened = Updates.askInstallPermission(this)
            repo.say(
                if (opened) "Разрешите программе устанавливать приложения и нажмите «Установить» ещё раз"
                else "Разрешите установку из неизвестных источников для The Cup of Tea в настройках ТВ"
            )
            return
        }
        lifecycleScope.launch { Updates.install(this@MainActivity, release) }
    }

    private fun connect(serverId: String?) {
        // Обязательное обновление: подключиться или сменить сервер можно только после него.
        if (Updates.required != null) {
            Updates.showScreen()
            return
        }
        val ask = VpnService.prepare(this)
        if (ask != null) {
            pendingServer = serverId
            askVpn.launch(ask)
        } else CupVpnService.connect(this, serverId)
    }
}
