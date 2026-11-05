package cupoftea.core.routing

import cupoftea.core.JObj
import cupoftea.core.Js
import cupoftea.core.Text
import cupoftea.core.arrOrPut
import cupoftea.core.config.ConfigException
import cupoftea.core.config.isIpv4
import cupoftea.core.config.isIpv6
import cupoftea.core.obj
import cupoftea.core.scalarText
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.net.IDN
import java.net.InetAddress
import java.net.URI
import java.util.regex.PatternSyntaxException

/**
 * Профиль маршрутизации: какие сайты и адреса идут напрямую, через VPN или блокируются. Формат JSON — как у панелей
 * (DirectSites, ProxyIp, BlockSites…, GlobalProxy). Порт src/PVpn.Core/Routing/RoutingProfile.cs.
 */
@Serializable
data class RoutingProfile(
    val name: String = "Свой профиль",
    /** Что не попало ни под одно правило: true — через VPN, false — напрямую. */
    val globalProxy: Boolean = true,
    val directSites: List<String> = emptyList(),
    val directIp: List<String> = emptyList(),
    val proxySites: List<String> = emptyList(),
    val proxyIp: List<String> = emptyList(),
    val blockSites: List<String> = emptyList(),
    val blockIp: List<String> = emptyList(),
    /** AsIs, IPIfNonMatch или IPOnDemand (как в xray). */
    val domainStrategy: String = "AsIs",
    /** Unix-время последнего изменения у источника: новее — профиль обновляется. */
    val lastUpdated: Long = 0,
) {
    val ruleCount get() = directSites.size + directIp.size + proxySites.size + proxyIp.size + blockSites.size + blockIp.size

    /** Нужны ли базы geoip/geosite (на ТВ они качаются только тогда). */
    val needsGeo
        get() = listOf(directSites, directIp, proxySites, proxyIp, blockSites, blockIp)
            .any { list -> list.any { it.startsWith("geosite:") || it.startsWith("geoip:") } }
}

object RoutingProfiles {
    const val MAX_RULES = 5000
    const val DIRECT_TAG = "route-direct"
    const val BLOCK_TAG = "route-block"

    /** Российские сайты и адреса — напрямую (госуслуги, банки, маркетплейсы не любят чужой IP). */
    val russiaDirect
        get() = RoutingProfile(
            name = "Россия напрямую",
            directSites = listOf("geosite:category-ru", "regexp:\\.ru$", "regexp:\\.su$", "regexp:\\.xn--p1ai$"),
            directIp = listOf("geoip:ru"),
        )

    val russiaDirectNoAds
        get() = russiaDirect.copy(name = "Россия напрямую, без рекламы", blockSites = listOf("geosite:category-ads-all"))

    val noAds get() = RoutingProfile(name = "Без рекламы", blockSites = listOf("geosite:category-ads-all"))

    val presets get() = listOf(russiaDirect, russiaDirectNoAds, noAds)

    private val geoTagRegex = Regex("^(geosite|geoip):!?[a-z0-9._@!-]{1,64}$", RegexOption.IGNORE_CASE)
    private val routingLinkRegex = Regex("^[a-z][a-z0-9+.-]*://routing/(?:onadd|add)/(.+)$", RegexOption.IGNORE_CASE)
    private val autoroutingLinkRegex = Regex("^[a-z][a-z0-9+.-]*://autorouting/(?:onadd|add)/(.+)$", RegexOption.IGNORE_CASE)

    /**
     * Профиль из того, что дала панель или пользователь: JSON, base64 от JSON или ссылка вида
     * «схема://routing/onadd/<base64>» (также add/…). null — это не профиль.
     */
    fun tryParse(text: String?): RoutingProfile? {
        var s = text?.trim()
        if (s.isNullOrEmpty() || s.equals("off", ignoreCase = true)) return null
        routingLinkRegex.find(s)?.let { s = Text.unescape(it.groupValues[1]) }
        if (!s!!.startsWith('{')) Text.decodeBase64(s!!)?.let { s = it.trim() }
        if (!s!!.startsWith('{')) return null
        return try {
            val node = Js.parse(s!!) as? JObj ?: return null
            validate(fromJson(node))
        } catch (e: IllegalArgumentException) {
            null
        } catch (e: ConfigException) {
            null
        }
    }

    /** Поля читаются без учёта регистра; значения бывают строками («"true"») — как их пишут панели. */
    private fun fromJson(o: JObj): RoutingProfile {
        fun get(vararg names: String): Any? = o.entries.firstOrNull { (k, _) -> names.any { it.equals(k, ignoreCase = true) } }?.value
        fun str(n: Any?): String? = n.scalarText()
        fun list(n: Any?): List<String> = when (n) {
            is List<*> -> n.mapNotNull { str(it)?.trim() }.filter { it.isNotEmpty() }
            is String, is Number, is Boolean -> str(n)!!.split(',', '\n').map { it.trim() }.filter { it.isNotEmpty() }
            else -> emptyList()
        }
        return RoutingProfile(
            name = str(get("Name"))?.trim()?.takeIf { it.isNotEmpty() } ?: "Профиль подписки",
            globalProxy = !str(get("GlobalProxy")).equals("false", ignoreCase = true),
            directSites = list(get("DirectSites")),
            directIp = list(get("DirectIp", "DirectIP")),
            proxySites = list(get("ProxySites")),
            proxyIp = list(get("ProxyIp", "ProxyIP")),
            blockSites = list(get("BlockSites")),
            blockIp = list(get("BlockIp", "BlockIP")),
            domainStrategy = str(get("DomainStrategy")) ?: "AsIs",
            lastUpdated = str(get("LastUpdated"))?.trim()?.toLongOrNull() ?: 0,
        )
    }

    /** Только встроенные базы geosite/geoip, домены, IP и CIDR; неверные элементы — ошибка, а не молчаливый пропуск. */
    fun validate(p: RoutingProfile): RoutingProfile {
        if (p.ruleCount > MAX_RULES) throw ConfigException("В профиле маршрутизации больше $MAX_RULES правил")
        if (p.domainStrategy !in listOf("AsIs", "IPIfNonMatch", "IPOnDemand"))
            throw ConfigException("Неизвестная стратегия DomainStrategy «${p.domainStrategy}»")
        return p.copy(
            name = if (p.name.length > 60) p.name.substring(0, 60) else p.name,
            directSites = sites(p.directSites), proxySites = sites(p.proxySites), blockSites = sites(p.blockSites),
            directIp = ips(p.directIp), proxyIp = ips(p.proxyIp), blockIp = ips(p.blockIp),
        )
    }

    private fun sites(items: List<String>) = items.map(::site).distinct()
    private fun ips(items: List<String>) = items.map(::ip).distinct()

    private fun site(raw: String): String {
        val s = raw.trim()
        if (geoTagRegex.matches(s) && s.startsWith("geosite:", ignoreCase = true)) return s.lowercase()
        for (prefix in listOf("regexp:", "keyword:", "full:", "domain:")) {
            if (!s.startsWith(prefix, ignoreCase = true)) continue
            val value = s.substring(prefix.length)
            if (value.isEmpty() || value.length > 300) break
            if (prefix == "regexp:") {
                try {
                    Regex(value)
                } catch (e: PatternSyntaxException) {
                    throw ConfigException("Профиль маршрутизации: «$raw» — неверное регулярное выражение")
                }
                return prefix + value
            }
            return if (prefix == "keyword:") prefix + value.lowercase()
            else prefix + (Domains.normalize(value) ?: throw ConfigException("Профиль маршрутизации: «$raw» — не сайт и не geosite"))
        }
        return "domain:" + (Domains.normalize(s) ?: throw ConfigException("Профиль маршрутизации: «$raw» — не сайт и не geosite"))
    }

    private fun ip(raw: String): String {
        val s = raw.trim()
        if (geoTagRegex.matches(s) && s.startsWith("geoip:", ignoreCase = true)) return s.lowercase()
        return Cidr.normalize(s) ?: throw ConfigException("Профиль маршрутизации: «$raw» — не IP, не подсеть и не geoip")
    }

    /**
     * Добавляет правила профиля в конфиг xray. Правила ставятся с позиции [insertAt] (после служебных правил клиента),
     * но перед «всё остальное». [balancerTag] — «через VPN» ведёт на балансировщик (автовыбор), а не на [proxyTag].
     */
    fun apply(config: JObj, profile: RoutingProfile, proxyTag: String, insertAt: Int, balancerTag: String? = null) {
        val outbounds = config.arrOrPut("outbounds")
        outbounds.add(Js.obj("tag" to DIRECT_TAG, "protocol" to "freedom"))
        outbounds.add(Js.obj("tag" to BLOCK_TAG, "protocol" to "blackhole"))

        val routing = config.obj("routing") ?: Js.obj("domainStrategy" to "AsIs").also { config["routing"] = it }
        val rules = routing.arrOrPut("rules")
        if (profile.domainStrategy != "AsIs") routing["domainStrategy"] = profile.domainStrategy

        // Порядок: блокировка, затем через VPN, затем напрямую.
        val added = mutableListOf<JObj>()
        fun add(key: String, items: List<String>, outbound: String) {
            if (items.isEmpty()) return
            val rule = Js.obj("type" to "field", key to Js.arrOf(items))
            if (outbound == proxyTag && balancerTag != null) rule["balancerTag"] = balancerTag else rule["outboundTag"] = outbound
            added.add(rule)
        }
        add("domain", profile.blockSites, BLOCK_TAG)
        add("ip", profile.blockIp, BLOCK_TAG)
        add("domain", profile.proxySites, proxyTag)
        add("ip", profile.proxyIp, proxyTag)
        add("domain", profile.directSites, DIRECT_TAG)
        add("ip", profile.directIp, DIRECT_TAG)
        if (!profile.globalProxy) added.add(Js.obj("type" to "field", "network" to "tcp,udp", "outboundTag" to DIRECT_TAG))

        var at = insertAt.coerceIn(0, rules.size)
        for (rule in added) rules.add(at++, rule)
    }

    /**
     * Автообновляемый профиль: «схема://autorouting/onadd/https://…» (или просто https://…) → адрес, откуда его брать.
     * null — это не такая ссылка. Только https.
     */
    fun autoroutingUrl(text: String?): URI? {
        var s = text?.trim()
        if (s.isNullOrEmpty()) return null
        autoroutingLinkRegex.find(s)?.let { s = Text.unescape(it.groupValues[1]) }
        return try {
            URI(s).takeIf { it.isAbsolute && it.scheme.equals("https", ignoreCase = true) && !it.host.isNullOrEmpty() }
        } catch (e: Exception) {
            null
        }
    }

    private val json = Json { encodeDefaults = true }

    /** Профиль как JSON — для хранения у подписки. */
    fun toJson(p: RoutingProfile): String = json.encodeToString(RoutingProfile.serializer(), p)
}

/** Домены сайтов: как SplitTunnelRules.NormalizeDomain на десктопе. */
object Domains {
    private val pattern = Regex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")

    /** «https://www.YouTube.com/watch» → «youtube.com»; null — не похоже на адрес сайта. */
    fun normalize(input: String): String? {
        var s = input.trim()
        s = if (s.contains("://")) {
            try {
                URI(s).host ?: s.substringAfter("://").split('/', '?', '#')[0]
            } catch (e: Exception) {
                s.substringAfter("://").split('/', '?', '#')[0]
            }
        } else s.split('/', '?', '#')[0]
        s = s.trimStart('*').trim('.').lowercase()
        if (s.isEmpty()) return null
        s = try {
            IDN.toASCII(s, IDN.ALLOW_UNASSIGNED).lowercase()
        } catch (e: IllegalArgumentException) {
            return null
        }
        if (s.length > 253 || !pattern.matches(s)) return null
        // www.youtube.com → youtube.com: иначе m.youtube.com и сам youtube.com прошли бы мимо правила.
        if (s.startsWith("www.") && s.count { it == '.' } >= 2) s = s.substring(4)
        return s
    }
}

/** IP и подсети: «1.2.3.4», «10.0.0.0/8», «fc00::/7». */
object Cidr {
    /** Нормализованный вид или null — не IP и не подсеть. */
    fun normalize(s: String): String? {
        val slash = s.indexOf('/')
        val addressText = if (slash < 0) s else s.substring(0, slash)
        val v4 = isIpv4(addressText)
        if (!v4 && !isIpv6(addressText)) return null
        val address = try {
            InetAddress.getByName(addressText).hostAddress
        } catch (e: Exception) {
            return null
        }
        val max = if (v4) 32 else 128
        if (slash < 0) return address
        val prefix = s.substring(slash + 1).toIntOrNull() ?: return null
        if (prefix !in 0..max) return null
        return "$address/$prefix"
    }
}
