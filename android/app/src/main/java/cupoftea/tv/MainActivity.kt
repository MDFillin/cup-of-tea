package cupoftea.tv

import android.net.VpnService
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import cupoftea.tv.data.Repository
import cupoftea.tv.ui.AppRoot
import cupoftea.tv.ui.UiActions
import cupoftea.tv.vpn.CupVpnService

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
        )
        setContent { AppRoot(repo, actions) }
    }

    private fun connect(serverId: String?) {
        val ask = VpnService.prepare(this)
        if (ask != null) {
            pendingServer = serverId
            askVpn.launch(ask)
        } else CupVpnService.connect(this, serverId)
    }
}
