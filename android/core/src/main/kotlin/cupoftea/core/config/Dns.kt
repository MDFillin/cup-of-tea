package cupoftea.core.config

import kotlinx.serialization.Serializable
import java.net.IDN
import java.net.URI
import java.net.URISyntaxException

@Serializable
enum class DnsProvider(val title: String) {
    Cloudflare("Cloudflare"),
    Google("Google"),
    Quad9("Quad9"),

    /** AdGuard DNS: блокирует рекламу и трекеры. */
    AdGuard("AdGuard (без рекламы)"),

    /** Свой адрес DNS-over-HTTPS (вводится со страницы на телефоне). */
    Custom("Свой"),
}

/** DNS через туннель (DNS-over-HTTPS внутри xray). Порт src/PVpn.Core/Config/DnsOptions.cs. */
@Serializable
data class DnsOptions(
    val provider: DnsProvider = DnsProvider.Cloudflare,
    /** Для Custom: «https://dns.example.com/dns-query» или «1.2.3.4». */
    val customUrl: String? = null,
)

/** Сервер DNS-over-HTTPS. host — IP или домен; serverName — имя для TLS, если хост — IP. */
data class DohServer(val host: String, val port: Int, val path: String, val serverName: String?) {
    val hostIsIp: Boolean get() = isIpLiteral(host)
    override fun toString() = "https://${serverName ?: host}${if (port == 443) "" else ":$port"}$path"
}

object DnsRules {
    /** Готовые серверы: по IP (не нужен отдельный DNS, чтобы найти сам DNS) и с именем для проверки сертификата. */
    fun preset(provider: DnsProvider): DohServer = when (provider) {
        DnsProvider.Cloudflare -> DohServer("1.1.1.1", 443, "/dns-query", "cloudflare-dns.com")
        DnsProvider.Google -> DohServer("8.8.8.8", 443, "/dns-query", "dns.google")
        DnsProvider.Quad9 -> DohServer("9.9.9.9", 443, "/dns-query", "dns.quad9.net")
        DnsProvider.AdGuard -> DohServer("94.140.14.14", 443, "/dns-query", "dns.adguard-dns.com")
        DnsProvider.Custom -> throw ConfigException("У своего DNS нет готового адреса")
    }

    fun resolve(options: DnsOptions?): DohServer =
        if (options?.provider == DnsProvider.Custom) parseCustom(options.customUrl)
        else preset(options?.provider ?: DnsProvider.Cloudflare)

    fun validate(options: DnsOptions): DnsOptions =
        if (options.provider != DnsProvider.Custom) options.copy(customUrl = options.customUrl?.trim())
        else options.copy(customUrl = parseCustom(options.customUrl).toString())

    /** «https://host[:порт]/путь», «host» или «1.2.3.4» (путь по умолчанию /dns-query). Только HTTPS. */
    fun parseCustom(url: String?): DohServer {
        var s = url?.trim() ?: ""
        if (s.isEmpty()) throw ConfigException("Свой DNS: укажите адрес DNS-over-HTTPS, например https://dns.example.com/dns-query")
        if (!s.contains("://")) s = "https://$s"
        val uri = try {
            URI(s)
        } catch (e: URISyntaxException) {
            null
        }
        if (uri == null || !uri.isAbsolute || !uri.scheme.equals("https", ignoreCase = true))
            throw ConfigException("Свой DNS: «$url» — нужен адрес DNS-over-HTTPS (https://…)")
        if (uri.rawUserInfo != null || uri.rawFragment != null || uri.host.isNullOrEmpty())
            throw ConfigException("Свой DNS: «$url» — неверный адрес")
        if (uri.host.startsWith("["))
            throw ConfigException("Свой DNS: адрес IPv6 не поддерживается (туннель работает только по IPv4)")
        val host = IDN.toASCII(uri.host).lowercase()
        var path = if (uri.rawPath.isNullOrEmpty() || uri.rawPath == "/") "/dns-query" else uri.rawPath
        if (uri.rawQuery != null) path += "?" + uri.rawQuery
        return DohServer(host, if (uri.port < 0) 443 else uri.port, path, null)
    }
}

/** IPv4 или IPv6 буквально (без обращения к DNS, в отличие от InetAddress.getByName). */
fun isIpLiteral(s: String): Boolean = isIpv4(s) || isIpv6(s)

fun isIpv4(s: String): Boolean {
    val parts = s.split('.')
    return parts.size == 4 && parts.all { p -> p.isNotEmpty() && p.length <= 3 && p.all { it.isDigit() } && p.toInt() <= 255 }
}

fun isIpv6(s: String): Boolean {
    val v = s.trim('[', ']').substringBefore('%')
    if (!v.contains(':') || v.count { it == ':' } < 2) return false
    return v.all { it.isDigit() || it in 'a'..'f' || it in 'A'..'F' || it == ':' || it == '.' } &&
        v.split("::").size <= 2
}
