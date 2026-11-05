package cupoftea.core

import cupoftea.core.config.BypassOptions
import cupoftea.core.config.DnsOptions
import cupoftea.core.config.DnsProvider
import cupoftea.core.config.XrayConfigBuilder
import cupoftea.core.config.XrayMode
import cupoftea.core.config.XrayOptions
import cupoftea.core.profiles.LinkParser
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/** Порт ConfigBuilderTests.cs: вместо SOCKS-бэкенда для sing-box — вход tun и DNS внутри xray. */
class ConfigBuilderTest {
    private val xhttp = LinkParser.parse(LinkParserTest.XHTTP_LINK)
    private val vision = LinkParser.parse(LinkParserTest.VISION_LINK)
    private val hy2 = LinkParser.parse(LinkParserTest.HY2_LINK)

    companion object {
        fun tunOptions() = XrayOptions(mode = XrayMode.Tun)
    }

    @Test
    fun xhttpOutboundHasReality() {
        val o = XrayConfigBuilder.buildOutbound(xhttp, "proxy")
        assertEquals("vless", o.at("protocol"))
        assertEquals("203.0.113.10", o.at("settings", "address"))
        assertEquals(443, o.at("settings", "port"))
        assertNull(o.at("settings", "flow"))
        assertEquals("xhttp", o.at("streamSettings", "network"))
        assertEquals("/deadbeef", o.at("streamSettings", "xhttpSettings", "path"))
        assertEquals("www.example.nl", o.at("streamSettings", "realitySettings", "serverName"))
        assertEquals(LinkParserTest.PBK, o.at("streamSettings", "realitySettings", "publicKey"))
        // На Android соединения не привязываются к адаптеру: программа исключена из VPN.
        assertNull(o.at("streamSettings", "sockopt"))
    }

    @Test
    fun visionOutboundHasFlowAndRawNetwork() {
        val o = XrayConfigBuilder.buildOutbound(vision, "proxy")
        assertEquals("xtls-rprx-vision", o.at("settings", "flow"))
        assertEquals("raw", o.at("streamSettings", "network"))
    }

    @Test
    fun hysteria2OutboundPinsCertificate() {
        val o = XrayConfigBuilder.buildOutbound(hy2, "proxy")
        assertEquals("hysteria", o.at("protocol"))
        assertEquals(2, o.at("settings", "version"))
        assertEquals("hysteria", o.at("streamSettings", "network"))
        assertEquals("my:s3cret-pass", o.at("streamSettings", "hysteriaSettings", "auth"))
        assertEquals(LinkParserTest.PIN, o.at("streamSettings", "tlsSettings", "pinnedPeerCertSha256"))
        assertEquals("h3", o.at("streamSettings", "tlsSettings", "alpn", 0))
        assertNull(o.at("streamSettings", "tlsSettings", "allowInsecure"))
    }

    @Test
    fun socksModeListensOnLoopbackAndSendsPrivateDirect() {
        val c = XrayConfigBuilder.build(xhttp, XrayOptions(mode = XrayMode.Socks, socksPort = 10808))
        val inbounds = c.arrAt("inbounds")
        assertEquals(listOf("socks"), inbounds.map { it.at("protocol") })
        assertEquals("127.0.0.1", inbounds.at(0, "listen"))
        assertEquals("proxy", c.at("outbounds", 0, "tag"))
        assertTrue("192.168.0.0/16" in c.arrAt("routing", "rules", 1, "ip"))
        assertNull(c["dns"])
    }

    @Test
    fun tunModeHasTunInboundAndDnsThroughXray() {
        val c = XrayConfigBuilder.build(xhttp, tunOptions())
        assertEquals("tun", c.at("inbounds", 0, "protocol"))
        assertEquals(true, c.at("inbounds", 0, "sniffing", "enabled"))
        // Первым правилом — DNS (порт 53) во внутренний DNS xray
        val first = c.objAt("routing", "rules", 0)
        assertEquals("53", first["port"])
        assertEquals(XrayConfigBuilder.DNS_OUT_TAG, first["outboundTag"])
        assertTrue(c.arrAt("outbounds").any { it.at("protocol") == "dns" })
        // DoH по имени, а имя — в hosts: отдельный DNS для самого DNS не нужен
        assertEquals("https://cloudflare-dns.com/dns-query", c.at("dns", "servers", 0))
        assertEquals("1.1.1.1", c.at("dns", "hosts", "cloudflare-dns.com", 0))
        assertEquals("UseIPv4", c.at("dns", "queryStrategy"))
    }

    @Test
    fun tunDnsFollowsProvider() {
        val c = XrayConfigBuilder.build(hy2, tunOptions().copy(dns = DnsOptions(DnsProvider.AdGuard)))
        assertEquals("https://dns.adguard-dns.com/dns-query", c.at("dns", "servers", 0))
        val custom = XrayConfigBuilder.build(hy2, tunOptions().copy(dns = DnsOptions(DnsProvider.Custom, "https://dns.example.com/q")))
        assertEquals("https://dns.example.com/q", custom.at("dns", "servers", 0))
    }

    @Test
    fun vlessDialsThroughFragmentOutbound() {
        val c = XrayConfigBuilder.build(xhttp, tunOptions())
        val outbounds = c.arrAt("outbounds")
        assertEquals("fragment", outbounds.at(0, "streamSettings", "sockopt", "dialerProxy"))
        val fragment = outbounds.single { it.at("tag") == "fragment" }
        assertEquals("tlshello", fragment.at("settings", "fragment", "packets"))

        val plain = XrayConfigBuilder.build(xhttp, tunOptions().copy(bypass = BypassOptions(fragment = false)))
        assertFalse(plain.arrAt("outbounds").any { it.at("tag") == "fragment" })
    }

    @Test
    fun hysteria2NeverUsesFragment() {
        val c = XrayConfigBuilder.build(hy2, tunOptions())
        assertFalse(c.arrAt("outbounds").any { it.at("tag") == "fragment" })
        assertNull(c.at("outbounds", 0, "streamSettings", "sockopt", "dialerProxy"))
    }

    @Test
    fun statsAndLogFile() {
        val c = XrayConfigBuilder.build(vision, tunOptions().copy(stats = true, logPath = "/data/x/xray.log"))
        assertEquals(true, c.at("policy", "system", "statsInboundDownlink"))
        assertEquals("/data/x/xray.log", c.at("log", "error"))
        assertEquals("none", c.at("log", "access"))
    }
}
