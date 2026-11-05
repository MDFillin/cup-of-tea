package cupoftea.core.profiles

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import java.util.UUID

/** Транспорт до сервера: от него зависит пинг. */
enum class ServerTransport { Tcp, Udp }

/**
 * Сервер из ссылки или подписки. Хранится зашифрованным (ProfileStore в :app).
 * Порт src/PVpn.Core/Profiles/Profile.cs; поля транспорта вынесены в [StreamFields].
 */
@Serializable
sealed class Profile {
    abstract val id: String
    abstract val name: String
    abstract val host: String
    abstract val port: Int

    /** Исходная ссылка: по ней профиль можно переимпортировать или экспортировать. */
    abstract val sourceLink: String

    /** Из какой подписки сервер; null — добавлен вручную. */
    abstract val subscriptionId: String?

    /** Короткое описание протокола для интерфейса, например «VLESS XHTTP REALITY». */
    abstract val protocolLabel: String

    abstract val transport: ServerTransport

    /** Строки, которые нельзя показывать в журналах. */
    abstract val secrets: List<String>

    abstract fun withIdentity(id: String = this.id, name: String = this.name, subscriptionId: String? = this.subscriptionId): Profile

    companion object {
        fun newId(): String = UUID.randomUUID().toString()
    }
}

/** Поля транспорта и защиты (общая часть VLESS, Trojan и VMess). */
@Serializable
data class StreamFields(
    /** raw (он же tcp), xhttp, ws, grpc или httpupgrade. */
    val network: String = "raw",
    /** reality, tls или none. */
    val security: String = "none",
    val sni: String? = null,
    val fingerprint: String? = null,
    val alpn: String? = null,
    /** SHA-256 сертификата сервера (pcs в ссылке): вместо проверки цепочки — сверка с ним. */
    val pinSha256: String? = null,
    /** Имя, по которому проверять сертификат, если оно не совпадает с SNI (vcn в ссылке). */
    val verifyName: String? = null,
    // REALITY
    val publicKey: String? = null,
    val shortId: String? = null,
    val spiderX: String? = null,
    // XHTTP
    val xhttpPath: String? = null,
    val xhttpHost: String? = null,
    val xhttpMode: String? = null,
    /** Параметр extra из ссылки (JSON с дополнительными настройками XHTTP). */
    val xhttpExtra: String? = null,
    // WebSocket и HTTPUpgrade
    val path: String? = null,
    val hostHeader: String? = null,
    // gRPC
    val serviceName: String? = null,
    /** gRPC в режиме multi (mode=multi в ссылке). */
    val grpcMulti: Boolean = false,
) {
    /** «XHTTP REALITY», «WS TLS», «gRPC TLS» — вторая половина подписи протокола. */
    fun label(overrideTransport: String? = null): String {
        val t = overrideTransport ?: when (network) {
            "xhttp" -> "XHTTP"
            "ws" -> "WS"
            "grpc" -> "gRPC"
            "httpupgrade" -> "HTTPUpgrade"
            else -> "TCP"
        }
        val s = when (security) {
            "reality" -> "REALITY"
            "tls" -> "TLS"
            else -> ""
        }
        return "$t $s".trimEnd()
    }
}

/** Сервер поверх TCP-транспорта xray (VLESS, Trojan, VMess). */
@Serializable
sealed class StreamProfile : Profile() {
    abstract val stream: StreamFields
    override val transport get() = ServerTransport.Tcp
}

@Serializable
@SerialName("vless")
data class VlessProfile(
    override val id: String = newId(),
    override val name: String,
    override val host: String,
    override val port: Int,
    override val sourceLink: String,
    override val subscriptionId: String? = null,
    val uuid: String,
    val encryption: String = "none",
    val flow: String? = null,
    override val stream: StreamFields = StreamFields(),
) : StreamProfile() {
    override val protocolLabel
        get() = "VLESS " + stream.label(if (stream.network == "raw" && flow == "xtls-rprx-vision") "Vision" else null)
    override val secrets get() = listOf(uuid)
    override fun withIdentity(id: String, name: String, subscriptionId: String?) =
        copy(id = id, name = name, subscriptionId = subscriptionId)
}

@Serializable
@SerialName("trojan")
data class TrojanProfile(
    override val id: String = newId(),
    override val name: String,
    override val host: String,
    override val port: Int,
    override val sourceLink: String,
    override val subscriptionId: String? = null,
    val password: String,
    override val stream: StreamFields = StreamFields(),
) : StreamProfile() {
    override val protocolLabel get() = "Trojan " + stream.label()
    override val secrets get() = listOf(password)
    override fun withIdentity(id: String, name: String, subscriptionId: String?) =
        copy(id = id, name = name, subscriptionId = subscriptionId)
}

@Serializable
@SerialName("vmess")
data class VmessProfile(
    override val id: String = newId(),
    override val name: String,
    override val host: String,
    override val port: Int,
    override val sourceLink: String,
    override val subscriptionId: String? = null,
    val uuid: String,
    /** Шифрование VMess (security в xray): auto, aes-128-gcm, chacha20-poly1305, none, zero. */
    val cipher: String = "auto",
    override val stream: StreamFields = StreamFields(),
) : StreamProfile() {
    override val protocolLabel get() = "VMess " + stream.label()
    override val secrets get() = listOf(uuid)
    override fun withIdentity(id: String, name: String, subscriptionId: String?) =
        copy(id = id, name = name, subscriptionId = subscriptionId)
}

@Serializable
@SerialName("shadowsocks")
data class ShadowsocksProfile(
    override val id: String = newId(),
    override val name: String,
    override val host: String,
    override val port: Int,
    override val sourceLink: String,
    override val subscriptionId: String? = null,
    /** Метод шифрования, например 2022-blake3-aes-128-gcm или chacha20-ietf-poly1305. */
    val method: String,
    val password: String,
) : Profile() {
    override val protocolLabel get() = "Shadowsocks"
    override val transport get() = ServerTransport.Tcp
    override val secrets get() = listOf(password)
    override fun withIdentity(id: String, name: String, subscriptionId: String?) =
        copy(id = id, name = name, subscriptionId = subscriptionId)
}

@Serializable
@SerialName("hysteria2")
data class Hysteria2Profile(
    override val id: String = newId(),
    override val name: String,
    override val host: String,
    override val port: Int,
    override val sourceLink: String,
    override val subscriptionId: String? = null,
    /** Строка авторизации: пароль или «логин:пароль». */
    val auth: String,
    val sni: String? = null,
    /** SHA-256 сертификата сервера (hex, двоеточия допустимы). */
    val pinSha256: String? = null,
    /** insecure=1 из ссылки. Сам по себе не используется: xray требует pin. */
    val insecure: Boolean = false,
    val alpn: String? = null,
    /** Порты для смены, например «20000-30000» или «443,20000-30000»; null — один порт. */
    val hopPorts: String? = null,
    /** Пароль обфускации salamander; null — без обфускации. */
    val obfsPassword: String? = null,
) : Profile() {
    override val protocolLabel
        get() = when {
            hopPorts == null && obfsPassword == null -> "Hysteria2"
            hopPorts == null -> "Hysteria2 (salamander)"
            obfsPassword == null -> "Hysteria2 (смена портов)"
            else -> "Hysteria2 (salamander, смена портов)"
        }
    override val transport get() = ServerTransport.Udp
    override val secrets get() = listOfNotNull(auth, obfsPassword)
    override fun withIdentity(id: String, name: String, subscriptionId: String?) =
        copy(id = id, name = name, subscriptionId = subscriptionId)
}

/**
 * Сервер из подписки в виде полного конфига xray: со своими маршрутами, DNS, фрагментацией, автовыбором и цепочками.
 * Хранится уже очищенным (ServerConfigs.sanitize). sourceLink пустой: ссылки у такого сервера нет.
 */
@Serializable
@SerialName("xray-config")
data class XrayConfigProfile(
    override val id: String = newId(),
    override val name: String,
    override val host: String,
    override val port: Int,
    override val sourceLink: String = "",
    override val subscriptionId: String? = null,
    /** Конфиг xray (JSON): outbounds, routing, dns, observatory; входы и журнал подставляет клиент. */
    val config: String,
    /** Протокол основного сервера, например «VLESS XHTTP REALITY». */
    val mainProtocol: String,
    /** Сколько серверов в автовыборе или цепочке (1 — один сервер). */
    val serverCount: Int = 1,
    /** Основной сервер работает по UDP (Hysteria2). */
    val udp: Boolean = false,
    /** Описание от панели (meta.serverDescription). */
    val description: String? = null,
) : Profile() {
    override val protocolLabel
        get() = if (serverCount > 1) "$mainProtocol · автовыбор из $serverCount" else mainProtocol
    override val transport get() = if (udp) ServerTransport.Udp else ServerTransport.Tcp
    override val secrets get() = cupoftea.core.config.ServerConfigs.secretsOf(config)
    override fun withIdentity(id: String, name: String, subscriptionId: String?) =
        copy(id = id, name = name, subscriptionId = subscriptionId)
}
