package cupoftea.tv.data

import android.annotation.SuppressLint
import android.content.Context
import android.os.Build
import android.provider.Settings
import android.util.Log
import cupoftea.core.config.AutoSelect
import cupoftea.core.config.BypassOptions
import cupoftea.core.config.BypassRules
import cupoftea.core.config.ServerConfigs
import cupoftea.core.profiles.DeviceHeaders
import cupoftea.core.profiles.LinkParser
import cupoftea.core.profiles.Profile
import cupoftea.core.profiles.ProfileData
import cupoftea.core.profiles.Subscription
import cupoftea.core.profiles.SubscriptionService
import cupoftea.core.profiles.SubscriptionUpdate
import cupoftea.core.profiles.XrayConfigProfile
import cupoftea.core.sessions.BatchPing
import cupoftea.core.sessions.DelayResult
import cupoftea.core.sessions.PingTarget
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import java.io.File
import java.security.MessageDigest

/** Группа серверов в списке: подписка или «Свои»; первым — автовыбор, если в группе от двух серверов по ссылкам. */
data class ServerGroup(val id: String, val title: String, val subscription: Subscription?, val servers: List<Profile>)

/**
 * Данные программы в одном месте (один процесс: интерфейс и служба VPN). Все изменения сохраняются сразу;
 * сетевые операции — в фоне.
 */
class Repository private constructor(private val context: Context) {
    companion object {
        private const val TAG = "CupRepo"
        const val OWN_GROUP = "own"

        @Volatile
        private var instance: Repository? = null

        fun get(context: Context): Repository = instance ?: synchronized(this) {
            instance ?: Repository(context.applicationContext).also { instance = it }
        }
    }

    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val profileStore = ProfileStore(context)
    private val settingsStore = SettingsStore(context)
    private val lock = Mutex()

    private val _data = MutableStateFlow(loadData())
    val data: StateFlow<ProfileData> = _data

    private val _settings = MutableStateFlow(settingsStore.load())
    val settings: StateFlow<AppSettings> = _settings

    /** Задержки по Id сервера. */
    private val _pings = MutableStateFlow<Map<String, DelayResult>>(emptyMap())
    val pings: StateFlow<Map<String, DelayResult>> = _pings

    private val _pinging = MutableStateFlow(false)
    val pinging: StateFlow<Boolean> = _pinging

    /** Последнее сообщение для пользователя (итог обновления подписки, ошибка). */
    private val _message = MutableStateFlow<String?>(null)
    val message: StateFlow<String?> = _message

    val geoDir: File get() = context.filesDir

    private fun loadData(): ProfileData = try {
        profileStore.load()
    } catch (e: Exception) {
        // Ключ Keystore потерян (сброс, восстановление из копии) — начинаем с пустого списка, файл не трогаем.
        Log.e(TAG, "не удалось прочитать profiles.dat", e)
        ProfileData()
    }

    private suspend fun edit(change: (ProfileData) -> Unit) = lock.withLock {
        val copy = storeJson.decodeFromString(ProfileData.serializer(), storeJson.encodeToString(ProfileData.serializer(), _data.value))
        change(copy)
        withContext(Dispatchers.IO) { profileStore.save(copy) }
        _data.value = copy
    }

    fun updateSettings(change: (AppSettings) -> AppSettings) {
        synchronized(settingsStore) {
            val next = change(_settings.value)
            settingsStore.save(next)
            _settings.value = next
        }
    }

    fun say(text: String?) {
        _message.value = text
    }

    // --- Серверы и группы

    /** Группы для списка: подписки по порядку, затем «Свои». Автовыбор — виртуальный сервер, в хранилище его нет. */
    fun groups(data: ProfileData = _data.value): List<ServerGroup> {
        val key = data to _settings.value.bypass
        groupsCache?.let { (k, v) -> if (k.first === key.first && k.second == key.second) return v }
        return buildGroups(data).also { groupsCache = key to it }
    }

    @Volatile
    private var groupsCache: Pair<Pair<ProfileData, Any>, List<ServerGroup>>? = null

    /** Автовыбор группы (виртуальный сервер). */
    fun isAuto(p: Profile) = p is XrayConfigProfile && p.id == AutoSelect.idFor(p.subscriptionId)

    private fun buildGroups(data: ProfileData): List<ServerGroup> {
        val result = mutableListOf<ServerGroup>()
        for (sub in data.subscriptions) {
            val servers = data.profiles.filter { it.subscriptionId == sub.id }
            result.add(ServerGroup(sub.id, sub.name, sub, withAuto(sub.name, sub.id, servers, sub)))
        }
        val own = data.profiles.filter { it.subscriptionId == null }
        if (own.isNotEmpty()) result.add(ServerGroup(OWN_GROUP, "Свои", null, withAuto("Свои", null, own, null)))
        return result
    }

    private fun withAuto(name: String, subId: String?, servers: List<Profile>, sub: Subscription?): List<Profile> {
        val joinable = servers.filter(AutoSelect::canJoin)
        if (joinable.size < AutoSelect.MIN_SERVERS) return servers
        return try {
            val auto = AutoSelect.build("Автовыбор · $name", AutoSelect.idFor(subId), subId, joinable.map { it to bypassFor(it, sub) })
            listOf(auto) + servers
        } catch (e: Exception) {
            servers
        }
    }

    /** Сервер по Id, в том числе автовыбор группы. */
    fun find(id: String?): Profile? {
        if (id == null) return null
        return _data.value.profiles.firstOrNull { it.id == id } ?: groups().flatMap { it.servers }.firstOrNull { it.id == id }
    }

    /** Настройки обхода для сервера: выбранный вариант с поправками панели его подписки. */
    fun bypassFor(profile: Profile, sub: Subscription? = _data.value.subscriptions.firstOrNull { it.id == profile.subscriptionId }): BypassOptions {
        val user = _settings.value.bypass.options()
        val withPanel = sub?.bypass?.applyTo(user) ?: user
        return try {
            BypassRules.validate(withPanel)
        } catch (e: Exception) {
            // Панель прислала неверные значения — они игнорируются.
            BypassRules.validate(user)
        }
    }

    fun select(id: String) = scope.launch { edit { it.selectedId = id } }

    fun removeServer(id: String) = scope.launch {
        edit { d ->
            d.profiles.removeAll { it.id == id && it.subscriptionId == null }
            if (d.selectedId == id) d.selectedId = null
        }
    }

    /**
     * Импорт текста: ссылки серверов, JSON с конфигами xray (как отдают панели или export --configs) или адрес
     * подписки https://… Возвращает сообщение для пользователя.
     */
    suspend fun import(text: String): String {
        val t = text.trim()
        if (t.startsWith("http://", true) || t.startsWith("https://", true)) {
            return addSubscription(t.lineSequence().first().trim())
        }
        val result = if (ServerConfigs.looksLikeConfigs(t)) ServerConfigs.parseMany(t).let { cupoftea.core.profiles.ImportResult(it.profiles, it.errors) }
        else LinkParser.parseMany(t)
        var added = 0
        edit { d -> result.profiles.forEach { if (d.upsert(it)) added++ } }
        val updated = result.profiles.size - added
        return buildString {
            append("Серверов добавлено: $added")
            if (updated > 0) append(", обновлено: $updated")
            if (result.errors.isNotEmpty()) append("; ошибок: ${result.errors.size} (${result.errors[0].message})")
        }
    }

    // --- Подписки

    private val device = DeviceHeaders {
        listOf(
            "x-hwid" to hwid(),
            "x-device-os" to "Android",
            "x-ver-os" to Build.VERSION.RELEASE,
            "x-device-model" to listOf(Build.MANUFACTURER, Build.MODEL).filter { !it.isNullOrBlank() }.joinToString(" ")
                .filter { it in ' '..'~' }.take(64),
            "x-client" to cupoftea.core.Product.ID,
        )
    }

    /** HWID — хэш ANDROID_ID с солью программы: стабилен для устройства, но сам ANDROID_ID не раскрывает. */
    @SuppressLint("HardwareIds")
    private fun hwid(): String {
        val id = Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: Build.FINGERPRINT
        val hash = MessageDigest.getInstance("SHA-256").digest("${cupoftea.core.Product.ID}:$id".toByteArray())
        return hash.take(16).joinToString("") { "%02x".format(it) }
    }

    private fun deviceIfAllowed() = if (_settings.value.sendHwid) device else null

    private fun now() = System.currentTimeMillis() / 1000

    suspend fun addSubscription(url: String): String = withContext(Dispatchers.IO) {
        try {
            var update: SubscriptionUpdate? = null
            edit { d -> update = SubscriptionService.add(d, url, null, deviceIfAllowed(), now()) }
            update.toString()
        } catch (e: Exception) {
            "Подписка не добавлена: ${e.message}"
        }
    }

    suspend fun updateSubscription(sub: Subscription): String = withContext(Dispatchers.IO) {
        var result = ""
        try {
            edit { d -> result = SubscriptionService.update(d, sub, deviceIfAllowed(), now()).toString() }
        } catch (e: Exception) {
            // Ошибка уже записана в подписку внутри edit — но edit откатился; запишем отдельно.
            edit { d -> d.setSubscription(sub.copy(lastAttempt = now(), lastError = e.message)) }
            result = "«${sub.name}»: ${e.message}"
        }
        result
    }

    /** Обновляет подписки, у которых подошёл срок; затем пингует их серверы. */
    suspend fun updateDueSubscriptions(): List<String> {
        val due = _data.value.subscriptions.filter { it.isDue(now()) }
        val messages = due.map { updateSubscription(it) }
        if (due.isNotEmpty()) pingAll()
        return messages
    }

    fun removeSubscription(id: String) = scope.launch { edit { it.removeSubscription(id) } }

    // --- Пинг

    /** Пинг серверов одним xray (вне VPN: программа исключена из туннеля). */
    suspend fun pingAll(all: List<Profile> = groups().flatMap { it.servers }) = withContext(Dispatchers.IO) {
        val servers = all.filterNot(::isAuto)
        if (servers.isEmpty() || _pinging.value) return@withContext
        _pinging.value = true
        try {
            val plan = BatchPing.plan(servers.map { PingTarget(it, bypassFor(it)) })
            _pings.value = _pings.value + plan.errors
            val instance = libcup.Libcup.start(cupoftea.core.Js.stringify(plan.config), geoDir.absolutePath, -1)
            try {
                val limit = Semaphore(BatchPing.MAX_PARALLEL)
                plan.tags.map { (id, tag) ->
                    async {
                        limit.withPermit {
                            val result = try {
                                DelayResult(instance.delay(tag, BatchPing.PROBE_URL, 10_000, 2).toInt(), null)
                            } catch (e: Exception) {
                                DelayResult(null, e.message?.substringAfterLast("> ")?.take(80) ?: "ошибка")
                            }
                            _pings.value = _pings.value + (id to result)
                        }
                    }
                }.awaitAll()
                // Задержка автовыбора — лучшая в его группе.
                for (group in groups()) {
                    val auto = group.servers.firstOrNull(::isAuto) ?: continue
                    val best = group.servers.filter { AutoSelect.canJoin(it) }.mapNotNull { _pings.value[it.id]?.milliseconds }.minOrNull()
                    _pings.value = _pings.value + (auto.id to if (best != null) DelayResult(best, null) else DelayResult(null, "нет ответа"))
                }
            } finally {
                instance.close()
            }
        } catch (e: Exception) {
            Log.e(TAG, "пинг не удался", e)
            _message.value = "Пинг не удался: ${e.message}"
        } finally {
            _pinging.value = false
        }
    }

    // --- Импорт для разработки: файл import.txt в папке программы (adb push) подхватывается при запуске

    suspend fun importDevFile() {
        val files = context.getExternalFilesDir(null)?.listFiles { f -> f.name.startsWith("import") && f.name.endsWith(".txt") } ?: return
        val messages = files.sortedBy { it.name }.map { file ->
            val text = file.readText()
            // Файл с ключами удаляется сразу, до разбора.
            file.delete()
            import(text)
        }
        if (messages.isNotEmpty()) _message.value = messages.joinToString("; ")
    }
}
