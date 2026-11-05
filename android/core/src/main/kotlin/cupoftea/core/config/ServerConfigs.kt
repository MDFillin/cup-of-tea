package cupoftea.core.config

import cupoftea.core.JArr
import cupoftea.core.JObj
import cupoftea.core.Js
import cupoftea.core.arr
import cupoftea.core.arrOrPut
import cupoftea.core.obj
import cupoftea.core.objects
import cupoftea.core.profiles.ImportError
import cupoftea.core.profiles.Profile
import cupoftea.core.profiles.XrayConfigProfile
import cupoftea.core.str

/** Что нашлось в подписке с полными конфигами: серверы, служебные строки панели и ошибки. */
data class ServerConfigImport(val profiles: List<Profile>, val notes: List<String>, val errors: List<ImportError>)

/**
 * Серверы в виде полного конфига xray — так некоторые панели отдают подписку: свои маршруты, DNS, фрагментация
 * (finalmask), автовыбор (балансировщики) и цепочки. От себя клиент добавляет только вход (tun или SOCKS) и
 * статистику. Порт src/PVpn.Core/Config/ServerConfigs.cs.
 */
object ServerConfigs {
    const val MAX_CONFIG_BYTES = 256 * 1024
    const val MAX_OUTBOUNDS = 64

    /** Теги входов десктопа: правила подписок ссылаются на них («socks, http, порт 53 → DNS»). */
    private val desktopInbounds = setOf("socks", "http")

    // Что берётся из конфига. Всё остальное (inbounds, log с путями к файлам, api, stats, reverse…) отбрасывается.
    private val topLevelKeys = setOf("outbounds", "routing", "dns", "observatory", "burstObservatory", "remarks", "meta")
    private val runtimeDropped = setOf("remarks", "meta")
    private val outboundKeys = setOf("tag", "protocol", "settings", "streamSettings", "mux", "proxySettings")
    private val routingKeys = setOf("domainStrategy", "rules", "balancers")
    private val protocols = setOf(
        "vless", "vmess", "trojan", "shadowsocks", "hysteria", "wireguard", "socks", "http", "freedom", "blackhole", "dns", "loopback",
    )

    /** Выходы, которые сами в сеть не ходят. */
    private val local = setOf("blackhole", "dns", "loopback")

    private val secretKeys = setOf("id", "password", "pass", "auth", "shortid", "pinnedpeercertsha256", "privatekey", "secretkey", "presharedkey", "psk")

    /** Ответ панели похож на JSON с конфигами, а не на список ссылок. */
    fun looksLikeConfigs(body: String): Boolean {
        val t = body.trimStart()
        return t.startsWith('[') || t.startsWith('{')
    }

    /** Разбирает ответ панели: массив конфигов (или один конфиг). */
    fun parseMany(json: String): ServerConfigImport {
        val root = try {
            Js.parse(json)
        } catch (e: IllegalArgumentException) {
            throw ConfigException("Подписка пришла в виде JSON, но разобрать его не удалось: ${e.message}")
        }
        val items: List<Any?> = when (root) {
            is List<*> -> root
            is Map<*, *> -> listOf(root)
            else -> emptyList()
        }
        val profiles = mutableListOf<Profile>()
        val notes = mutableListOf<String>()
        val errors = mutableListOf<ImportError>()
        for ((i, item) in items.withIndex()) {
            val config = item as? JObj ?: continue
            try {
                val (profile, info) = fromConfig(config)
                if (info) notes.add(profile.name) else profiles.add(profile)
            } catch (e: ConfigException) {
                errors.add(ImportError(i + 1, "«${remarks(config) ?: "конфиг ${i + 1}"}»: ${e.message}"))
            }
        }
        return ServerConfigImport(profiles, notes, errors)
    }

    /** Сервер из одного конфига. info — служебная строка панели (трафик, срок), а не сервер. */
    fun fromConfig(raw: JObj): Pair<XrayConfigProfile, Boolean> {
        val config = sanitize(raw)
        val outbounds = outbounds(config)
        val network = outbounds.filter(::isNetwork)
        if (network.isEmpty()) throw ConfigException("в конфиге нет ни одного сервера")

        val main = mainOutbound(config, outbounds) ?: network[0]
        val (host, port) = address(main)
        // Панели показывают трафик и срок «серверами» с адресом 127.0.0.1 — подключаться к ним незачем.
        val info = network.all { isPlaceholder(address(it).first) }
        val profile = XrayConfigProfile(
            name = remarks(raw) ?: "$host:$port",
            host = host,
            port = port,
            config = Js.stringify(config),
            mainProtocol = label(main),
            serverCount = network.size,
            udp = main.str("protocol") in listOf("hysteria", "wireguard"),
            description = raw.obj("meta")?.str("serverDescription"),
        )
        return profile to info
    }

    /** Разбор сохранённого конфига перед запуском — ничего не принимая на веру. */
    fun parseRuntime(json: String): XrayConfigProfile {
        if (json.toByteArray(Charsets.UTF_8).size > MAX_CONFIG_BYTES) throw ConfigException("Конфиг сервера слишком большой")
        val raw = try {
            Js.parse(json) as? JObj ?: throw ConfigException("Конфиг сервера — не объект JSON")
        } catch (e: IllegalArgumentException) {
            throw ConfigException("Конфиг сервера — не JSON: ${e.message}")
        }
        return fromConfig(raw).first
    }

    /**
     * Оставляет из конфига только серверы, маршруты, DNS и наблюдение за серверами; проверяет ссылки на файлы
     * (ext:, file:, сертификаты) и сокеты вместо адресов.
     */
    fun sanitize(raw: JObj): JObj {
        if (Js.stringify(raw).toByteArray(Charsets.UTF_8).size > MAX_CONFIG_BYTES) throw ConfigException("конфиг больше 256 КБ")

        val result = JObj()
        for ((key, value) in raw) if (key in topLevelKeys && value != null) result[key] = Js.clone(value)

        val outbounds = result.arr("outbounds")
        if (outbounds == null || outbounds.isEmpty()) throw ConfigException("в конфиге нет outbounds")
        if (outbounds.size > MAX_OUTBOUNDS) throw ConfigException("в конфиге больше $MAX_OUTBOUNDS выходов")
        val clean = JArr()
        for (node in outbounds) {
            val o = node as? JObj
            val protocol = o?.str("protocol")
            if (o == null || protocol == null || protocol !in protocols)
                throw ConfigException("неподдерживаемый выход: ${o?.get("protocol")?.let { Js.stringify(it) } ?: "?"}")
            val copy = JObj()
            for ((key, value) in o) if (key in outboundKeys && value != null) copy[key] = value
            copy.obj("streamSettings")?.let { stream ->
                if (stream.str("network") in listOf("domainsocket", "ds", "unix"))
                    throw ConfigException("локальные сокеты вместо сервера не поддерживаются")
                // Привязка к адаптеру и метки пакетов — дело клиента, а сертификаты с диска читать незачем.
                stream.obj("sockopt")?.let {
                    it.remove("interface")
                    it.remove("mark")
                    it.remove("tproxy")
                }
                stream.obj("tlsSettings")?.remove("certificates")
            }
            for (address in addressesOf(copy)) {
                if (address.contains('/') || address.contains('\\') || address.startsWith('@'))
                    throw ConfigException("адрес сервера «$address» похож на путь к файлу")
            }
            clean.add(copy)
        }
        result["outbounds"] = clean

        val routing = result.obj("routing")
        if (routing != null) {
            val copy = JObj()
            for ((key, value) in routing) if (key in routingKeys && value != null) copy[key] = value
            result["routing"] = copy
        } else result.remove("routing")

        if (result.containsKey("dns") && result["dns"] !is Map<*, *>) result.remove("dns")

        // Наборы правил из файлов (ext:файл.dat:тег) и любые file:-пути — только встроенные geoip/geosite.
        findString(result) { it.startsWith("ext:", ignoreCase = true) || it.startsWith("file:", ignoreCase = true) }
            ?.let { throw ConfigException("ссылки на файлы в конфиге не поддерживаются ($it)") }
        return result
    }

    /** Строки конфига, которые нельзя показывать в журналах: UUID, пароли, pin сертификата. */
    fun secretsOf(config: String): List<String> {
        val found = LinkedHashSet<String>()
        fun walk(node: Any?, name: String?) {
            when (node) {
                is Map<*, *> -> node.forEach { (k, v) -> walk(v, k as String) }
                is List<*> -> node.forEach { walk(it, name) }
                is String -> if (name != null && name.lowercase() in secretKeys && node.length >= 4) found.add(node)
            }
        }
        try {
            walk(Js.parse(config), null)
        } catch (e: IllegalArgumentException) {
        }
        return found.toList()
    }

    /** Адреса всех серверов конфига (домены и IP). */
    fun serverHosts(profile: XrayConfigProfile): List<String> {
        val config = Js.parseObj(profile.config) ?: JObj()
        return outbounds(config).filter(::isNetwork).flatMap(::addressesOf).filterNot(::isPlaceholder)
            .distinctBy { it.lowercase() }
    }

    /** Есть ли у сервера свой DNS (тогда запросы DNS отдаются ему, а не DoH приложения). */
    fun hasDns(profile: XrayConfigProfile): Boolean = hasDns(Js.parseObj(profile.config))

    private fun hasDns(config: JObj?) = config?.obj("dns")?.isNotEmpty() == true

    /**
     * Конфиг для запуска: очищенный конфиг сервера + вход клиента и статистика.
     * options.bypass не применяется: у сервера свои настройки обхода.
     */
    fun buildRuntime(profile: XrayConfigProfile, options: XrayOptions): JObj {
        val config = sanitize(Js.parseObj(profile.config) ?: throw ConfigException("Конфиг сервера повреждён"))
        // Автовыбор, собранный клиентом из серверов по ссылкам: маршрутов сервера в нём нет, действует свой профиль.
        val auto = AutoSelect.isClientAuto(config)
        val serverDns = hasDns(config)
        runtimeDropped.forEach { config.remove(it) }
        config["log"] = XrayConfigBuilder.log(options)

        val routing = config.obj("routing") ?: JObj().also { config["routing"] = it }
        val rules = routing.arrOrPut("rules")
        if (!options.useServerRouting && !auto) stripServerRouting(rules)
        if (auto) options.routing?.let {
            cupoftea.core.routing.RoutingProfiles.apply(config, it, XrayConfigBuilder.PROXY_TAG, rules.size - 1, AutoSelect.BALANCER_TAG)
        }
        // Правила подписки про входы десктопа («socks», «http») относятся и к нашему входу.
        val ownInbound = if (options.mode == XrayMode.Tun) XrayConfigBuilder.TUN_TAG else XrayConfigBuilder.SOCKS_TAG
        for (rule in rules.objects()) {
            val tags = rule.arr("inboundTag") ?: continue
            if (tags.any { it in desktopInbounds } && ownInbound !in tags) tags.add(ownInbound)
        }
        XrayConfigBuilder.addInbound(config, options, hasServerDns = serverDns)
        return config
    }

    /**
     * Выходы для пинга (BatchPing): основной сервер конфига под тегом [tag] и, если он подключается через другой
     * выход (dialerProxy, цепочка), — тот тоже, под своим тегом.
     */
    fun pingOutbounds(profile: XrayConfigProfile, tag: String): List<JObj> {
        val config = sanitize(Js.parseObj(profile.config) ?: throw ConfigException("Конфиг сервера повреждён"))
        val outbounds = outbounds(config)
        val main = Js.clone(mainOutbound(config, outbounds) ?: outbounds.firstOrNull(::isNetwork) ?: throw ConfigException("в конфиге нет сервера"))
        main["tag"] = tag
        val result = mutableListOf(main)
        val sockopt = main.obj("streamSettings")?.obj("sockopt")
        val dialer = sockopt?.str("dialerProxy") ?: main.obj("proxySettings")?.str("tag")
        val via = dialer?.let { d -> outbounds.firstOrNull { it.str("tag") == d } }
        if (via != null) {
            val copy = Js.clone(via)
            copy["tag"] = "$tag-via"
            copy.obj("streamSettings")?.obj("sockopt")?.remove("dialerProxy")
            if (sockopt?.get("dialerProxy") != null) sockopt["dialerProxy"] = "$tag-via"
            else main.obj("proxySettings")!!["tag"] = "$tag-via"
            result.add(copy)
        } else if (dialer != null) {
            sockopt?.remove("dialerProxy")
            main.remove("proxySettings")
        }
        return result
    }

    /**
     * «Маршруты подписки выключены»: всё через сервер, кроме локальных сетей. Остаются служебные правила
     * (DNS, цепочки по inboundTag, последнее «всё остальное»).
     */
    fun stripServerRouting(rules: JArr) {
        for (i in rules.indices.reversed()) {
            val rule = rules[i] as? JObj ?: continue
            if (isStructural(rule)) continue
            val ip = rule.arr("ip")
            if (ip != null && ip.all { it == "geoip:private" }) continue
            rules.removeAt(i)
        }
    }

    private fun isStructural(rule: JObj) = rule["inboundTag"] != null || rule.str("port") == "53" || isCatchAll(rule)

    /** Правило «всё остальное»: без условий, кроме вида сети. */
    private fun isCatchAll(rule: JObj) = rule.keys.all { it in setOf("type", "network", "outboundTag", "balancerTag", "ruleTag") }

    private fun outbounds(config: JObj): List<JObj> = config.arr("outbounds")?.objects() ?: emptyList()

    /** Сервер (а не blackhole, dns, loopback или прямой выход). */
    private fun isNetwork(o: JObj): Boolean {
        val p = o.str("protocol") ?: return false
        return p !in local && p != "freedom"
    }

    /** Основной сервер — куда ведёт последнее правило «всё остальное»; без правил xray шлёт всё в первый выход. */
    private fun mainOutbound(config: JObj, outbounds: List<JObj>): JObj? {
        val routing = config.obj("routing")
        val final = routing?.arr("rules")?.objects()?.lastOrNull(::isCatchAll)
        final?.str("outboundTag")?.let { tag -> return outbounds.firstOrNull { it.str("tag") == tag && isNetwork(it) } }
        final?.str("balancerTag")?.let { balancerTag ->
            val balancer = routing.arr("balancers")?.objects()?.firstOrNull { it.str("tag") == balancerTag }
            if (balancer != null) {
                val selectors = balancer.arr("selector")?.filterIsInstance<String>() ?: emptyList()
                return outbounds.firstOrNull { o -> isNetwork(o) && o.str("tag")?.let { t -> selectors.any { t.startsWith(it) } } == true }
            }
        }
        val first = outbounds.firstOrNull()
        return if (first != null && isNetwork(first)) first else null
    }

    /** Адрес и порт сервера: vnext/servers (старая запись) или address/port прямо в settings. */
    private fun address(outbound: JObj): Pair<String, Int> {
        val settings = outbound.obj("settings")
        val entry = settings?.arr("vnext")?.firstOrNull() as? JObj
            ?: settings?.arr("servers")?.firstOrNull() as? JObj
            ?: settings
        val host = entry?.str("address") ?: "?"
        val port = when (val p = entry?.get("port")) {
            is Int -> p
            else -> 0
        }
        return host to port
    }

    private fun addressesOf(outbound: JObj): List<String> {
        val settings = outbound.obj("settings") ?: return emptyList()
        val result = mutableListOf<String>()
        settings.str("address")?.let { result.add(it) }
        for (list in listOf(settings["vnext"], settings["servers"], settings["peers"]).filterIsInstance<JArr>()) {
            for (entry in list.objects()) {
                val e = (entry["address"] ?: entry["endpoint"]) as? String ?: continue
                result.add(if (e.contains(':') && !isIpLiteral(e)) e.substring(0, e.lastIndexOf(':')) else e)
            }
        }
        return result
    }

    private fun isPlaceholder(host: String) =
        host in setOf("127.0.0.1", "localhost", "0.0.0.0", "::1", "?") || host.startsWith("127.")

    /** «VLESS XHTTP REALITY», «Trojan gRPC TLS», «Hysteria2». */
    private fun label(outbound: JObj): String {
        val protocol = outbound.str("protocol") ?: "?"
        if (protocol == "hysteria") return "Hysteria2"
        val stream = outbound.obj("streamSettings")
        val network = stream?.str("network") ?: "tcp"
        val settings = outbound.obj("settings")
        val flow = ((settings?.arr("vnext")?.firstOrNull() as? JObj)?.arr("users")?.firstOrNull() as? JObj)?.str("flow")
            ?: settings?.str("flow")
        val transport = if (flow?.contains("vision") == true) "Vision" else when (network) {
            "xhttp", "splithttp" -> "XHTTP"
            "grpc" -> "gRPC"
            "ws" -> "WebSocket"
            "httpupgrade" -> "HTTPUpgrade"
            "raw", "tcp" -> "TCP"
            else -> network
        }
        val security = when (stream?.str("security")) {
            "reality" -> "REALITY"
            "tls" -> "TLS"
            else -> ""
        }
        val name = when (protocol) {
            "vless" -> "VLESS"
            "vmess" -> "VMess"
            "trojan" -> "Trojan"
            "shadowsocks" -> "Shadowsocks"
            else -> protocol
        }
        return "$name $transport $security".trimEnd()
    }

    private fun remarks(config: JObj): String? = config.str("remarks")?.takeIf { it.isNotBlank() }?.trim()

    private fun findString(node: Any?, match: (String) -> Boolean): String? = when (node) {
        is Map<*, *> -> node.values.firstNotNullOfOrNull { findString(it, match) }
        is List<*> -> node.firstNotNullOfOrNull { findString(it, match) }
        is String -> if (match(node)) node else null
        else -> null
    }
}
