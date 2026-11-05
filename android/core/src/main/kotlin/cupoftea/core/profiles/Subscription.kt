package cupoftea.core.profiles

import cupoftea.core.Product
import cupoftea.core.Text
import cupoftea.core.config.ConfigException
import cupoftea.core.config.PanelBypass
import cupoftea.core.config.ServerConfigs
import cupoftea.core.routing.RoutingProfile
import cupoftea.core.routing.RoutingProfiles
import kotlinx.serialization.Serializable
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.SocketTimeoutException
import java.net.URI
import java.net.URL
import java.util.UUID

/**
 * Подписка: ссылка, по которой панель (Remnawave, 3x-ui, Marzban…) отдаёт список серверов. Хранится вместе
 * с серверами в зашифрованном хранилище — в ссылке личный токен. Время — секунды Unix. Порт Subscription.cs.
 */
@Serializable
data class Subscription(
    val id: String = UUID.randomUUID().toString(),
    val name: String,
    val url: String,
    val autoUpdate: Boolean = true,
    /** Как часто обновлять; панель может подсказать заголовком profile-update-interval. */
    val updateIntervalHours: Int = 12,
    val lastUpdated: Long? = null,
    /** Последняя попытка (удачная или нет) — чтобы при ошибке не стучаться каждую минуту. */
    val lastAttempt: Long? = null,
    val lastError: String? = null,
    /** Трафик и срок из subscription-userinfo (байты; 0 — без ограничения). */
    val upload: Long = 0,
    val download: Long = 0,
    val total: Long = 0,
    val expire: Long? = null,
    /** Служебные строки панели вместо серверов: «Использовано 3.43/60 ГБ». */
    val notes: List<String> = emptyList(),
    val description: String? = null,
    val announce: String? = null,
    val announceUrl: String? = null,
    val supportUrl: String? = null,
    val webPageUrl: String? = null,
    /** Панель просит не показывать адрес подписки (hide-url). */
    val hideUrl: Boolean = false,
    /** Как панель предлагает сортировать серверы: ping, name или none. */
    val sortOrder: String? = null,
    val routing: RoutingProfile? = null,
    val bypass: PanelBypass? = null,
    /** Откуда обновлять профиль маршрутизации при каждом обновлении подписки (autorouting). */
    val routingUrl: String? = null,
    /** Как часто панель советует обновлять базы geoip/geosite, часов. */
    val geoUpdateHours: Int? = null,
) {
    fun isDue(nowSeconds: Long): Boolean {
        if (!autoUpdate) return false
        val wait = if (lastError == null) updateIntervalHours * 3600L else 30 * 60L
        return (lastAttempt ?: Long.MIN_VALUE / 2) < nowSeconds - wait
    }
}

/** Что панель сообщила о себе заголовками или строками «#имя: значение» в теле подписки. */
data class SubscriptionMeta(
    val description: String? = null,
    val announce: String? = null,
    val announceUrl: String? = null,
    val supportUrl: String? = null,
    val webPageUrl: String? = null,
    val hideUrl: Boolean = false,
    val sortOrder: String? = null,
    val routing: RoutingProfile? = null,
    val bypass: PanelBypass? = null,
    val routingUrl: String? = null,
    val geoUpdateHours: Int? = null,
    /** Панель переехала: новый полный адрес (new-url) или новый хост (new-domain). Только https. */
    val newUrl: String? = null,
    val newDomain: String? = null,
)

/** Что вернула панель. */
data class SubscriptionFetch(
    val result: ImportResult,
    val unsupported: Int,
    val title: String?,
    val updateIntervalHours: Int?,
    val upload: Long,
    val download: Long,
    val total: Long,
    val expire: Long?,
    val notes: List<String> = emptyList(),
    val meta: SubscriptionMeta = SubscriptionMeta(),
)

/** Итог обновления подписки для сообщения пользователю. */
data class SubscriptionUpdate(
    val subscription: Subscription, val added: Int, val updated: Int, val removed: Int, val unsupported: Int,
    val errors: List<ImportError>,
) {
    override fun toString(): String {
        val sb = StringBuilder("«${subscription.name}»: серверов новых $added, обновлено $updated, удалено $removed")
        if (unsupported > 0) sb.append("; пропущено ссылок других протоколов (ssr, tuic, wireguard…): $unsupported")
        if (errors.isNotEmpty()) sb.append("; ошибок в ссылках: ${errors.size} (первая: ${errors[0].message})")
        return sb.append('.').toString()
    }
}

class SubscriptionException(message: String) : Exception(message)

/** Заголовки устройства для панели (x-hwid и т. п.) — их даёт приложение (Android ID, модель). */
fun interface DeviceHeaders {
    fun headers(): List<Pair<String, String>>
}

object SubscriptionClient {
    const val MAX_BYTES = 4 * 1024 * 1024
    const val TIMEOUT_MS = 30_000

    /**
     * С этим User-Agent панели отдают не ссылки, а полные конфиги xray — со встроенными маршрутами, DNS,
     * фрагментацией и автовыбором. Если панель так не умеет, она всё равно вернёт ссылки.
     */
    const val FULL_CONFIG_USER_AGENT = "INCY/2.0.0"

    /** Только http(s), без логина в адресе. Возвращает нормализованный адрес. */
    fun validateUrl(url: String?): String {
        val s = url?.trim() ?: ""
        val uri = try {
            URI(s)
        } catch (e: Exception) {
            null
        }
        if (uri == null || !uri.isAbsolute || uri.scheme?.lowercase() !in listOf("https", "http") || uri.host.isNullOrEmpty())
            throw SubscriptionException("Нужна ссылка на подписку вида https://…")
        if (uri.rawUserInfo != null) throw SubscriptionException("Логин и пароль в адресе подписки не поддерживаются")
        // Как Uri.AbsoluteUri в .NET: схема и хост в нижнем регистре, пустой путь — «/».
        val path = if (uri.rawPath.isNullOrEmpty()) "/" else uri.rawPath
        val port = if (uri.port < 0) "" else ":${uri.port}"
        val query = uri.rawQuery?.let { "?$it" } ?: ""
        val fragment = uri.rawFragment?.let { "#$it" } ?: ""
        return "${uri.scheme.lowercase()}://${uri.host.lowercase()}$port$path$query$fragment"
    }

    /** Скачивает подписку. [device] — заголовки x-hwid и т. п.; null — не сообщать (выключено в настройках). */
    fun fetch(url: String, device: DeviceHeaders?): SubscriptionFetch {
        val u = validateUrl(url)
        val (body, headers) = get(u, device)
        return parse(body) { name -> headers[name.lowercase()] }
    }

    /** GET с переадресациями (до 5), тело до MAX_BYTES; ключи заголовков — в нижнем регистре. */
    internal fun get(url: String, device: DeviceHeaders?): Pair<String, Map<String, String>> {
        var current = URL(url)
        repeat(6) {
            val c = current.openConnection() as HttpURLConnection
            try {
                c.connectTimeout = TIMEOUT_MS
                c.readTimeout = TIMEOUT_MS
                c.instanceFollowRedirects = false
                c.setRequestProperty("User-Agent", "$FULL_CONFIG_USER_AGENT (${Product.ID})")
                c.setRequestProperty("Accept-Language", "ru-RU")
                device?.headers()?.forEach { (k, v) -> c.setRequestProperty(k, v) }
                val code = c.responseCode
                if (code in 300..399) {
                    val location = c.getHeaderField("Location") ?: throw SubscriptionException("Панель подписки ответила $code без адреса")
                    val next = URL(current, location)
                    if (next.protocol !in listOf("https", "http")) throw SubscriptionException("Переадресация не на http(s)")
                    // С https на http не уходим: в ссылке токен.
                    if (current.protocol == "https" && next.protocol == "http") throw SubscriptionException("Панель переадресует с https на http")
                    current = next
                    return@repeat
                }
                if (code !in 200..299) throw SubscriptionException("Панель подписки ответила $code ${c.responseMessage ?: ""}".trimEnd())
                val headers = c.headerFields.filterKeys { it != null }.mapKeys { it.key.lowercase() }.mapValues { it.value.firstOrNull() ?: "" }
                val out = ByteArrayOutputStream()
                c.inputStream.use { input ->
                    val buf = ByteArray(16 * 1024)
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        out.write(buf, 0, n)
                        if (out.size() > MAX_BYTES)
                            throw SubscriptionException("Подписка больше ${MAX_BYTES / 1024 / 1024} МБ — это не похоже на список серверов")
                    }
                }
                return out.toString("UTF-8") to headers
            } catch (e: SocketTimeoutException) {
                throw SubscriptionException("Панель подписки не ответила за ${TIMEOUT_MS / 1000} секунд")
            } catch (e: IOException) {
                throw SubscriptionException("Не удалось получить подписку: ${e.message}")
            } finally {
                c.disconnect()
            }
        }
        throw SubscriptionException("Слишком много переадресаций")
    }

    /** Разбирает ответ панели; [header] — значение заголовка по имени или null. */
    fun parse(body: String, header: (String) -> String?): SubscriptionFetch {
        if (body.trimStart().startsWith('<'))
            throw SubscriptionException("По ссылке открылась веб-страница, а не список серверов. Скопируйте в панели именно ссылку подписки.")
        val info = parseUserInfo(header("subscription-userinfo"))
        // Строки «#имя: значение» в теле — запасной путь, заголовки главнее.
        val bodyMeta = bodyMeta(body)
        val value: (String) -> String? = { name -> header(name) ?: bodyMeta[name.lowercase()] }
        val interval = value("profile-update-interval")?.trim()?.takeIf { it.all(Char::isDigit) }?.toIntOrNull()?.takeIf { it in 1..168 }
        val title = decodeTitle(value("profile-title") ?: header("subscription-name"))
        val meta = parseMeta(value, bodyMeta["routing"], bodyMeta["autorouting"])

        if (ServerConfigs.looksLikeConfigs(body)) {
            val configs = try {
                ServerConfigs.parseMany(body)
            } catch (e: ConfigException) {
                throw SubscriptionException(e.message ?: "")
            }
            if (configs.profiles.isEmpty())
                throw SubscriptionException(
                    if (configs.errors.isNotEmpty()) "Ни один конфиг из подписки не подошёл: ${configs.errors[0].message}"
                    else "В подписке не нашлось серверов"
                )
            return SubscriptionFetch(
                ImportResult(configs.profiles, configs.errors), 0, title, interval,
                info.upload, info.download, info.total, info.expire, configs.notes, meta,
            )
        }

        // Обычно подписка — base64 от списка ссылок, реже — сам список.
        val text = if (!body.contains("://")) Text.decodeBase64(body.trim()) ?: body else body
        val result = LinkParser.parseMany(text)
        val unsupported = otherLinkRegex.findAll(text).count()
        if (result.profiles.isEmpty() && result.errors.isEmpty())
            throw SubscriptionException(
                if (unsupported > 0) "В подписке нет поддерживаемых серверов (есть только другие протоколы: $unsupported)"
                else "В подписке не нашлось ссылок на серверы"
            )
        return SubscriptionFetch(result, unsupported, title, interval, info.upload, info.download, info.total, info.expire, meta = meta)
    }

    private fun parseMeta(value: (String) -> String?, bodyRouting: String?, bodyAutorouting: String?): SubscriptionMeta {
        fun text(name: String, max: Int): String? = decodeText(value(name))?.let { if (it.length > max) it.substring(0, max) else it }
        fun link(s: String?): String? {
            val t = s?.trim() ?: return null
            return try {
                val u = URI(t)
                if (u.isAbsolute && u.scheme.lowercase() in listOf("https", "http", "tg", "mailto")) u.toASCIIString() else null
            } catch (e: Exception) {
                null
            }
        }
        val newUrl = value("new-url")?.trim()
        val newDomain = value("new-domain")?.trim()
        val sort = value("sort-order")?.trim()?.lowercase()
        val mail = value("support-email")?.trim()
        return SubscriptionMeta(
            description = text("profile-description", 300),
            announce = text("announce", 200),
            announceUrl = link(value("announce-url")),
            supportUrl = link(value("support-url")) ?: if (mail != null && mail.length > 3 && mail.contains('@')) "mailto:$mail" else null,
            webPageUrl = link(value("profile-web-page-url") ?: value("homepage")),
            hideUrl = value("hide-url")?.trim()?.lowercase() in listOf("1", "true"),
            sortOrder = if (sort in listOf("ping", "name", "none")) sort else null,
            routing = RoutingProfiles.tryParse(value("routing")) ?: RoutingProfiles.tryParse(bodyRouting),
            bypass = PanelBypass.fromHeaders(value),
            routingUrl = (RoutingProfiles.autoroutingUrl(value("autorouting")) ?: RoutingProfiles.autoroutingUrl(bodyAutorouting))?.toASCIIString(),
            geoUpdateHours = value("routing-geo-update-interval")?.trim()?.toIntOrNull()?.takeIf { it in 1..8760 },
            newUrl = newUrl?.let {
                try {
                    URI(it).takeIf { u -> u.isAbsolute && u.scheme.equals("https", true) && !u.host.isNullOrEmpty() }?.toASCIIString()
                } catch (e: Exception) {
                    null
                }
            },
            newDomain = newDomain?.takeIf { it.isNotEmpty() && hostRegex.matches(it) }?.lowercase(),
        )
    }

    private val hostRegex = Regex("^(?=.{1,253}$)[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$")

    private val metaLineRegex = Regex(
        "^#\\s*(announce|announce-url|profile-title|support-url|profile-web-page-url|profile-update-interval|hide-url|routing-geo-update-interval)\\s*:\\s*(.+)$",
        RegexOption.IGNORE_CASE,
    )

    /** Ссылки протоколов, которые клиент не поддерживает. */
    private val otherLinkRegex = Regex("(?<![a-z0-9])(?:ssr|tuic|wireguard|wg|socks|hysteria|anytls|amneziawg|awg)://\\S+", RegexOption.IGNORE_CASE)

    /**
     * Служебные строки в теле подписки (в том числе внутри base64): «#announce: текст», «#support-url: …» и
     * ссылка на профиль маршрутизации «схема://routing/onadd/…». Ключи — в нижнем регистре.
     */
    fun bodyMeta(body: String): Map<String, String> {
        val text = if (!body.contains("://") && !body.trimStart().startsWith('#')) Text.decodeBase64(body.trim()) ?: body else body
        val result = LinkedHashMap<String, String>()
        for (line in text.replace("\r\n", "\n").split('\n')) {
            val m = metaLineRegex.find(line.trim())
            if (m != null) result.putIfAbsent(m.groupValues[1].lowercase(), m.groupValues[2].trim())
            else if (line.contains("://routing/onadd/", ignoreCase = true) || line.contains("://routing/add/", ignoreCase = true))
                result.putIfAbsent("routing", line.trim())
            else if (line.contains("://autorouting/", ignoreCase = true)) result.putIfAbsent("autorouting", line.trim())
        }
        return result
    }

    /** Текст заголовка: обычный или «base64:…». */
    fun decodeText(value: String?): String? {
        var s = value?.trim()
        if (s.isNullOrEmpty()) return null
        if (s.startsWith("base64:", ignoreCase = true)) s = Text.decodeBase64(s.substring(7))?.trim()
        return if (s.isNullOrEmpty()) null else s
    }

    data class UserInfo(val upload: Long, val download: Long, val total: Long, val expire: Long?)

    /** «upload=1; download=2; total=3; expire=1700000000» (expire=0 или нет — бессрочно). */
    fun parseUserInfo(value: String?): UserInfo {
        var upload = 0L
        var download = 0L
        var total = 0L
        var expire: Long? = null
        for (part in (value ?: "").split(';').map { it.trim() }.filter { it.isNotEmpty() }) {
            val kv = part.split('=', limit = 2).map { it.trim() }
            if (kv.size != 2) continue
            val n = kv[1].toDoubleOrNull() ?: continue
            if (n < 0 || n > Long.MAX_VALUE.toDouble()) continue
            when (kv[0].lowercase()) {
                "upload" -> upload = n.toLong()
                "download" -> download = n.toLong()
                "total" -> total = n.toLong()
                // Некоторые панели пишут срок в миллисекундах.
                "expire" -> when {
                    n > 32_000_000_000 && n / 1000 < 253402300800 -> expire = (n / 1000).toLong()
                    n > 0 && n < 253402300800 -> expire = n.toLong()
                }
            }
        }
        return UserInfo(upload, download, total, expire)
    }

    /** profile-title: обычный текст или «base64:…», до 60 символов. */
    fun decodeTitle(value: String?): String? = decodeText(value)?.let { if (it.length > 60) it.substring(0, 60) else it }
}

object SubscriptionMerge {
    /**
     * Заменяет серверы подписки новым списком. Сервер с тем же именем сохраняет Id (выбор в списке не теряется);
     * пропавшие из подписки удаляются, ручные серверы не трогаются. Повторы имён внутри подписки получают « (2)».
     */
    fun apply(data: ProfileData, subscription: Subscription, fetch: SubscriptionFetch, nowSeconds: Long): SubscriptionUpdate {
        val old = data.profiles.filter { it.subscriptionId == subscription.id }
        val byName = old.groupBy { it.name.lowercase() }.mapValues { it.value.first() }
        val seen = HashSet<String>()
        val fresh = mutableListOf<Profile>()
        var added = 0
        var updated = 0
        for (p in fetch.result.profiles) {
            var name = p.name
            var i = 2
            while (!seen.add(name.lowercase())) name = "${p.name} (${i++})"
            val existing = byName[name.lowercase()]
            if (existing != null) {
                fresh.add(p.withIdentity(id = existing.id, name = name, subscriptionId = subscription.id))
                updated++
            } else {
                fresh.add(p.withIdentity(name = name, subscriptionId = subscription.id))
                added++
            }
        }
        // Новые серверы встают на место старых (порядок списка у пользователя не прыгает).
        val index = data.profiles.indexOfFirst { it.subscriptionId == subscription.id }
        data.profiles.removeAll { it.subscriptionId == subscription.id }
        data.profiles.addAll(if (index < 0) data.profiles.size else index.coerceAtMost(data.profiles.size), fresh)
        val selected = data.selectedId
        if (selected != null && old.any { it.id == selected } && fresh.none { it.id == selected }) data.selectedId = null

        val meta = fetch.meta
        val updatedSub = subscription.copy(
            updateIntervalHours = fetch.updateIntervalHours ?: subscription.updateIntervalHours,
            lastUpdated = nowSeconds,
            lastAttempt = nowSeconds,
            lastError = null,
            upload = fetch.upload,
            download = fetch.download,
            total = fetch.total,
            expire = fetch.expire,
            notes = fetch.notes,
            description = meta.description,
            announce = meta.announce,
            announceUrl = meta.announceUrl,
            supportUrl = meta.supportUrl,
            webPageUrl = meta.webPageUrl,
            hideUrl = meta.hideUrl,
            sortOrder = meta.sortOrder,
            routing = meta.routing,
            bypass = meta.bypass,
            routingUrl = meta.routingUrl,
            geoUpdateHours = meta.geoUpdateHours,
            // Панель переехала — дальше ходим по новому адресу (только https).
            url = meta.newUrl ?: meta.newDomain?.let { replaceHost(subscription.url, it) } ?: subscription.url,
        )
        data.setSubscription(updatedSub)
        val removed = old.count { o -> fresh.none { it.id == o.id } }
        return SubscriptionUpdate(updatedSub, added, updated, removed, fetch.unsupported, fetch.result.errors)
    }

    private fun replaceHost(url: String, host: String): String = try {
        val u = URI(url)
        URI(u.scheme, u.rawUserInfo, host, u.port, u.rawPath, u.rawQuery, u.rawFragment).toASCIIString()
    } catch (e: Exception) {
        url
    }

    /** Название новой подписки: из profile-title или по адресу панели. */
    fun defaultName(data: ProfileData, url: String, title: String?): String {
        val name = title ?: URI(url).host
        var result = name
        var i = 2
        while (data.subscriptions.any { it.name.equals(result, ignoreCase = true) }) result = "$name (${i++})"
        return result
    }
}

/** Добавление и обновление подписок поверх хранилища. Сетевые вызовы — блокирующие, звать из фонового потока. */
object SubscriptionService {
    fun add(data: ProfileData, url: String, name: String?, device: DeviceHeaders?, nowSeconds: Long): SubscriptionUpdate {
        val u = SubscriptionClient.validateUrl(url)
        if (data.subscriptions.any { it.url == u }) throw SubscriptionException("Эта подписка уже добавлена")
        val fetch = SubscriptionClient.fetch(u, device)
        val sub = Subscription(name = if (name.isNullOrBlank()) SubscriptionMerge.defaultName(data, u, fetch.title) else name.trim(), url = u)
        return withAutorouting(data, SubscriptionMerge.apply(data, sub, fetch, nowSeconds), null)
    }

    /** Обновляет подписку; при ошибке запоминает её в подписке (серверы остаются прежними) и бросает исключение. */
    fun update(data: ProfileData, sub: Subscription, device: DeviceHeaders?, nowSeconds: Long): SubscriptionUpdate {
        try {
            val fetch = SubscriptionClient.fetch(sub.url, device)
            return withAutorouting(data, SubscriptionMerge.apply(data, sub, fetch, nowSeconds), sub.routing)
        } catch (e: SubscriptionException) {
            data.setSubscription(sub.copy(lastAttempt = nowSeconds, lastError = e.message))
            throw e
        }
    }

    /**
     * Панель дала адрес профиля маршрутизации (autorouting) — скачиваем его заново при каждом обновлении подписки;
     * он главнее статичного профиля. Не скачался — остаётся прежний.
     */
    private fun withAutorouting(data: ProfileData, update: SubscriptionUpdate, previous: RoutingProfile?): SubscriptionUpdate {
        val url = update.subscription.routingUrl ?: return update
        val profile = try {
            RoutingFetch.fetch(url)
        } catch (e: IOException) {
            null
        } catch (e: SubscriptionException) {
            null
        }
        val routing = profile ?: update.subscription.routing ?: previous
        if (routing == update.subscription.routing) return update
        val sub = update.subscription.copy(routing = routing)
        data.setSubscription(sub)
        return update.copy(subscription = sub)
    }
}

/** Скачивание профиля маршрутизации по адресу автообновления (до 2 МБ, только https). */
object RoutingFetch {
    fun fetch(url: String): RoutingProfile? {
        if (!url.startsWith("https://", ignoreCase = true)) return null
        val (body, _) = SubscriptionClient.get(url, null)
        return if (body.length > 2 * 1024 * 1024) null else RoutingProfiles.tryParse(body)
    }
}
