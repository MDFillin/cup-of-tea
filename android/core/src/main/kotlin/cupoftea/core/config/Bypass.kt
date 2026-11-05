package cupoftea.core.config

import kotlinx.serialization.Serializable
import java.util.Base64

class ConfigException(message: String) : Exception(message)

/**
 * Настройки обхода блокировок. Значения по умолчанию проверены на домашней сети (фрагментация, mux, шумы).
 * null у отпечатка и режима XHTTP — взять из ссылки сервера. Порт src/PVpn.Core/Config/BypassOptions.cs.
 */
@Serializable
data class BypassOptions(
    /** Отпечаток TLS (uTLS); null — из ссылки, а если там нет — chrome. */
    val fingerprint: String? = null,
    /** Режим XHTTP; null — из ссылки. */
    val xhttpMode: String? = null,
    /** Резать начало TLS-соединения на куски: DPI не видит SNI. «tlshello» — только ClientHello, «1-3» — первые записи. */
    val fragment: Boolean = true,
    val fragmentPackets: String = "tlshello",
    val fragmentLength: String = "100-200",
    val fragmentInterval: String = "10-20",
    /** mux.cool для TCP-транспорта: у Vision через mux идёт только UDP (xudp), у XHTTP своё — xmux. */
    val mux: Boolean = true,
    val muxConcurrency: Int = 8,
    val xudpConcurrency: Int = 16,
    /** Свои параметры xmux для XHTTP; null — из ссылки или значения xray. */
    val xmux: XmuxOptions? = null,
    /** Шумы перед подключением Hysteria2 (UDP). */
    val noises: Boolean = true,
    /** rand — случайные байты (длина — noisePacket), str, hex или base64 — свои данные. */
    val noiseType: String = "rand",
    val noisePacket: String = "50-100",
    val noiseDelay: String = "10-16",
    /** Как часто Hysteria2 меняет порт, если в ссылке диапазон портов. */
    val hopIntervalSeconds: Int = 30,
) {
    companion object {
        val Default = BypassOptions()
    }
}

/** Параметры xmux (XHTTP). Пустое поле — значение xray по умолчанию. */
@Serializable
data class XmuxOptions(
    val maxConcurrency: String? = null,
    val maxConnections: String? = null,
    val cMaxReuseTimes: String? = null,
    val hMaxRequestTimes: String? = null,
    val hMaxReusableSecs: String? = null,
    val hKeepAlivePeriod: Int = 0,
)

/**
 * Готовые варианты обхода для телевизора вместо десятка полей: «Выкл», «Стандарт» (как на десктопе по умолчанию)
 * и «Усиленный» (фрагментация первых записей и шумы подлиннее).
 */
enum class BypassPreset(val title: String) {
    Off("Выкл"),
    Standard("Стандарт"),
    Strong("Усиленный");

    fun options(): BypassOptions = when (this) {
        Off -> BypassOptions(fragment = false, mux = false, noises = false)
        Standard -> BypassOptions.Default
        Strong -> BypassOptions(
            fragmentPackets = "1-3", fragmentLength = "50-150", fragmentInterval = "10-30",
            noisePacket = "100-200", noiseDelay = "10-30",
        )
    }
}

object BypassRules {
    val fingerprints = listOf("chrome", "firefox", "safari", "ios", "android", "edge", "random", "randomized")
    val xhttpModes = listOf("auto", "packet-up", "stream-up", "stream-one")
    val noiseTypes = listOf("rand", "str", "hex", "base64")

    private val rangeRegex = Regex("^(\\d{1,6})(?:-(\\d{1,6}))?$")

    /** Проверяет и нормализует настройки. */
    fun validate(o: BypassOptions): BypassOptions {
        val fingerprint = choice(o.fingerprint, fingerprints, "Отпечаток TLS")
        val mode = choice(o.xhttpMode, xhttpModes, "Режим XHTTP")

        var packets = o.fragmentPackets.trim().lowercase()
        if (packets != "tlshello")
            packets = range(packets, 1, 100, "Фрагментация: пакеты (tlshello или диапазон записей, например 1-3)")
        val length = range(o.fragmentLength, 1, 16384, "Фрагментация: длина куска, байт")
        val interval = range(o.fragmentInterval, 0, 1000, "Фрагментация: пауза, мс")

        if (o.muxConcurrency !in 1..1024) throw ConfigException("Mux: число потоков на соединение — от 1 до 1024")
        if (o.xudpConcurrency !in 1..1024) throw ConfigException("Mux: число UDP-потоков (xudp) на соединение — от 1 до 1024")

        val noiseType = choice(o.noiseType, noiseTypes, "Шумы: тип") ?: "rand"
        val noisePacket = validateNoisePacket(noiseType, o.noisePacket)
        val noiseDelay = range(o.noiseDelay, 0, 1000, "Шумы: пауза, мс")

        if (o.hopIntervalSeconds !in 5..3600) throw ConfigException("Смена портов Hysteria2: интервал — от 5 до 3600 секунд")

        return o.copy(
            fingerprint = fingerprint,
            xhttpMode = mode,
            fragmentPackets = packets,
            fragmentLength = length,
            fragmentInterval = interval,
            xmux = o.xmux?.let { validateXmux(it) },
            noiseType = noiseType,
            noisePacket = noisePacket,
            noiseDelay = noiseDelay,
        )
    }

    private fun validateXmux(x: XmuxOptions): XmuxOptions {
        if (x.hKeepAlivePeriod !in 0..600) throw ConfigException("XMUX: keep-alive, с (hKeepAlivePeriod) — от 0 до 600")
        val result = XmuxOptions(
            maxConcurrency = optionalRange(x.maxConcurrency, 1, 1024, "XMUX: потоков на соединение (maxConcurrency)"),
            maxConnections = optionalRange(x.maxConnections, 1, 1024, "XMUX: соединений (maxConnections)"),
            cMaxReuseTimes = optionalRange(x.cMaxReuseTimes, 1, 100000, "XMUX: повторных использований соединения (cMaxReuseTimes)"),
            hMaxRequestTimes = optionalRange(x.hMaxRequestTimes, 1, 100000, "XMUX: запросов на соединение (hMaxRequestTimes)"),
            hMaxReusableSecs = optionalRange(x.hMaxReusableSecs, 1, 86400, "XMUX: время жизни соединения, с (hMaxReusableSecs)"),
            hKeepAlivePeriod = x.hKeepAlivePeriod,
        )
        if (result.maxConcurrency != null && result.maxConnections != null)
            throw ConfigException("XMUX: maxConcurrency и maxConnections нельзя задать вместе — оставьте одно из полей пустым")
        return result
    }

    private fun validateNoisePacket(type: String, packet: String?): String {
        val p = packet?.trim() ?: ""
        return when (type) {
            "rand" -> range(p, 1, 8192, "Шумы: длина случайного пакета, байт")
            "str" -> if (p.length in 1..1024) p else throw ConfigException("Шумы: текст пакета — от 1 до 1024 символов")
            "hex" -> {
                var hex = p.replace(" ", "")
                if (hex.startsWith("0x", ignoreCase = true)) hex = hex.substring(2)
                if (hex.isEmpty() || hex.length > 2048 || hex.length % 2 != 0 || !hex.all { it.isHexDigit() })
                    throw ConfigException("Шумы: пакет в hex — чётное число шестнадцатеричных цифр, до 1024 байт")
                hex.lowercase()
            }
            else -> {
                val bytes = try {
                    Base64.getDecoder().decode(p.filterNot { it.isWhitespace() })
                } catch (e: IllegalArgumentException) {
                    null
                }
                if (bytes != null && bytes.size in 1..1024) p else throw ConfigException("Шумы: пакет в base64 — от 1 до 1024 байт")
            }
        }
    }

    private fun Char.isHexDigit() = this in '0'..'9' || this in 'a'..'f' || this in 'A'..'F'

    private fun choice(value: String?, allowed: List<String>, what: String): String? {
        if (value.isNullOrBlank()) return null
        val v = value.trim().lowercase()
        return if (v in allowed) v else throw ConfigException("$what: «$value» не поддерживается (есть ${allowed.joinToString(", ")})")
    }

    private fun optionalRange(value: String?, min: Int, max: Int, what: String) =
        if (value.isNullOrBlank()) null else range(value, min, max, what)

    /** Число или диапазон «от-до» в формате xray, например «100-200». */
    fun range(value: String?, min: Int, max: Int, what: String): String {
        val m = rangeRegex.find(value?.replace(" ", "") ?: "")
        if (m != null) {
            val from = m.groupValues[1].toInt()
            val to = m.groupValues[2].takeIf { it.isNotEmpty() }?.toInt() ?: from
            if (from >= min && to <= max && from <= to) return if (from == to) "$from" else "$from-$to"
        }
        throw ConfigException("$what: нужно число или диапазон «от-до» в пределах $min–$max, а не «$value»")
    }
}

/**
 * Настройки обхода, которые присылает панель подписки заголовками (fragmentation-*, noises-*). Применяются к серверам
 * этой подписки поверх настроек пользователя. null в поле — панель его не задала. Порт PanelBypass.cs.
 */
@Serializable
data class PanelBypass(
    val fragment: Boolean? = null,
    val fragmentPackets: String? = null,
    val fragmentLength: String? = null,
    val fragmentInterval: String? = null,
    val noises: Boolean? = null,
    val noiseType: String? = null,
    val noisePacket: String? = null,
    val noiseDelay: String? = null,
) {
    /** Настройки пользователя с заменой того, что задала панель (проверять — BypassRules.validate). */
    fun applyTo(user: BypassOptions) = user.copy(
        fragment = fragment ?: user.fragment,
        fragmentPackets = fragmentPackets ?: user.fragmentPackets,
        fragmentLength = fragmentLength ?: user.fragmentLength,
        fragmentInterval = fragmentInterval ?: user.fragmentInterval,
        noises = noises ?: user.noises,
        noiseType = noiseType ?: user.noiseType,
        noisePacket = noisePacket ?: user.noisePacket,
        noiseDelay = noiseDelay ?: user.noiseDelay,
    )

    /** Коротко для интерфейса: «фрагментация tlshello 10-20 байт 5-15 мс; шумы выкл.». */
    fun describe(): String {
        val parts = mutableListOf<String>()
        if (fragment == false) parts.add("фрагментация выкл.")
        else if (fragment == true || fragmentPackets != null || fragmentLength != null || fragmentInterval != null)
            parts.add(
                ("фрагментация " + listOfNotNull(
                    fragmentPackets, fragmentLength?.let { "$it байт" }, fragmentInterval?.let { "$it мс" },
                ).joinToString(" ")).trim()
            )
        if (noises == false) parts.add("шумы выкл.")
        else if (noises == true || noiseType != null || noisePacket != null || noiseDelay != null)
            parts.add(("шумы " + listOfNotNull(noiseType, noisePacket, noiseDelay?.let { "$it мс" }).joinToString(" ")).trim())
        return parts.joinToString("; ")
    }

    companion object {
        /** Из заголовков панели; null — ни одного такого заголовка нет. */
        fun fromHeaders(header: (String) -> String?): PanelBypass? {
            fun clean(s: String?): String? = if (s.isNullOrBlank()) null else if (s.trim().length > 200) null else s.trim()
            fun flag(s: String?): Boolean? = when (clean(s)?.lowercase()) {
                "1", "true", "on" -> true
                "0", "false", "off" -> false
                else -> null
            }
            val packets = clean(header("fragmentation-packets"))?.lowercase()
            val result = PanelBypass(
                fragment = flag(header("fragmentation-enable")),
                // «all» у панелей — все записи соединения; у xray это диапазон записей.
                fragmentPackets = if (packets == "all") "1-100" else packets,
                fragmentLength = clean(header("fragmentation-length")),
                fragmentInterval = clean(header("fragmentation-interval")),
                noises = flag(header("noises-enable")),
                noiseType = clean(header("noises-type"))?.lowercase(),
                noisePacket = clean(header("noises-packet")),
                noiseDelay = clean(header("noises-delay")),
            )
            return if (result == PanelBypass()) null else result
        }
    }
}
