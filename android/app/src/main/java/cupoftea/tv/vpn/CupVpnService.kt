package cupoftea.tv.vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.util.Log
import cupoftea.tv.MainActivity
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.io.File

/** Состояние подключения — общее для службы и интерфейса (один процесс). */
sealed interface VpnState {
    data object Off : VpnState
    data object Connecting : VpnState
    data class On(val server: String, val since: Long) : VpnState
    data class Failed(val message: String) : VpnState
}

class CupVpnService : VpnService() {
    companion object {
        const val MTU = cupoftea.core.config.XrayConfigBuilder.TUN_MTU
        const val ACTION_CONNECT = "cupoftea.tv.CONNECT"
        const val ACTION_DISCONNECT = "cupoftea.tv.DISCONNECT"
        const val EXTRA_INDEX = "index"
        private const val TAG = "CupVpn"
        private const val CHANNEL = "vpn"
        private const val NOTIFICATION_ID = 1

        private val _state = MutableStateFlow<VpnState>(VpnState.Off)
        val state: StateFlow<VpnState> = _state

        fun spikeFile(context: Context) = File(context.getExternalFilesDir(null), "spike.json")
        fun logFile(context: Context) = File(context.filesDir, "xray.log")
    }

    private var tun: ParcelFileDescriptor? = null
    private var core: libcup.Instance? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_DISCONNECT -> stop()
            // Без action служба запускается системой («постоянный VPN») — подключаемся к первому серверу
            else -> start(intent?.getIntExtra(EXTRA_INDEX, 0) ?: 0)
        }
        return START_NOT_STICKY
    }

    override fun onRevoke() {
        // Пользователь включил другой VPN или отозвал разрешение
        stop()
    }

    override fun onDestroy() {
        closeAll()
        super.onDestroy()
    }

    private fun start(index: Int) {
        startForegroundCompat(notification("Подключение…"))
        _state.value = VpnState.Connecting
        Thread {
            try {
                closeAll()
                val text = spikeFile(this).readText()
                val name = SpikeConfig.names(text)[index]
                val log = logFile(this).apply { delete() }
                val config = SpikeConfig.build(text, index, log.absolutePath)

                val pfd = Builder()
                    .setSession(name)
                    .setMtu(MTU)
                    .addAddress("172.19.0.1", 30)
                    .addRoute("0.0.0.0", 0)
                    .addAddress("fdfe:dcba:9876::1", 126)
                    .addRoute("::", 0)
                    .addDnsServer("172.19.0.2")
                    // Сама программа (xray) идёт мимо туннеля — иначе петля
                    .addDisallowedApplication(packageName)
                    .apply { if (Build.VERSION.SDK_INT >= 29) setMetered(false) }
                    .establish() ?: throw IllegalStateException("нет разрешения на VPN")
                tun = pfd

                libcup.Libcup.setMemoryLimit(96)
                core = libcup.Libcup.start(config, filesDir.absolutePath, pfd.fd.toLong())
                Log.i(TAG, "xray ${libcup.Libcup.version()} подключён: $name")
                _state.value = VpnState.On(name, System.currentTimeMillis())
                startForegroundCompat(notification("Подключено: $name"))
            } catch (e: Throwable) {
                Log.e(TAG, "не подключился", e)
                closeAll()
                _state.value = VpnState.Failed(e.message ?: e.javaClass.simpleName)
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }.start()
    }

    private fun stop() {
        closeAll()
        _state.value = VpnState.Off
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun closeAll() {
        try {
            core?.close()
        } catch (e: Exception) {
            Log.w(TAG, "xray не остановился", e)
        }
        core = null
        try {
            tun?.close()
        } catch (_: Exception) {
        }
        tun = null
    }

    /** Байты через сервер с прошлого вызова: вверх и вниз. */
    fun traffic(): Pair<Long, Long> {
        val c = core ?: return 0L to 0L
        return c.counter("inbound>>>tun>>>traffic>>>uplink", true).coerceAtLeast(0) to
            c.counter("inbound>>>tun>>>traffic>>>downlink", true).coerceAtLeast(0)
    }

    private fun startForegroundCompat(n: Notification) {
        if (Build.VERSION.SDK_INT >= 34)
            startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED)
        else
            startForeground(NOTIFICATION_ID, n)
    }

    private fun notification(text: String): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26 && nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "VPN", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
        )
        val builder = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CHANNEL)
        else @Suppress("DEPRECATION") Notification.Builder(this)
        return builder
            .setContentTitle("The Cup of Tea")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_lock_lock)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }
}
