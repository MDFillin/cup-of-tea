package cupoftea.core.config

import cupoftea.core.JArr
import cupoftea.core.JObj
import cupoftea.core.Js
import cupoftea.core.arrOrPut
import cupoftea.core.objOrPut
import cupoftea.core.objects
import cupoftea.core.str
import cupoftea.core.profiles.Hysteria2Profile
import cupoftea.core.profiles.Profile
import cupoftea.core.profiles.ShadowsocksProfile
import cupoftea.core.profiles.StreamProfile
import cupoftea.core.profiles.TrojanProfile
import cupoftea.core.profiles.VlessProfile
import cupoftea.core.profiles.VmessProfile
import cupoftea.core.profiles.XrayConfigProfile
import cupoftea.core.routing.RoutingProfile
import cupoftea.core.routing.RoutingProfiles

enum class XrayMode {
    /** Вход tun: дескриптор TUN приходит от VpnService, весь трафик устройства идёт в xray. */
    Tun,

    /** SOCKS без пароля на 127.0.0.1 — для проверки сервера (внешний IP, задержка). */
    Socks,
}

/**
 * Параметры сборки конфига. На Android нет привязки к адаптеру: сама программа исключена из VPN
 * (addDisallowedApplication), и её соединения идут мимо туннеля.
 */
data class XrayOptions(
    val mode: XrayMode = XrayMode.Tun,
    val socksPort: Int = 10808,
    /** Обход блокировок (уже проверенный BypassRules.validate) — для серверов по ссылкам. */
    val bypass: BypassOptions = BypassOptions.Default,
    val logLevel: String = "warning",
    /** Файл журнала ошибок xray; null — без журнала. */
    val logPath: String? = null,
    /** Счётчики трафика (stats + policy). */
    val stats: Boolean = false,
    /** Для серверов с полным конфигом из подписки: применять их маршруты. false — всё через сервер. */
    val useServerRouting: Boolean = true,
    /** Заранее разрешённые адреса (домен → IP): попадают в dns.hosts. */
    val resolvedHosts: Map<String, String>? = null,
    /** Профиль маршрутизации (уже проверенный); null — всё через VPN. */
    val routing: RoutingProfile? = null,
    /** DNS через туннель — если у сервера нет своего. */
    val dns: DnsOptions = DnsOptions(),
)

/** Сборка конфигов xray. Порт src/PVpn.Core/Config/XrayConfigBuilder.cs. */
object XrayConfigBuilder {
    const val PROXY_TAG = "proxy"
    const val FRAGMENT_TAG = "fragment"
    const val TUN_TAG = "tun"
    const val SOCKS_TAG = "socks"
    const val DNS_OUT_TAG = "dns-out"

    val privateCidrs = listOf(
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.168.0.0/16", "224.0.0.0/4", "255.255.255.255/32", "::1/128", "fc00::/7", "fe80::/10",
    )

    fun build(profile: Profile, options: XrayOptions): JObj {
        if (profile is XrayConfigProfile) return ServerConfigs.buildRuntime(profile, options)
        val bypass = options.bypass
        val fragment = bypass.fragment && profile is StreamProfile && profile.stream.security in listOf("tls", "reality")
        val outbounds = Js.arr(buildOutbound(profile, PROXY_TAG, fragment, bypass))
        if (fragment) outbounds.add(buildFragmentOutbound(bypass))
        outbounds.add(Js.obj("tag" to "direct", "protocol" to "freedom"))
        outbounds.add(Js.obj("tag" to "block", "protocol" to "blackhole"))
        val config = Js.obj(
            "log" to log(options),
            "inbounds" to Js.arr(),
            "outbounds" to outbounds,
        )
        val rules = Js.arr(
            Js.obj("type" to "field", "domain" to Js.arr("localhost"), "outboundTag" to "direct"),
            Js.obj("type" to "field", "ip" to Js.arrOf(privateCidrs), "outboundTag" to "direct"),
        )
        config["routing"] = Js.obj("domainStrategy" to "AsIs", "rules" to rules)
        options.routing?.let { RoutingProfiles.apply(config, it, PROXY_TAG, rules.size) }
        addInbound(config, options, hasServerDns = false)
        return config
    }

    internal fun log(options: XrayOptions): JObj {
        val log = Js.obj("loglevel" to options.logLevel, "access" to "none", "dnsLog" to false)
        if (options.logPath != null) log["error"] = options.logPath
        return log
    }

    internal fun sniffing() = Js.obj(
        "enabled" to true,
        "destOverride" to Js.arr("http", "tls", "quic"),
        "routeOnly" to true,
    )

    /**
     * Вход клиента и всё, что ему нужно. В TUN: вход tun, DNS (порт 53) — во внутренний DNS xray, а если у сервера
     * нет своего DNS — DNS-over-HTTPS выбранного провайдера через туннель. В SOCKS — только вход на 127.0.0.1.
     */
    internal fun addInbound(config: JObj, options: XrayOptions, hasServerDns: Boolean) {
        val inbounds = JArr()
        if (options.mode == XrayMode.Socks) {
            inbounds.add(
                Js.obj(
                    "tag" to SOCKS_TAG, "listen" to "127.0.0.1", "port" to options.socksPort, "protocol" to "socks",
                    "settings" to Js.obj("auth" to "noauth", "udp" to true, "ip" to "127.0.0.1"),
                    "sniffing" to sniffing(),
                )
            )
        } else {
            inbounds.add(
                Js.obj(
                    "tag" to TUN_TAG, "protocol" to "tun",
                    "settings" to Js.obj("name" to "tun0", "mtu" to TUN_MTU),
                    "sniffing" to sniffing(),
                )
            )
            // Выход dns бывает уже в конфиге подписки (часто с тем же тегом dns-out) — тогда ведём в него.
            val outbounds = config.arrOrPut("outbounds")
            val dnsTag = outbounds.objects().firstOrNull { it.str("protocol") == "dns" && it.str("tag") != null }?.str("tag")
                ?: uniqueTag(outbounds, DNS_OUT_TAG).also { outbounds.add(Js.obj("tag" to it, "protocol" to "dns")) }
            val rules = config.objOrPut("routing").arrOrPut("rules")
            rules.add(0, Js.obj("type" to "field", "inboundTag" to Js.arr(TUN_TAG), "port" to "53", "outboundTag" to dnsTag))
            if (!hasServerDns) addDoh(config, options.dns)
            // Туннель — только IPv4: и свой DNS сервера отдаёт программам только A-записи (если подписка не задала
            // иное), иначе запросы AAAA и попытки по IPv6 дают паузы при открытии сайтов (как 0.8.2 на ПК).
            else config.objOrPut("dns").putIfAbsent("queryStrategy", "UseIPv4")
        }
        config["inbounds"] = inbounds
        options.resolvedHosts?.takeIf { it.isNotEmpty() }?.let { addHosts(config, it) }
        if (options.stats) addStats(config)
    }

    /** Тег, которого ещё нет среди выходов: base, base-2, base-3… */
    private fun uniqueTag(outbounds: JArr, base: String): String {
        val tags = outbounds.objects().mapNotNull { it.str("tag") }.toSet()
        var tag = base
        var i = 2
        while (tag in tags) tag = "$base-${i++}"
        return tag
    }

    /** DNS-over-HTTPS через туннель: готовый провайдер — по имени, а адрес имени — в dns.hosts (без отдельного DNS). */
    private fun addDoh(config: JObj, options: DnsOptions) {
        val server = DnsRules.resolve(options)
        val dns = config.objOrPut("dns")
        val name = server.serverName ?: server.host
        dns["servers"] = Js.arr("https://$name${if (server.port == 443) "" else ":${server.port}"}${server.path}")
        if (server.serverName != null) dns.objOrPut("hosts")[server.serverName] = Js.arr(server.host)
        // Туннель — только IPv4: адреса IPv6 программам не отдаём, иначе они пытаются идти по IPv6 и ждут.
        dns["queryStrategy"] = "UseIPv4"
    }

    internal fun addHosts(config: JObj, resolved: Map<String, String>) {
        val hosts = config.objOrPut("dns").objOrPut("hosts")
        for ((host, ip) in resolved) {
            // Своё сопоставление из подписки главнее.
            if (!hosts.containsKey(host) && isIpLiteral(ip)) hosts[host] = Js.arr(ip)
        }
    }

    /** Счётчики трафика: входы (весь трафик устройства) и выходы. */
    internal fun addStats(config: JObj) {
        config["stats"] = JObj()
        config["policy"] = Js.obj(
            "system" to Js.obj(
                "statsOutboundUplink" to true, "statsOutboundDownlink" to true,
                "statsInboundUplink" to true, "statsInboundDownlink" to true,
            )
        )
    }

    /** MTU туннеля: столько же отдаёт VpnService. */
    const val TUN_MTU = 1500

    /** [viaFragment] — устанавливать соединение через выход «fragment». */
    fun buildOutbound(profile: Profile, tag: String, viaFragment: Boolean = false, bypass: BypassOptions = BypassOptions.Default): JObj =
        when (profile) {
            is StreamProfile -> buildStreamOutbound(profile, tag, viaFragment, bypass)
            is ShadowsocksProfile -> buildShadowsocks(profile, tag)
            is Hysteria2Profile -> buildHysteria2(profile, tag, bypass)
            else -> throw ConfigException("Неподдерживаемый профиль: ${profile::class.simpleName}")
        }

    /** freedom, через который основной выход открывает TCP: режет TLS ClientHello на куски. */
    fun buildFragmentOutbound(bypass: BypassOptions = BypassOptions.Default) = Js.obj(
        "tag" to FRAGMENT_TAG,
        "protocol" to "freedom",
        "settings" to Js.obj(
            "fragment" to Js.obj(
                "packets" to bypass.fragmentPackets,
                "length" to bypass.fragmentLength,
                "interval" to bypass.fragmentInterval,
            )
        ),
    )

    private fun buildStreamOutbound(p: StreamProfile, tag: String, viaFragment: Boolean, bypass: BypassOptions): JObj {
        val address = p.host
        val (protocol, settings) = when (p) {
            // У VLESS в xray 26 — плоская запись.
            is VlessProfile -> "vless" to Js.obj("address" to address, "port" to p.port, "id" to p.uuid, "encryption" to p.encryption)
                .also { if (!p.flow.isNullOrEmpty()) it["flow"] = p.flow }
            is TrojanProfile -> "trojan" to Js.obj("servers" to Js.arr(Js.obj("address" to address, "port" to p.port, "password" to p.password)))
            is VmessProfile -> "vmess" to Js.obj(
                "vnext" to Js.arr(
                    Js.obj("address" to address, "port" to p.port, "users" to Js.arr(Js.obj("id" to p.uuid, "security" to p.cipher)))
                )
            )
        }
        val stream = buildStream(p, bypass)
        if (viaFragment) stream.objOrPut("sockopt")["dialerProxy"] = FRAGMENT_TAG
        val outbound = Js.obj("tag" to tag, "protocol" to protocol, "settings" to settings, "streamSettings" to stream)
        // У XHTTP своё мультиплексирование (xmux), у gRPC — своё; mux.cool — только поверх raw.
        if (bypass.mux && p.stream.network == "raw") {
            val vision = p is VlessProfile && p.flow == "xtls-rprx-vision"
            outbound["mux"] = Js.obj(
                "enabled" to true,
                // Vision не пускает TCP через mux: через mux идёт только UDP.
                "concurrency" to if (vision) -1 else bypass.muxConcurrency,
                "xudpConcurrency" to bypass.xudpConcurrency,
                // QUIC (UDP 443) — отдельным соединением, без mux.
                "xudpProxyUDP443" to "skip",
            )
        }
        return outbound
    }

    private fun alpnList(alpn: String?) = (alpn ?: "").split(',').map { it.trim() }.filter { it.isNotEmpty() }

    private fun buildStream(p: StreamProfile, bypass: BypassOptions): JObj {
        val s = p.stream
        val fingerprint = bypass.fingerprint ?: s.fingerprint ?: "chrome"
        val stream = Js.obj("network" to s.network, "security" to s.security)
        when (s.security) {
            "reality" -> {
                val reality = Js.obj(
                    "serverName" to s.sni, "fingerprint" to fingerprint, "publicKey" to s.publicKey, "shortId" to (s.shortId ?: ""),
                )
                if (!s.spiderX.isNullOrEmpty()) reality["spiderX"] = s.spiderX
                stream["realitySettings"] = reality
            }
            "tls" -> {
                val tls = Js.obj("serverName" to (s.sni ?: s.hostHeader ?: s.xhttpHost ?: p.host), "fingerprint" to fingerprint)
                val alpn = alpnList(s.alpn)
                if (alpn.isNotEmpty()) tls["alpn"] = Js.arrOf(alpn)
                if (!s.pinSha256.isNullOrEmpty()) tls["pinnedPeerCertSha256"] = s.pinSha256
                if (!s.verifyName.isNullOrEmpty()) tls["verifyPeerCertByName"] = s.verifyName
                stream["tlsSettings"] = tls
            }
        }
        when (s.network) {
            "xhttp" -> {
                val xhttp = Js.obj("path" to (s.xhttpPath ?: "/"), "mode" to (bypass.xhttpMode ?: s.xhttpMode ?: "auto"))
                if (!s.xhttpHost.isNullOrEmpty()) xhttp["host"] = s.xhttpHost
                var extra = if (s.xhttpExtra.isNullOrEmpty()) null else Js.parseObj(s.xhttpExtra)
                bypass.xmux?.let {
                    // Если extra задан, xray берёт настройки XHTTP (кроме host, path и mode) только из него.
                    if (extra == null) extra = JObj()
                    extra!!["xmux"] = buildXmux(it)
                }
                if (extra != null) xhttp["extra"] = extra
                stream["xhttpSettings"] = xhttp
            }
            "ws", "httpupgrade" -> {
                val ws = Js.obj("path" to (s.path ?: "/"))
                if (!s.hostHeader.isNullOrEmpty()) ws["host"] = s.hostHeader
                stream[if (s.network == "ws") "wsSettings" else "httpupgradeSettings"] = ws
            }
            "grpc" -> {
                val grpc = Js.obj("serviceName" to (s.serviceName ?: ""))
                if (s.grpcMulti) grpc["multiMode"] = true
                if (!s.hostHeader.isNullOrEmpty()) grpc["authority"] = s.hostHeader
                stream["grpcSettings"] = grpc
            }
        }
        return stream
    }

    /** Shadowsocks (AEAD и 2022): без транспорта и TLS — шифрует сам протокол. */
    private fun buildShadowsocks(p: ShadowsocksProfile, tag: String) = Js.obj(
        "tag" to tag,
        "protocol" to "shadowsocks",
        "settings" to Js.obj(
            "servers" to Js.arr(Js.obj("address" to p.host, "port" to p.port, "method" to p.method, "password" to p.password))
        ),
    )

    private fun buildXmux(x: XmuxOptions): JObj {
        val xmux = JObj()
        x.maxConcurrency?.let { xmux["maxConcurrency"] = it }
        x.maxConnections?.let { xmux["maxConnections"] = it }
        x.cMaxReuseTimes?.let { xmux["cMaxReuseTimes"] = it }
        x.hMaxRequestTimes?.let { xmux["hMaxRequestTimes"] = it }
        x.hMaxReusableSecs?.let { xmux["hMaxReusableSecs"] = it }
        if (x.hKeepAlivePeriod > 0) xmux["hKeepAlivePeriod"] = x.hKeepAlivePeriod
        return xmux
    }

    private fun buildHysteria2(p: Hysteria2Profile, tag: String, bypass: BypassOptions): JObj {
        val tls = Js.obj(
            "serverName" to (p.sni ?: p.host),
            "alpn" to Js.arrOf(if (p.alpn.isNullOrEmpty()) listOf("h3") else alpnList(p.alpn)),
        )
        if (!p.pinSha256.isNullOrEmpty()) tls["pinnedPeerCertSha256"] = p.pinSha256
        val stream = Js.obj(
            "network" to "hysteria",
            "hysteriaSettings" to Js.obj("version" to 2, "auth" to p.auth),
            "security" to "tls",
            "tlsSettings" to tls,
        )
        // Слои маскировки UDP: первый элемент — внутренний слой.
        val udpMasks = JArr()
        // Salamander шифрует сами пакеты QUIC — самый внутренний слой; шум и смена портов работают поверх него.
        if (p.obfsPassword != null)
            udpMasks.add(Js.obj("type" to "salamander", "settings" to Js.obj("password" to p.obfsPassword)))
        if (bypass.noises)
            udpMasks.add(Js.obj("type" to "noise", "settings" to Js.obj("noise" to Js.arr(buildNoise(bypass)))))
        if (p.hopPorts != null) {
            udpMasks.add(
                Js.obj(
                    "type" to "udphop",
                    "settings" to Js.obj(
                        // Как у официального клиента Hysteria: по таймеру новый локальный сокет и новый порт сервера.
                        "mode" to "intervallocal,intervalremote",
                        "interval" to bypass.hopIntervalSeconds.toString(),
                        "remotePorts" to p.hopPorts,
                    ),
                )
            )
        }
        if (udpMasks.isNotEmpty()) stream["finalmask"] = Js.obj("udp" to udpMasks)
        return Js.obj(
            "tag" to tag,
            "protocol" to "hysteria",
            "settings" to Js.obj("version" to 2, "address" to p.host, "port" to p.port),
            "streamSettings" to stream,
        )
    }

    private fun buildNoise(bypass: BypassOptions): JObj {
        val item = JObj()
        if (bypass.noiseType == "rand") item["rand"] = bypass.noisePacket
        else {
            item["type"] = bypass.noiseType
            item["packet"] = bypass.noisePacket
        }
        item["delay"] = bypass.noiseDelay
        return item
    }
}
