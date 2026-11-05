package cupoftea.core.profiles

import cupoftea.core.Text
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

class LinkParseException(message: String) : Exception(message)

data class ImportError(val line: Int, val message: String)

data class ImportResult(val profiles: List<Profile>, val errors: List<ImportError>)

/** Разбор ссылок vless, hy2 (hysteria2), trojan, vmess, ss. Порт src/PVpn.Core/Profiles/LinkParser.cs. */
object LinkParser {
    private val linkRegex = Regex("(?<![a-z0-9])(?:vless|hy2|hysteria2|trojan|vmess|ss)://\\S+", RegexOption.IGNORE_CASE)

    /**
     * Достаёт все ссылки из произвольного текста (по одной или несколько в строке, с подписями вокруг).
     * Если ссылок нет, пробует раскодировать текст как base64-подписку.
     */
    fun parseMany(text: String): ImportResult {
        val profiles = mutableListOf<Profile>()
        val errors = mutableListOf<ImportError>()
        val lines = text.replace("\r\n", "\n").split('\n')
        for ((i, line) in lines.withIndex()) {
            for (m in linkRegex.findAll(line)) {
                try {
                    profiles.add(parse(m.value))
                } catch (e: LinkParseException) {
                    errors.add(ImportError(i + 1, e.message ?: ""))
                }
            }
        }
        if (profiles.isEmpty() && errors.isEmpty()) {
            val decoded = Text.decodeBase64(text.trim())
            if (decoded != null && linkRegex.containsMatchIn(decoded)) return parseMany(decoded)
        }
        return ImportResult(profiles, errors)
    }

    fun parse(link: String): Profile {
        val l = link.trim()
        // vmess:// чаще всего — base64 от JSON, а не обычная ссылка.
        if (l.startsWith("vmess://", ignoreCase = true)) return parseVmess(l)
        // ss:// старого вида целиком в base64 — split разобрать не сможет.
        if (l.startsWith("ss://", ignoreCase = true)) return parseShadowsocks(l)
        val raw = split(l)
        return when (raw.scheme) {
            "vless" -> parseVless(raw, l)
            "hy2", "hysteria2" -> parseHysteria2(raw, l)
            "trojan" -> parseTrojan(raw, l)
            else -> throw LinkParseException("Неизвестный тип ссылки: ${raw.scheme}://")
        }
    }

    private fun parseVless(r: RawLink, link: String): VlessProfile {
        if (r.userInfo.isNullOrEmpty()) throw LinkParseException("В ссылке vless нет идентификатора пользователя (UUID)")
        val q = r.query
        return VlessProfile(
            name = nameOrDefault(r), host = r.host, port = r.port, sourceLink = link,
            uuid = r.userInfo,
            encryption = q["encryption"].nullIfEmpty() ?: "none",
            flow = q["flow"].nullIfEmpty(),
            stream = parseStream(q, defaultSecurity = "none"),
        )
    }

    /** trojan://пароль@хост:порт?security=tls&sni=…&type=ws&path=…#имя (по умолчанию TLS). */
    private fun parseTrojan(r: RawLink, link: String): TrojanProfile {
        if (r.userInfo.isNullOrEmpty()) throw LinkParseException("В ссылке trojan нет пароля (часть до @)")
        return TrojanProfile(
            name = nameOrDefault(r), host = r.host, port = r.port, sourceLink = link, password = r.userInfo,
            stream = parseStream(r.query, defaultSecurity = "tls"),
        )
    }

    /**
     * vmess://base64(JSON) (ps, add, port, id, scy, net, type, host, path, tls, sni, alpn, fp)
     * или обычная ссылка vmess://uuid@хост:порт?type=…&security=…#имя.
     */
    private fun parseVmess(link: String): VmessProfile {
        val body = link.substring(8)
        val hash = body.indexOf('#')
        val json = Text.decodeBase64(if (hash >= 0) body.substring(0, hash) else body)
        if (json != null && json.trimStart().startsWith('{')) {
            val o = try {
                Json.parseToJsonElement(json) as? JsonObject ?: throw LinkParseException("Ссылка vmess: в base64 не JSON")
            } catch (e: IllegalArgumentException) {
                throw LinkParseException("Ссылка vmess: в base64 не JSON")
            }
            fun get(name: String): String? {
                val v = o[name] as? JsonPrimitive ?: return null
                return if (v.isString) v.content.trim().nullIfEmpty() else if (v.content == "null" || v.content == "true" || v.content == "false") null else v.content
            }
            val host = get("add") ?: throw LinkParseException("В ссылке vmess нет адреса сервера (add)")
            val port = get("port")?.trim()?.toIntOrNull()
            if (port == null || port !in 1..65535) throw LinkParseException("В ссылке vmess неверный порт")
            val id = get("id") ?: throw LinkParseException("В ссылке vmess нет UUID (id)")
            val net = get("net") ?: "tcp"
            // Параметры JSON — в виде, как у обычной ссылки, чтобы транспорт разбирался одним кодом.
            val q = CaseInsensitiveMap()
            q["type"] = net
            fun put(key: String, value: String?) {
                if (value != null) q[key] = value
            }
            put("security", get("tls"))
            put("sni", get("sni"))
            put("alpn", get("alpn"))
            put("fp", get("fp"))
            put("host", get("host"))
            put("path", get("path"))
            put("headerType", get("type"))
            put("pbk", get("pbk"))
            put("sid", get("sid"))
            put("allowInsecure", get("allowInsecure"))
            put("pcs", get("pcs"))
            put("vcn", get("vcn"))
            if (net == "grpc") {
                put("serviceName", get("path"))
                put("mode", if (get("type") == "multi") "multi" else null)
                q.remove("headerType")
            }
            val name = get("ps") ?: (if (hash >= 0) Text.unescape(body.substring(hash + 1)).trim() else null) ?: "$host:$port"
            return VmessProfile(
                name = name, host = host.trim('[', ']'), port = port, sourceLink = link, uuid = id,
                cipher = normalizeVmessCipher(get("scy")),
                stream = parseStream(q, defaultSecurity = "none"),
            )
        }

        val r = split(link)
        if (r.userInfo.isNullOrEmpty()) throw LinkParseException("В ссылке vmess нет UUID")
        // В обычной ссылке vmess «security» — шифрование VMess, а защита транспорта — в «security=tls|reality».
        val query = CaseInsensitiveMap(r.query)
        var cipher = query["encryption"]
        val sec = query["security"]
        if (sec != null && sec !in listOf("tls", "reality", "none")) {
            if (cipher == null) cipher = sec
            query.remove("security")
        }
        return VmessProfile(
            name = nameOrDefault(r), host = r.host, port = r.port, sourceLink = link, uuid = r.userInfo,
            cipher = normalizeVmessCipher(cipher),
            stream = parseStream(query, defaultSecurity = "none"),
        )
    }

    private fun normalizeVmessCipher(cipher: String?): String {
        val c = cipher?.trim()?.lowercase() ?: "auto"
        return when (c) {
            "", "auto" -> "auto"
            "aes-128-gcm", "chacha20-poly1305", "none", "zero" -> c
            else -> throw LinkParseException("Шифрование VMess «$c» не поддерживается (auto, aes-128-gcm, chacha20-poly1305, none, zero)")
        }
    }

    private val shadowsocksMethods = setOf(
        "aes-128-gcm", "aes-256-gcm", "chacha20-poly1305", "chacha20-ietf-poly1305", "xchacha20-poly1305",
        "xchacha20-ietf-poly1305", "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "none", "plain",
    )

    /**
     * ss://base64(метод:пароль)@хост:порт#имя (SIP002), ss://метод:пароль@хост:порт (пароль в процентной кодировке)
     * или старое ss://base64(метод:пароль@хост:порт)#имя. Плагины не поддерживаются.
     */
    private fun parseShadowsocks(link: String): ShadowsocksProfile {
        var body = link.substring(5)
        var fragment = ""
        val hash = body.indexOf('#')
        if (hash >= 0) {
            fragment = Text.unescape(body.substring(hash + 1)).trim()
            body = body.substring(0, hash)
        }
        // Старый вид: всё до «#» — base64, внутри «метод:пароль@хост:порт».
        if (!body.contains('@')) {
            val legacy = Text.decodeBase64(body.split('?', '/')[0])
            if (legacy != null && legacy.contains('@')) body = legacy
        }
        val r = split("ss://" + body + if (fragment.isNotEmpty()) "#" + Text.escapeDataString(fragment) else "")
        val plugin = r.query["plugin"].nullIfEmpty()
        if (plugin != null && !plugin.startsWith("none", ignoreCase = true))
            throw LinkParseException("Плагин Shadowsocks «${plugin.split(';')[0]}» не поддерживается")
        var userInfo = r.userInfo ?: throw LinkParseException("В ссылке ss нет метода и пароля")
        if (!userInfo.contains(':')) Text.decodeBase64(userInfo)?.let { userInfo = it }
        val colon = userInfo.indexOf(':')
        if (colon <= 0 || colon == userInfo.length - 1)
            throw LinkParseException("В ссылке ss нет метода и пароля («метод:пароль»)")
        val method = userInfo.substring(0, colon).trim().lowercase()
        if (method !in shadowsocksMethods) throw LinkParseException("Метод Shadowsocks «$method» не поддерживается")
        return ShadowsocksProfile(
            name = nameOrDefault(r), host = r.host, port = r.port, sourceLink = link,
            method = method, password = userInfo.substring(colon + 1),
        )
    }

    /**
     * Транспорт и защита из параметров ссылки: type (tcp, xhttp, ws, grpc, httpupgrade), security (reality, tls, none),
     * sni, fp, alpn, pbk, sid, spx, path, host, mode, serviceName, extra, allowInsecure.
     */
    private fun parseStream(q: Map<String, String>, defaultSecurity: String): StreamFields {
        val network = when (val t = (q["type"].nullIfEmpty() ?: "tcp").lowercase()) {
            "tcp", "raw" -> "raw"
            "xhttp", "splithttp" -> "xhttp"
            "ws", "websocket" -> "ws"
            "grpc", "gun" -> "grpc"
            "httpupgrade" -> "httpupgrade"
            else -> throw LinkParseException("Транспорт «$t» не поддерживается (есть tcp, xhttp, ws, grpc и httpupgrade)")
        }

        val headerType = q["headerType"]
        if (network == "raw" && !(headerType == null || headerType == "" || headerType == "none"))
            throw LinkParseException("Маскировка tcp headerType=$headerType не поддерживается")

        var security = (q["security"].nullIfEmpty() ?: defaultSecurity).lowercase()
        if (security == "" || security == "false" || security == "0") security = "none"
        if (security !in listOf("reality", "tls", "none"))
            throw LinkParseException("Защита «$security» не поддерживается (есть reality, tls и none)")

        val publicKey = q["pbk"]
        val sni = q["sni"].nullIfEmpty() ?: q["peer"].nullIfEmpty()
        if (security == "reality" && publicKey.isNullOrEmpty())
            throw LinkParseException("В ссылке REALITY нет открытого ключа сервера (pbk)")
        if (security == "reality" && sni == null)
            throw LinkParseException("В ссылке REALITY нет имени сайта для маскировки (sni)")

        val extra = q["extra"]
        if (!extra.isNullOrEmpty()) {
            try {
                Json.parseToJsonElement(extra)
            } catch (e: IllegalArgumentException) {
                throw LinkParseException("Параметр extra в ссылке — не JSON")
            }
        }

        // xray 26 не принимает allowInsecure: без проверки сертификата — только с его отпечатком (pcs).
        val pin = q["pcs"].nullIfEmpty() ?: q["pinSHA256"].nullIfEmpty()
        if (pin != null && !pin.split(',').all { isSha256Hex(it.trim()) })
            throw LinkParseException("pcs в ссылке — не SHA-256 в hex")
        if (security == "tls" && pin == null && q["allowInsecure"] in listOf("1", "true", "True"))
            throw LinkParseException("Ссылка с allowInsecure=1 без отпечатка сертификата (pcs): xray 26 не подключается без проверки сертификата")

        val path = q["path"].nullIfEmpty()
        val host = q["host"].nullIfEmpty()
        val mode = q["mode"].nullIfEmpty()
        return StreamFields(
            network = network,
            security = security,
            sni = sni,
            fingerprint = q["fp"].nullIfEmpty(),
            alpn = q["alpn"].nullIfEmpty(),
            pinSha256 = if (security == "tls") pin else null,
            verifyName = if (security == "tls") q["vcn"].nullIfEmpty() else null,
            publicKey = publicKey.nullIfEmpty(),
            shortId = q["sid"].nullIfEmpty(),
            spiderX = q["spx"].nullIfEmpty(),
            xhttpPath = if (network == "xhttp") path ?: "/" else null,
            xhttpHost = if (network == "xhttp") host else null,
            xhttpMode = if (network == "xhttp") mode ?: "auto" else null,
            xhttpExtra = if (network == "xhttp") extra.nullIfEmpty() else null,
            path = if (network == "ws" || network == "httpupgrade") path ?: "/" else null,
            hostHeader = if (network == "ws" || network == "httpupgrade") host else null,
            serviceName = if (network == "grpc") q["serviceName"].nullIfEmpty() ?: path ?: "" else null,
            grpcMulti = network == "grpc" && mode == "multi",
        )
    }

    private fun parseHysteria2(r: RawLink, link: String): Hysteria2Profile {
        if (r.userInfo.isNullOrEmpty()) throw LinkParseException("В ссылке hy2 нет пароля (часть до @)")
        val q = r.query
        // Диапазон портов: в адресе (официальный формат) или в параметре mport.
        var hopPorts = r.portSpec
        val mport = q["mport"].nullIfEmpty()
        if (mport != null) {
            val fromParam = normalizePorts(mport)
            if (fromParam != null) hopPorts = fromParam
            else if (mport.trim().toIntOrNull()?.let { it in 1..65535 } != true)
                throw LinkParseException("Неверный диапазон портов mport=«$mport»")
        }

        val obfs = q["obfs"]?.lowercase()
        var obfsPassword: String? = null
        if (obfs == "salamander")
            obfsPassword = q["obfs-password"].nullIfEmpty() ?: throw LinkParseException("Обфускация salamander без пароля (obfs-password)")
        else if (!(obfs == null || obfs == "" || obfs == "none"))
            throw LinkParseException("Обфускация hy2 «$obfs» не поддерживается (есть salamander)")

        val pin = q["pinSHA256"].nullIfEmpty()
        if (pin != null && !isSha256Hex(pin)) throw LinkParseException("pinSHA256 в ссылке hy2 — не SHA-256 в hex")

        val insecure = q["insecure"] == "1" || q["insecure"] == "true"
        if (insecure && pin == null)
            throw LinkParseException("Ссылка hy2 с insecure=1 без pinSHA256: xray не подключается без проверки сертификата")

        return Hysteria2Profile(
            name = nameOrDefault(r), host = r.host, port = r.port, sourceLink = link,
            auth = r.userInfo,
            sni = q["sni"].nullIfEmpty() ?: q["peer"].nullIfEmpty(),
            pinSha256 = pin,
            insecure = insecure,
            alpn = q["alpn"].nullIfEmpty(),
            hopPorts = hopPorts,
            obfsPassword = obfsPassword,
        )
    }

    /** portSpec — несколько портов в адресе (только у hy2), например «443,20000-30000»; иначе null. */
    private class RawLink(
        val scheme: String, val userInfo: String?, val host: String, val port: Int, val portSpec: String?,
        val query: CaseInsensitiveMap, val fragment: String,
    )

    private fun split(link: String): RawLink {
        val schemeEnd = link.indexOf("://")
        if (schemeEnd <= 0) throw LinkParseException("Это не ссылка: нет «://»")
        val scheme = link.substring(0, schemeEnd).lowercase()
        var rest = link.substring(schemeEnd + 3)

        var fragment = ""
        val hash = rest.indexOf('#')
        if (hash >= 0) {
            fragment = Text.unescape(rest.substring(hash + 1))
            rest = rest.substring(0, hash)
        }

        var queryText = ""
        val question = rest.indexOf('?')
        if (question >= 0) {
            queryText = rest.substring(question + 1)
            rest = rest.substring(0, question)
        }

        var userInfo: String? = null
        val at = rest.lastIndexOf('@')
        if (at >= 0) {
            userInfo = Text.unescape(rest.substring(0, at))
            rest = rest.substring(at + 1)
        }

        val slash = rest.indexOf('/')
        if (slash >= 0) rest = rest.substring(0, slash)

        val host: String
        val portText: String
        if (rest.startsWith('[')) {
            val close = rest.indexOf(']')
            if (close < 0) throw LinkParseException("Неверный IPv6-адрес сервера")
            host = rest.substring(1, close)
            portText = rest.substring(close + 1).trimStart(':')
        } else {
            val colon = rest.lastIndexOf(':')
            if (colon < 0) throw LinkParseException("В ссылке нет порта сервера")
            host = rest.substring(0, colon)
            portText = rest.substring(colon + 1)
        }

        if (host.isBlank()) throw LinkParseException("В ссылке нет адреса сервера")
        var portSpec: String? = null
        val hy2 = scheme == "hy2" || scheme == "hysteria2"
        var port = portText.trim().toIntOrNull()
        if (port == null || port !in 1..65535) {
            // Hysteria2 умеет менять порты: «host:443,20000-30000». Основной порт — первый.
            portSpec = if (hy2) normalizePorts(portText) else null
            if (portSpec == null)
                throw LinkParseException(
                    if (hy2) "Неверный порт «$portText» (нужен номер или диапазон, например 20000-30000)"
                    else "Неверный порт «$portText» (несколько портов бывают только у hy2)"
                )
            port = portSpec.split(',', '-')[0].toInt()
        }

        val query = CaseInsensitiveMap()
        for (pair in queryText.split('&').filter { it.isNotEmpty() }) {
            val eq = pair.indexOf('=')
            val key = Text.unescape(if (eq >= 0) pair.substring(0, eq) else pair)
            val value = if (eq >= 0) Text.unescape(pair.substring(eq + 1)) else ""
            query[key] = value
        }
        return RawLink(scheme, userInfo, host, port, portSpec, query, fragment)
    }

    /**
     * Список портов «443,20000-30000» (до 16 частей, порты 1–65535; «:» как разделитель диапазона тоже допустим)
     * в виде для xray; null — неверный формат или это один порт.
     */
    fun normalizePorts(text: String): String? {
        val parts = text.replace(" ", "").split(',').filter { it.isNotEmpty() }
        if (parts.isEmpty() || parts.size > 16) return null
        val result = mutableListOf<String>()
        var total = 0
        for (part in parts) {
            val bounds = part.split('-', ':')
            val from = bounds[0].toIntOrNull()
            if (bounds.size > 2 || from == null || from !in 1..65535) return null
            var to = from
            if (bounds.size == 2) {
                to = bounds[1].toIntOrNull() ?: return null
                if (to < from || to > 65535) return null
            }
            total += to - from + 1
            result.add(if (from == to) "$from" else "$from-$to")
        }
        return if (total > 1) result.joinToString(",") else null
    }

    private fun nameOrDefault(r: RawLink) = if (r.fragment.isBlank()) "${r.host}:${r.port}" else r.fragment.trim()

    fun isSha256Hex(value: String): Boolean {
        val hex = value.replace(":", "")
        return hex.length == 64 && hex.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }
    }
}

internal fun String?.nullIfEmpty(): String? = if (this.isNullOrEmpty()) null else this

/** Словарь параметров ссылки без учёта регистра ключей (как StringComparer.OrdinalIgnoreCase). */
internal class CaseInsensitiveMap(from: Map<String, String> = emptyMap()) : AbstractMutableMap<String, String>() {
    private val map = LinkedHashMap<String, Pair<String, String>>()

    init {
        from.forEach { (k, v) -> put(k, v) }
    }

    override val entries: MutableSet<MutableMap.MutableEntry<String, String>>
        get() = map.values.associate { it.first to it.second }.toMutableMap().entries

    override fun put(key: String, value: String): String? = map.put(key.lowercase(), key to value)?.second
    override fun get(key: String): String? = map[key.lowercase()]?.second
    override fun remove(key: String): String? = map.remove(key.lowercase())?.second
    override fun containsKey(key: String) = map.containsKey(key.lowercase())
}
