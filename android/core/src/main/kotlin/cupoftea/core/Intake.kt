package cupoftea.core

import cupoftea.core.config.ConfigException
import cupoftea.core.config.DnsRules
import cupoftea.core.config.ServerConfigs
import cupoftea.core.routing.RoutingProfile
import cupoftea.core.routing.RoutingProfiles

/** Что прислали с телефона (или вставили): подписка, серверы, профиль маршрутизации, свой DNS. */
sealed interface Intake {
    data class Subscription(val url: String) : Intake
    /** Ссылки серверов (в том числе base64-подпиской) или JSON с конфигами xray. */
    data class Servers(val text: String) : Intake
    data class Routing(val profile: RoutingProfile) : Intake
    /** Профиль маршрутизации по адресу (обновляется сам). */
    data class Autorouting(val url: String) : Intake
    data class Dns(val url: String) : Intake
    data class Unknown(val reason: String) : Intake
}

/**
 * Разбор присланного текста. Понимает ссылки действий cupoftea:// (и с любой другой схемой — так их выдают
 * панели и боты): add|onadd|import|sub/<подписка или base64>, routing/onadd/<профиль>, autorouting/onadd/<https://…>.
 * Порт MainViewModel.Links.cs десктопа.
 */
object IntakeParser {
    private val appLink = Regex(
        "^[a-z][a-z0-9+.-]*://(?<action>add|onadd|import|sub|routing/onadd|routing/add|autorouting/onadd|autorouting/add)/(?<payload>.+)$",
        RegexOption.IGNORE_CASE,
    )

    private val routingKeys = listOf("DirectSites", "DirectIp", "ProxySites", "ProxyIp", "BlockSites", "BlockIp", "GlobalProxy")

    fun isAppLink(text: String?) = text != null && appLink.matches(text.trim())

    /** [dns] — поле «свой DNS-over-HTTPS» на странице телефона. */
    fun parse(text: String, dns: Boolean = false): Intake {
        val t = text.trim()
        if (t.isEmpty()) return Intake.Unknown("пусто")
        if (dns) return try {
            Intake.Dns(DnsRules.parseCustom(t).toString())
        } catch (e: ConfigException) {
            Intake.Unknown(e.message ?: "неверный адрес DNS")
        }

        appLink.find(t)?.let { m ->
            val action = m.groups["action"]!!.value.lowercase()
            val payload = Text.unescape(m.groups["payload"]!!.value)
            if (action.startsWith("autorouting")) {
                return RoutingProfiles.autoroutingUrl(payload)?.let { Intake.Autorouting(it.toASCIIString()) }
                    ?: Intake.Unknown("В ссылке нет адреса профиля маршрутизации (нужен https://…)")
            }
            if (action.startsWith("routing")) {
                return RoutingProfiles.tryParse(payload)?.let { Intake.Routing(it) }
                    ?: Intake.Unknown("В ссылке нет профиля маршрутизации")
            }
            // add/onadd/import/sub: адрес подписки, base64 от него или сами ссылки серверов.
            val inner = if (payload.contains("://")) payload else Text.decodeBase64(payload)?.trim() ?: payload
            return if (isHttpUrl(inner)) Intake.Subscription(inner) else Intake.Servers(inner)
        }

        if (t.startsWith('{') || t.startsWith('[')) {
            // JSON: конфиги xray (с outbounds) или профиль маршрутизации.
            if (t.contains("\"outbounds\"") && ServerConfigs.looksLikeConfigs(t)) return Intake.Servers(t)
            // Разбор профиля терпимый (как на десктопе) — требуем хотя бы одно его поле, иначе любой JSON стал бы профилем.
            val looksLikeRouting = routingKeys.any { t.contains("\"$it\"", ignoreCase = true) }
            if (looksLikeRouting) RoutingProfiles.tryParse(t)?.let { return Intake.Routing(it) }
            return Intake.Unknown("JSON не похож ни на конфиги серверов, ни на профиль маршрутизации")
        }
        val firstLine = t.lineSequence().first().trim()
        if (isHttpUrl(firstLine) && !t.contains("vless://") && !t.contains("hy2://")) return Intake.Subscription(firstLine)
        return Intake.Servers(t)
    }

    private fun isHttpUrl(s: String) =
        (s.startsWith("https://", ignoreCase = true) || s.startsWith("http://", ignoreCase = true)) && !s.contains(' ')
}
