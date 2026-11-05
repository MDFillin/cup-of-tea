package cupoftea.core.config

import cupoftea.core.JArr
import cupoftea.core.JObj
import cupoftea.core.Js
import cupoftea.core.bool
import cupoftea.core.obj
import cupoftea.core.objOrPut
import cupoftea.core.profiles.Profile
import cupoftea.core.profiles.StreamProfile
import cupoftea.core.profiles.XrayConfigProfile
import java.security.MessageDigest
import java.util.UUID

/**
 * «Автовыбор» для серверов по ссылкам: из группы серверов собирается конфиг xray с балансировщиком — у каждого
 * сервера свой выход, xray сам проверяет их и ведёт трафик через самый быстрый (leastPing). Порт AutoSelect.cs.
 */
object AutoSelect {
    const val BALANCER_TAG = "auto"

    /** Префикс выходов-серверов: по нему их выбирают балансировщик и проверки. */
    const val SERVER_PREFIX = "srv-"
    private const val DIRECT_TAG = "auto-direct"

    /** Пометка в meta: конфиг собран клиентом, к нему применяется свой профиль маршрутизации. */
    const val META_KEY = "clientAuto"

    const val MIN_SERVERS = 2

    /** Серверы, которые могут войти в автовыбор: по ссылкам (у конфигов из подписки свои балансировщики). */
    fun canJoin(p: Profile) = p !is XrayConfigProfile

    /** Постоянный Id автовыбора группы — чтобы выбор в списке переживал перезапуск (как Guid из MD5 на десктопе). */
    fun idFor(subscriptionId: String?): String {
        val hash = MessageDigest.getInstance("MD5").digest(("cupoftea-auto:" + (subscriptionId ?: "own")).toByteArray())
        // Порядок байтов как у System.Guid(byte[]): первые три поля — little-endian.
        val b = hash.copyOf()
        fun swap(i: Int, j: Int) {
            val t = b[i]; b[i] = b[j]; b[j] = t
        }
        swap(0, 3); swap(1, 2); swap(4, 5); swap(6, 7)
        var msb = 0L
        var lsb = 0L
        for (i in 0..7) msb = (msb shl 8) or (b[i].toLong() and 0xFF)
        for (i in 8..15) lsb = (lsb shl 8) or (b[i].toLong() and 0xFF)
        return UUID(msb, lsb).toString()
    }

    /** [servers] — серверы группы и их настройки обхода (свои или с поправками панели подписки). */
    fun build(name: String, id: String, subscriptionId: String?, servers: List<Pair<Profile, BypassOptions>>): XrayConfigProfile {
        if (servers.size < MIN_SERVERS) throw ConfigException("для автовыбора нужно хотя бы $MIN_SERVERS сервера")
        val outbounds = JArr()
        for ((index, entry) in servers.withIndex()) {
            serverOutbounds(entry.first, "$SERVER_PREFIX$index", "auto-fragment-$index", entry.second).forEach { outbounds.add(it) }
        }
        return fromOutbounds(name, id, subscriptionId, outbounds, servers.size)
    }

    internal fun fromOutbounds(name: String, id: String, subscriptionId: String?, outbounds: JArr, count: Int): XrayConfigProfile {
        outbounds.add(Js.obj("tag" to DIRECT_TAG, "protocol" to "freedom"))
        val config = Js.obj(
            "remarks" to name,
            "meta" to Js.obj(META_KEY to true),
            "outbounds" to outbounds,
            "burstObservatory" to Js.obj(
                "subjectSelector" to Js.arr(SERVER_PREFIX),
                "pingConfig" to Js.obj(
                    "destination" to "http://www.gstatic.com/generate_204",
                    "interval" to "1m",
                    "sampling" to 2,
                    "timeout" to "5s",
                ),
            ),
            "routing" to Js.obj(
                "domainStrategy" to "AsIs",
                "balancers" to Js.arr(
                    Js.obj(
                        "tag" to BALANCER_TAG,
                        "selector" to Js.arr(SERVER_PREFIX),
                        "strategy" to Js.obj("type" to "leastPing"),
                        "fallbackTag" to SERVER_PREFIX + "0",
                    )
                ),
                "rules" to Js.arr(
                    Js.obj("type" to "field", "domain" to Js.arr("localhost"), "outboundTag" to DIRECT_TAG),
                    Js.obj("type" to "field", "ip" to Js.arrOf(XrayConfigBuilder.privateCidrs), "outboundTag" to DIRECT_TAG),
                    Js.obj("type" to "field", "network" to "tcp,udp", "balancerTag" to BALANCER_TAG),
                ),
            ),
        )
        val (built, _) = ServerConfigs.fromConfig(config)
        return built.copy(id = id, subscriptionId = subscriptionId, description = "лучший по пингу из $count")
    }

    /** Конфиг собран клиентом (см. build). */
    fun isClientAuto(config: JObj) = config.obj("meta")?.bool(META_KEY) == true

    /** Выходы одного сервера по ссылке: сам сервер и, если включена фрагментация, его собственный выход фрагментации. */
    internal fun serverOutbounds(profile: Profile, tag: String, fragmentTag: String, bypass: BypassOptions): List<JObj> {
        val b = BypassRules.validate(bypass)
        val fragment = b.fragment && profile is StreamProfile && profile.stream.security in listOf("tls", "reality")
        val main = XrayConfigBuilder.buildOutbound(profile, tag, fragment, b)
        if (!fragment) return listOf(main)
        main.objOrPut("streamSettings").objOrPut("sockopt")["dialerProxy"] = fragmentTag
        val frag = XrayConfigBuilder.buildFragmentOutbound(b)
        frag["tag"] = fragmentTag
        return listOf(main, frag)
    }
}
