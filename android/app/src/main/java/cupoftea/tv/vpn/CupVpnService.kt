package cupoftea.tv.vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.IpPrefix
import android.net.Network
import android.net.NetworkCapabilities
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.util.Log
import cupoftea.core.Js
import cupoftea.core.config.ServerConfigs
import cupoftea.core.config.XrayConfigBuilder
import cupoftea.core.config.XrayMode
import cupoftea.core.config.XrayOptions
import cupoftea.core.config.isIpLiteral
import cupoftea.core.geo.Geo
import cupoftea.core.profiles.Flags
import cupoftea.core.profiles.Profile
import cupoftea.core.profiles.XrayConfigProfile
import cupoftea.core.routing.TunRoutes
import cupoftea.core.sessions.BatchPing
import cupoftea.core.sessions.Watchdog
import cupoftea.core.sessions.WatchdogAction
import cupoftea.tv.MainActivity
import cupoftea.tv.data.Repository
import cupoftea.tv.data.SplitMode
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.net.InetAddress

/** Состояние подключения — общее для службы и интерфейса (один процесс). */
sealed interface VpnState {
    data object Off : VpnState
    data class Connecting(val server: String, val step: String) : VpnState
    data class On(val serverId: String, val server: String, val since: Long) : VpnState
    data class Failed(val message: String) : VpnState
}

/** Трафик и связь, пока VPN включён. */
data class VpnStats(
    val upBytes: Long = 0, val downBytes: Long = 0,
    val upSpeed: Long = 0, val downSpeed: Long = 0,
    val publicIp: String? = null,
    /** Сервер не отвечает (сторож исчерпал перезапуски). */
    val serverDown: Boolean = false,
)

/**
 * Служба VPN: TUN от Android, xray внутри процесса со входом tun. Сама программа исключена из туннеля
 * (иначе соединения xray с сервером ушли бы в тот же туннель). Порт TunSession/служба Windows на десктопе.
 */
class CupVpnService : VpnService() {
    companion object {
        const val ACTION_CONNECT = "cupoftea.tv.CONNECT"
        const val ACTION_DISCONNECT = "cupoftea.tv.DISCONNECT"
        const val EXTRA_SERVER = "server"
        private const val TAG = "CupVpn"
        private const val CHANNEL = "vpn"
        private const val CHANNEL_ALERTS = "alerts"
        private const val NOTIFICATION_ID = 1
        private const val ALERT_ID = 2
        private const val IP_URL = "https://api.ipify.org"

        private val _state = MutableStateFlow<VpnState>(VpnState.Off)
        val state: StateFlow<VpnState> = _state

        private val _stats = MutableStateFlow(VpnStats())
        val stats: StateFlow<VpnStats> = _stats

        fun logFile(context: Context) = File(context.filesDir, "xray.log")

        /** Подключиться (или переключиться) к серверу; null — к выбранному в списке. */
        fun connect(context: Context, serverId: String? = null) {
            val intent = Intent(context, CupVpnService::class.java).setAction(ACTION_CONNECT).putExtra(EXTRA_SERVER, serverId)
            if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent) else context.startService(intent)
        }

        /** Настройки подключения изменились: если VPN включён — переподключиться с ними к тому же серверу. */
        fun reconnectIfOn(context: Context) {
            (_state.value as? VpnState.On)?.let { connect(context, it.serverId) }
        }

        fun disconnect(context: Context) {
            if (_state.value == VpnState.Off) return
            context.startService(Intent(context, CupVpnService::class.java).setAction(ACTION_DISCONNECT))
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mutex = Mutex()
    private lateinit var repo: Repository

    private var tun: ParcelFileDescriptor? = null
    private var core: libcup.Instance? = null
    private var config: String? = null
    private var profile: Profile? = null
    private var watchJob: Job? = null
    private var statsJob: Job? = null
    private val watchdog = Watchdog()
    private var network: Network? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

    override fun onCreate() {
        super.onCreate()
        repo = Repository.get(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_DISCONNECT -> scope.launch { mutex.withLock { stop(userAction = true) } }
            // Без action служба запускается системой («постоянный VPN») — подключаемся к выбранному серверу.
            else -> {
                startForegroundCompat(notification("Подключение…"))
                val id = intent?.getStringExtra(EXTRA_SERVER)
                scope.launch { mutex.withLock { start(id) } }
            }
        }
        return START_NOT_STICKY
    }

    override fun onRevoke() {
        // Включили другой VPN или отозвали разрешение.
        scope.launch { mutex.withLock { stop(userAction = true) } }
    }

    override fun onDestroy() {
        closeCore()
        closeTun()
        unwatchNetwork()
        scope.cancel()
        if (_state.value !is VpnState.Failed) _state.value = VpnState.Off
        super.onDestroy()
    }

    // --- Подключение

    private suspend fun start(serverId: String?) {
        val p = repo.find(serverId ?: repo.data.value.selectedId) ?: repo.groups().firstOrNull()?.servers?.firstOrNull()
        if (p == null) {
            fail("Нет серверов: добавьте подписку или ссылку")
            return
        }
        val name = Flags.strip(p.name)
        try {
            _state.value = VpnState.Connecting(name, "Готовлю конфиг")
            startForegroundCompat(notification("Подключение: $name"))
            if (p.id != repo.data.value.selectedId) repo.select(p.id)

            val settings = repo.settings.value
            val hosts = serverHosts(p)
            val resolved = resolve(hosts)
            val options = XrayOptions(
                mode = XrayMode.Tun,
                bypass = repo.bypassFor(p),
                logPath = logFile(this).absolutePath,
                stats = true,
                useServerRouting = settings.useServerRouting,
                resolvedHosts = resolved,
                routing = settings.routing,
                dns = settings.dns,
            )
            val cfg = XrayConfigBuilder.build(p, options)
            if (Geo.needed(cfg) && !Geo.installed(repo.geoDir)) {
                _state.value = VpnState.Connecting(name, "Скачиваю базы geoip/geosite")
                Geo.download(Geo.sourceOf(settings.geoSource), repo.geoDir) { step ->
                    _state.value = VpnState.Connecting(name, step)
                }
            }
            val json = Js.stringify(cfg)
            _state.value = VpnState.Connecting(name, "Запускаю ядро")

            // TUN поднимается заново только при первом подключении или смене состава приложений.
            val serverIps = (resolved.values + hosts.filter(::isIpLiteral)).toSet()
            closeCore()
            closeTun()
            val pfd = establish(name, serverIps) ?: throw IllegalStateException("нет разрешения на VPN")
            tun = pfd
            libcup.Libcup.setMemoryLimit(settings.coreMemoryMb.toLong())
            logFile(this).delete()
            core = libcup.Libcup.start(json, repo.geoDir.absolutePath, pfd.fd.toLong())
            config = json
            profile = p
            watchdog.reset()
            Log.i(TAG, "xray ${libcup.Libcup.version()} подключён: ${p.name}")

            _state.value = VpnState.On(p.id, name, System.currentTimeMillis())
            _stats.value = VpnStats()
            repo.updateSettings { it.copy(wasConnected = true) }
            startForegroundCompat(notification("Подключено: $name"))
            cancelAlert()
            watchNetwork()
            startJobs()
        } catch (e: Exception) {
            Log.e(TAG, "не подключился", e)
            fail(shortError(e))
        }
    }

    /** Адреса серверов профиля (домены и IP). */
    private fun serverHosts(p: Profile): List<String> =
        if (p is XrayConfigProfile) ServerConfigs.serverHosts(p) else listOf(p.host)

    /**
     * Домены серверов — заранее, системным DNS (программа вне туннеля): xray подключается к серверу, не завися от
     * своего DNS, а адреса уходят из маршрутов туннеля.
     */
    private fun resolve(hosts: List<String>): Map<String, String> = hosts.filterNot(::isIpLiteral).take(32).mapNotNull { host ->
        try {
            InetAddress.getAllByName(host).firstOrNull { it is java.net.Inet4Address }?.hostAddress?.let { host to it }
        } catch (e: Exception) {
            null
        }
    }.toMap()

    private fun establish(session: String, serverIps: Set<String>): ParcelFileDescriptor? {
        val settings = repo.settings.value
        val b = Builder()
            .setSession(session)
            .setMtu(XrayConfigBuilder.TUN_MTU)
            .addAddress(TunRoutes.TUN_ADDRESS, TunRoutes.TUN_PREFIX)
            .addDnsServer(TunRoutes.TUN_DNS)
            .setBlocking(false)
            .setConfigureIntent(PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE))
        if (Build.VERSION.SDK_INT >= 29) b.setMetered(false)

        // IPv4: всё, кроме локальных сетей и адресов сервера (его панель открывается и при включённом VPN).
        val excluded = TunRoutes.excluded(serverIps)
        if (Build.VERSION.SDK_INT >= 33) {
            b.addRoute("0.0.0.0", 0)
            for (r in excluded) b.excludeRoute(IpPrefix(InetAddress.getByName(r.address), r.prefix))
        } else {
            for (r in TunRoutes.include(excluded)) b.addRoute(r.address, r.prefix)
        }
        // IPv6: глобальные адреса — в туннель (xray отдаёт программам только IPv4, так что это защита от утечек).
        b.addAddress("fdfe:dcba:9876::1", 126)
        b.addRoute("2000::", 3)

        // Раздельное туннелирование по приложениям. Сама программа всегда вне туннеля.
        val pm = packageManager
        fun installed(pkg: String) = try {
            pm.getPackageInfo(pkg, 0); true
        } catch (e: PackageManager.NameNotFoundException) {
            false
        }
        when (settings.splitMode) {
            SplitMode.Only -> {
                val apps = settings.splitApps.filter { it != packageName && installed(it) }
                if (apps.isEmpty()) b.addDisallowedApplication(packageName) else apps.forEach { b.addAllowedApplication(it) }
            }
            SplitMode.Exclude -> {
                b.addDisallowedApplication(packageName)
                settings.splitApps.filter { it != packageName && installed(it) }.forEach { b.addDisallowedApplication(it) }
            }
            SplitMode.Off -> b.addDisallowedApplication(packageName)
        }
        return b.establish()
    }

    /** Перезапуск только ядра: TUN остаётся, пакеты ждут в нём — мимо туннеля в эти секунды ничего не уходит. */
    private suspend fun restartCore(reason: String) = mutex.withLock {
        val pfd = tun ?: return@withLock
        val json = config ?: return@withLock
        Log.w(TAG, "перезапуск ядра: $reason")
        closeCore()
        try {
            core = libcup.Libcup.start(json, repo.geoDir.absolutePath, pfd.fd.toLong())
        } catch (e: Exception) {
            Log.e(TAG, "ядро не перезапустилось", e)
            fail(shortError(e))
        }
    }

    private suspend fun stop(userAction: Boolean) {
        stopJobs()
        unwatchNetwork()
        closeCore()
        closeTun()
        config = null
        profile = null
        if (userAction) repo.updateSettings { it.copy(wasConnected = false) }
        _state.value = VpnState.Off
        _stats.value = VpnStats()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private suspend fun fail(message: String) {
        stopJobs()
        unwatchNetwork()
        closeCore()
        closeTun()
        _state.value = VpnState.Failed(message)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun closeCore() {
        try {
            core?.close()
        } catch (e: Exception) {
            Log.w(TAG, "xray не остановился", e)
        }
        core = null
    }

    private fun closeTun() {
        try {
            tun?.close()
        } catch (_: Exception) {
        }
        tun = null
    }

    // --- Сторож и статистика

    private fun startJobs() {
        stopJobs()
        watchJob = scope.launch {
            delay(3_000)
            refreshIp()
            while (isActive) {
                delay(watchdog.nextCheckInMs)
                val c = core ?: continue
                val ok = try {
                    c.get("", BatchPing.PROBE_URL, 10_000)
                    true
                } catch (e: Exception) {
                    false
                }
                when (watchdog.report(ok, networkAvailable())) {
                    WatchdogAction.Restart -> restartCore("две проверки подряд не прошли")
                    WatchdogAction.None -> {}
                }
                val down = watchdog.gaveUp
                if (down != _stats.value.serverDown) {
                    _stats.value = _stats.value.copy(serverDown = down)
                    if (down) alert("Сервер не отвечает", "${profile?.name?.let(Flags::strip) ?: "Сервер"}: проверьте его или выберите другой")
                    else cancelAlert()
                }
            }
        }
        statsJob = scope.launch {
            while (isActive) {
                delay(1_000)
                val c = core ?: continue
                // Вход tun — весь трафик устройства через VPN (и через сервер, и напрямую по маршрутам).
                val up = c.counter("inbound>>>${XrayConfigBuilder.TUN_TAG}>>>traffic>>>uplink", true).coerceAtLeast(0)
                val down = c.counter("inbound>>>${XrayConfigBuilder.TUN_TAG}>>>traffic>>>downlink", true).coerceAtLeast(0)
                val s = _stats.value
                _stats.value = s.copy(upBytes = s.upBytes + up, downBytes = s.downBytes + down, upSpeed = up, downSpeed = down)
            }
        }
    }

    private fun stopJobs() {
        watchJob?.cancel()
        statsJob?.cancel()
        watchJob = null
        statsJob = null
    }

    private fun refreshIp() {
        val c = core ?: return
        val ip = try {
            c.get("", IP_URL, 10_000).take(45)
        } catch (e: Exception) {
            null
        }
        _stats.value = _stats.value.copy(publicIp = ip)
    }

    // --- Смена сети: соединения xray привязаны к старой сети — перезапускаем ядро

    private fun cm() = getSystemService(ConnectivityManager::class.java)

    private fun networkAvailable(): Boolean = network != null

    private fun watchNetwork() {
        if (networkCallback != null) return
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(n: Network) {
                val previous = network
                network = n
                setUnderlyingNetworks(arrayOf(n))
                if (previous != null && previous != n) scope.launch {
                    restartCore("сменилась сеть")
                    delay(3_000)
                    refreshIp()
                }
            }

            override fun onLost(n: Network) {
                if (n == network) network = null
            }
        }
        networkCallback = cb
        // Сеть по умолчанию для программы — физическая (программа исключена из VPN).
        cm().registerDefaultNetworkCallback(cb)
    }

    private fun unwatchNetwork() {
        networkCallback?.let {
            try {
                cm().unregisterNetworkCallback(it)
            } catch (_: Exception) {
            }
        }
        networkCallback = null
        network = null
    }

    // --- Уведомления

    private fun startForegroundCompat(n: Notification) {
        if (Build.VERSION.SDK_INT >= 34) startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED)
        else startForeground(NOTIFICATION_ID, n)
    }

    private fun channel(id: String, title: String, importance: Int) {
        val nm = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26 && nm.getNotificationChannel(id) == null)
            nm.createNotificationChannel(NotificationChannel(id, title, importance))
    }

    private fun builder(channelId: String): Notification.Builder =
        if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, channelId) else @Suppress("DEPRECATION") Notification.Builder(this)

    private fun openApp() = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)

    private fun notification(text: String): Notification {
        channel(CHANNEL, "VPN", NotificationManager.IMPORTANCE_LOW)
        return builder(CHANNEL)
            .setContentTitle("The Cup of Tea")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_lock_lock)
            .setContentIntent(openApp())
            .setOngoing(true)
            .build()
    }

    private fun alert(title: String, text: String) {
        channel(CHANNEL_ALERTS, "Предупреждения", NotificationManager.IMPORTANCE_DEFAULT)
        getSystemService(NotificationManager::class.java).notify(
            ALERT_ID,
            builder(CHANNEL_ALERTS).setContentTitle(title).setContentText(text)
                .setSmallIcon(android.R.drawable.stat_notify_error).setContentIntent(openApp()).setAutoCancel(true).build(),
        )
    }

    private fun cancelAlert() = getSystemService(NotificationManager::class.java).cancel(ALERT_ID)

    /** Ошибка ядра — последнее звено цепочки «a > b > c», без технических префиксов. */
    private fun shortError(e: Throwable): String =
        (e.message ?: e.javaClass.simpleName).substringAfterLast(" > ").removePrefix("go.Universe\$proxyerror: ").take(200)
}
