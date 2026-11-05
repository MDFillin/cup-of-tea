package cupoftea.core

import cupoftea.core.config.ConfigException
import cupoftea.core.config.ServerConfigs
import cupoftea.core.config.XrayConfigBuilder
import cupoftea.core.config.XrayMode
import cupoftea.core.config.XrayOptions
import cupoftea.core.profiles.Flags
import cupoftea.core.profiles.ServerTransport
import cupoftea.core.profiles.XrayConfigProfile
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource

/** Порт ServerConfigTests.cs: конфиги серверов, как их отдают панели. Все ключи — фиктивные. */
class ServerConfigTest {
    companion object {
        /** Как ServerConfigSample в CoreUpdater.cs: маршруты, DNS, автовыбор, фрагментация в finalmask. */
        val SAMPLE = """
        {
          "remarks": "self-config",
          "dns": { "queryStrategy": "UseIPv4", "servers": [
            { "address": "77.88.8.8", "domains": ["full:a.example.com"], "skipFallback": true, "tag": "dns-bootstrap" },
            { "address": "https://dns.google/dns-query", "tag": "dns-remote" } ] },
          "outbounds": [
            { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "a.example.com", "port": 443,
                "users": [ { "id": "${LinkParserTest.UUID}", "encryption": "none", "flow": "xtls-rprx-vision" } ] } ] },
              "streamSettings": { "network": "tcp", "security": "reality",
                "realitySettings": { "serverName": "www.example.com", "fingerprint": "chrome", "publicKey": "${LinkParserTest.PBK}", "shortId": "0123abcd" },
                "finalmask": { "tcp": [ { "type": "fragment", "settings": { "packets": "tlshello", "length": "10-20", "delay": "5-15", "maxSplit": "3-6" } } ] },
                "sockopt": { "domainStrategy": "UseIPv4" } } },
            { "tag": "proxy-2", "protocol": "hysteria", "settings": { "version": 2, "address": "203.0.113.11", "port": 443 },
              "streamSettings": { "network": "hysteria", "hysteriaSettings": { "version": 2, "auth": "self-test" }, "security": "tls",
                "tlsSettings": { "serverName": "www.example.com", "alpn": ["h3"], "pinnedPeerCertSha256": "${LinkParserTest.PIN}" } } },
            { "tag": "direct", "protocol": "freedom", "settings": { "domainStrategy": "UseIPv4" } },
            { "tag": "block", "protocol": "blackhole" },
            { "tag": "dns-out", "protocol": "dns" } ],
          "burstObservatory": { "subjectSelector": ["proxy"], "pingConfig": { "destination": "http://www.gstatic.com/generate_204", "interval": "30s", "sampling": 2, "timeout": "3s" } },
          "routing": { "domainStrategy": "AsIs",
            "balancers": [ { "tag": "auto", "selector": ["proxy"], "strategy": { "type": "leastLoad" } } ],
            "rules": [
              { "type": "field", "inboundTag": ["dns-bootstrap"], "outboundTag": "direct" },
              { "type": "field", "inboundTag": ["socks", "http"], "port": "53", "outboundTag": "dns-out" },
              { "type": "field", "protocol": ["bittorrent"], "outboundTag": "block" },
              { "type": "field", "ip": ["geoip:private"], "outboundTag": "direct" },
              { "type": "field", "domain": ["geosite:category-ru", "regexp:\\.ru$"], "outboundTag": "direct" },
              { "type": "field", "ip": ["geoip:ru"], "outboundTag": "direct" },
              { "type": "field", "network": "tcp,udp", "balancerTag": "auto" } ] }
        }
        """.trimIndent()

        fun sample(): JObj = Js.parseObj(SAMPLE)!!
        fun server(config: JObj = sample()): XrayConfigProfile = ServerConfigs.fromConfig(config).first
        private val tun = XrayOptions(mode = XrayMode.Tun)
    }

    private fun rules(config: JObj) = config.arrAt("routing", "rules")

    @Test
    fun parsesPanelResponseWithServersAndNotes() {
        val info = sample()
        info["remarks"] = "Использовано 3.43/60.00 ГБ"
        for (o in info.arrAt("outbounds").objects()) {
            (o.at("settings", "vnext", 0) as? JObj)?.set("address", "127.0.0.1")
            if (o.at("settings", "address") != null) o.objAt("settings")["address"] = "127.0.0.1"
        }
        val broken = Js.obj("remarks" to "сломанный", "outbounds" to Js.arr(Js.obj("protocol" to "blackhole")))
        val result = ServerConfigs.parseMany(Js.stringify(Js.arr(sample(), info, broken)))

        val server = result.profiles.single() as XrayConfigProfile
        assertEquals("self-config", server.name)
        assertEquals("a.example.com" to 443, server.host to server.port)
        assertEquals("VLESS Vision REALITY", server.mainProtocol)
        assertEquals(2, server.serverCount)
        assertEquals("VLESS Vision REALITY · автовыбор из 2", server.protocolLabel)
        assertEquals(ServerTransport.Tcp, server.transport)
        assertEquals("", server.sourceLink)
        assertEquals(listOf("Использовано 3.43/60.00 ГБ"), result.notes)
        assertTrue(result.errors.single().message.contains("сломанный"))
    }

    @Test
    fun sanitizeKeepsOnlySafeParts() {
        val raw = sample()
        raw["inbounds"] = Js.arr(Js.obj("listen" to "0.0.0.0", "port" to 1080, "protocol" to "socks"))
        raw["log"] = Js.obj("access" to "/sdcard/evil.txt")
        raw["api"] = Js.obj("tag" to "api")
        val proxy = raw.objAt("outbounds", 0)
        proxy["sendThrough"] = "10.0.0.1"
        proxy.objAt("streamSettings", "sockopt")["interface"] = "wlan0"
        proxy.objAt("streamSettings")["tlsSettings"] = Js.obj("certificates" to Js.arr(Js.obj("certificateFile" to "/sdcard/key.pem")))

        val clean = ServerConfigs.sanitize(raw)
        assertNull(clean["inbounds"])
        assertNull(clean["log"])
        assertNull(clean["api"])
        val p = clean.objAt("outbounds", 0)
        assertNull(p["sendThrough"])
        assertNull(p.at("streamSettings", "sockopt", "interface"))
        assertNull(p.at("streamSettings", "tlsSettings", "certificates"))
        // Фрагментация, маршруты, DNS и автовыбор сервера остаются как есть.
        assertEquals("fragment", p.at("streamSettings", "finalmask", "tcp", 0, "type"))
        assertNotNull(clean["dns"])
        assertNotNull(clean["burstObservatory"])
        assertNotNull(clean.at("routing", "balancers"))
    }

    @ParameterizedTest
    @CsvSource("ext:custom.dat:ru", "file:C:/x.dat")
    fun rejectsFileReferences(domain: String) {
        val raw = sample()
        rules(raw).add(0, Js.obj("type" to "field", "domain" to Js.arr(domain), "outboundTag" to "direct"))
        assertTrue(assertThrows<ConfigException> { ServerConfigs.sanitize(raw) }.message!!.contains("файлы"))
    }

    @Test
    fun rejectsSocketAddressesAndUnknownOutbounds() {
        val socket = sample()
        socket.objAt("outbounds", 0, "settings", "vnext", 0)["address"] = "\\\\.\\pipe\\something"
        assertThrows<ConfigException> { ServerConfigs.sanitize(socket) }
        val unknown = sample()
        unknown.arrAt("outbounds").add(Js.obj("protocol" to "reverse-something"))
        assertThrows<ConfigException> { ServerConfigs.sanitize(unknown) }
    }

    @Test
    fun tunRuntimeUsesServersOwnDnsOutboundAndTunTag() {
        val c = XrayConfigBuilder.build(server(), tun)
        val inbound = c.arrAt("inbounds").single()
        assertEquals("tun", inbound.at("protocol"))
        assertEquals(true, inbound.at("sniffing", "routeOnly"))
        // Выход dns-out уже есть у сервера — второго с тем же тегом нет (иначе xray не запускается).
        val dnsOuts = c.arrAt("outbounds").filter { it.at("protocol") == "dns" }
        assertEquals(1, dnsOuts.size)
        assertEquals("dns-out", rules(c).at(0, "outboundTag"))
        // Правило сервера про входы десктопа (socks, http) относится и к нашему tun.
        assertTrue(rules(c).objects().any { it.arr("inboundTag")?.containsAll(listOf("socks", "tun")) == true })
        // У сервера свой DNS — DoH приложения не подставляется.
        assertEquals("77.88.8.8", c.at("dns", "servers", 0, "address"))
        // Настройки «Обход блокировок» к серверу из подписки не применяются.
        assertFalse(c.arrAt("outbounds").any { it.at("tag") == XrayConfigBuilder.FRAGMENT_TAG })
        assertNull(c["remarks"])
        assertEquals("none", c.at("log", "access"))
    }

    @Test
    fun ownDnsOutboundGetsFreeTagWhenNameIsTaken() {
        val raw = sample()
        raw.arrAt("outbounds").removeIf { it.at("protocol") == "dns" }
        raw.arrAt("outbounds").add(Js.obj("tag" to "dns-out", "protocol" to "freedom"))
        val c = XrayConfigBuilder.build(server(raw), tun)
        val tags = c.arrAt("outbounds").map { it.at("tag") }
        assertEquals(tags.size, tags.toSet().size)
        assertEquals("dns-out-2", rules(c).at(0, "outboundTag"))
    }

    @Test
    fun resolvedServerAddressesGoToDnsHosts() {
        val raw = sample()
        raw.objAt("dns")["hosts"] = Js.obj("b.example.com" to Js.arr("198.51.100.2"))
        val c = XrayConfigBuilder.build(
            server(raw), tun.copy(resolvedHosts = mapOf("a.example.com" to "198.51.100.1", "b.example.com" to "198.51.100.9")),
        )
        assertEquals("198.51.100.1", c.at("dns", "hosts", "a.example.com", 0))
        // Сопоставление из подписки не перезаписывается.
        assertEquals("198.51.100.2", c.at("dns", "hosts", "b.example.com", 0))
    }

    @Test
    fun serverRoutingCanBeTurnedOff() {
        val on = rules(XrayConfigBuilder.build(server(), tun))
        val off = rules(XrayConfigBuilder.build(server(), tun.copy(useServerRouting = false)))
        assertTrue(on.any { it.at("domain") != null })
        // Остаются служебные правила: DNS, inboundTag, локальные сети и последнее «всё остальное».
        assertFalse(off.any { it.at("domain") != null || it.at("protocol") != null })
        assertTrue(off.any { it.at("port") == "53" })
        assertTrue(off.any { it.at("ip", 0) == "geoip:private" })
        assertEquals("auto", off.last().at("balancerTag"))
    }

    @Test
    fun secretsAndHostsComeFromConfig() {
        val s = server()
        assertTrue(LinkParserTest.UUID in s.secrets)
        assertTrue("self-test" in s.secrets)
        assertTrue("0123abcd" in s.secrets)
        assertEquals(listOf("a.example.com", "203.0.113.11"), ServerConfigs.serverHosts(s))
        assertTrue(ServerConfigs.hasDns(s))
    }

    @ParameterizedTest
    @CsvSource(
        "Финляндия🇫🇮🇫🇮 xhttp, Финляндия xhttp, FI, 4",
        "🇮🇹[Ру мост]-Италия, [Ру мост]-Италия, IT, 2",
        "Автовыбор Wi-FI, Автовыбор Wi-FI, , 1",
        "by @kpenkuu4au🇬🇷, by @kpenkuu4au, GR, 2",
    )
    fun flagsAreSplitOut(name: String, plain: String, country: String?, segments: Int) {
        assertEquals(plain, Flags.strip(name))
        assertEquals(country, Flags.firstCountry(name))
        assertEquals(segments, Flags.split(name).size)
    }
}
