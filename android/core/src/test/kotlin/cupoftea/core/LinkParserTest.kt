package cupoftea.core

import cupoftea.core.profiles.Hysteria2Profile
import cupoftea.core.profiles.LinkParseException
import cupoftea.core.profiles.LinkParser
import cupoftea.core.profiles.ServerTransport
import cupoftea.core.profiles.VlessProfile
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.Arguments
import org.junit.jupiter.params.provider.MethodSource
import java.util.Base64

/** Порт tests/PVpn.Core.Tests/LinkParserTests.cs. Все ключи и пароли — фиктивные. */
class LinkParserTest {
    companion object {
        const val UUID = "11111111-2222-4333-8444-555555555555"
        const val PBK = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde"
        const val PIN = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99"

        const val XHTTP_LINK =
            "vless://$UUID@203.0.113.10:443?encryption=none&security=reality&sni=www.example.nl&fp=chrome&pbk=$PBK&sid=0123abcd&type=xhttp&path=%2Fdeadbeef&mode=auto#my-xhttp"
        const val VISION_LINK =
            "vless://$UUID@203.0.113.10:8443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.example.nl&fp=chrome&pbk=$PBK&sid=0123abcd&type=tcp&headerType=none#my-vision"
        const val HY2_LINK = "hy2://my:s3cret-pass@203.0.113.10:443?sni=www.example.nl&insecure=1&pinSHA256=$PIN#my-hy2"

        @JvmStatic
        fun rejected() = listOf(
            Arguments.of("vless://$UUID@203.0.113.10:443?type=kcp&security=none#x", "kcp"),
            Arguments.of("vless://$UUID@203.0.113.10:443?security=reality&sni=www.example.nl#x", "pbk"),
            Arguments.of("vless://$UUID@203.0.113.10:443?security=reality&pbk=$PBK#x", "sni"),
            Arguments.of("vless://$UUID@203.0.113.10?security=none#x", "порт"),
            Arguments.of("vless://203.0.113.10:443?security=none#x", "UUID"),
            Arguments.of("hy2://pass@203.0.113.10:443?insecure=1#x", "pinSHA256"),
            Arguments.of("hy2://pass@203.0.113.10:443?obfs=salamander#x", "Обфускация"),
            Arguments.of("hy2://pass@203.0.113.10:443?mport=abc#x", "mport"),
            Arguments.of("hy2://pass@203.0.113.10:abc#x", "порт"),
            Arguments.of("vless://$UUID@203.0.113.10:443,8443?security=none#x", "только у hy2"),
            Arguments.of("hy2://pass@203.0.113.10:443?pinSHA256=1234#x", "SHA-256"),
            Arguments.of("tuic://pass@203.0.113.10:443#x", "Неизвестный"),
        )

        @JvmStatic
        fun hopping() = listOf(
            Arguments.of("hy2://pass@203.0.113.10:20000-30000?sni=www.example.nl#x", 20000, "20000-30000"),
            Arguments.of("hy2://pass@203.0.113.10:443,20000-30000#x", 443, "443,20000-30000"),
            Arguments.of("hy2://pass@203.0.113.10:443?mport=20000-30000#x", 443, "20000-30000"),
            Arguments.of("hy2://pass@203.0.113.10:443?mport=20000:30000#x", 443, "20000-30000"),
            Arguments.of("hysteria2://pass@203.0.113.10:5000-5010#x", 5000, "5000-5010"),
            Arguments.of("hy2://pass@203.0.113.10:443?mport=443#x", 443, null),
        )
    }

    @Test
    fun parsesXhttpReality() {
        val p = LinkParser.parse(XHTTP_LINK) as VlessProfile
        assertEquals("my-xhttp", p.name)
        assertEquals("203.0.113.10", p.host)
        assertEquals(443, p.port)
        assertEquals(UUID, p.uuid)
        assertEquals("xhttp", p.stream.network)
        assertEquals("reality", p.stream.security)
        assertEquals("www.example.nl", p.stream.sni)
        assertEquals(PBK, p.stream.publicKey)
        assertEquals("0123abcd", p.stream.shortId)
        assertEquals("/deadbeef", p.stream.xhttpPath)
        assertEquals("auto", p.stream.xhttpMode)
        assertNull(p.flow)
        assertEquals("VLESS XHTTP REALITY", p.protocolLabel)
        assertEquals(ServerTransport.Tcp, p.transport)
    }

    @Test
    fun parsesVisionReality() {
        val p = LinkParser.parse(VISION_LINK) as VlessProfile
        assertEquals(8443, p.port)
        assertEquals("raw", p.stream.network)
        assertEquals("xtls-rprx-vision", p.flow)
        assertNull(p.stream.xhttpPath)
        assertEquals("VLESS Vision REALITY", p.protocolLabel)
    }

    @Test
    fun parsesHysteria2WithPin() {
        val p = LinkParser.parse(HY2_LINK) as Hysteria2Profile
        assertEquals("my:s3cret-pass", p.auth)
        assertEquals("www.example.nl", p.sni)
        assertEquals(PIN, p.pinSha256)
        assertTrue(p.insecure)
        assertEquals(ServerTransport.Udp, p.transport)
    }

    @Test
    fun acceptsHysteria2SchemeAndPinWithoutColons() {
        val pin = PIN.replace(":", "").lowercase()
        val p = LinkParser.parse("hysteria2://pass@203.0.113.10:443?pinSHA256=$pin#x") as Hysteria2Profile
        assertEquals("pass", p.auth)
        assertFalse(p.insecure)
        assertEquals(pin, p.pinSha256)
    }

    @Test
    fun decodesCyrillicNameAndIpv6Host() {
        val p = LinkParser.parse("vless://$UUID@[2001:db8::1]:443?security=none#%D0%AF-%D0%B4%D0%BE%D0%BC")
        assertEquals("Я-дом", p.name)
        assertEquals("2001:db8::1", p.host)
    }

    @Test
    fun defaultsNameToHostAndPort() {
        assertEquals("203.0.113.10:443", LinkParser.parse("vless://$UUID@203.0.113.10:443?security=none").name)
    }

    @ParameterizedTest
    @MethodSource("rejected")
    fun rejectsUnsupportedLinks(link: String, expectedInMessage: String) {
        val e = assertThrows<LinkParseException> { LinkParser.parse(link) }
        assertTrue(e.message!!.contains(expectedInMessage), e.message)
    }

    @Test
    fun parseManyFindsLinksInLabelledTextAndReportsErrors() {
        val text = "Мои ключи:\nmy-xhttp: $XHTTP_LINK\n$VISION_LINK   $HY2_LINK\n\nсломанная: vless://broken"
        val result = LinkParser.parseMany(text)
        assertEquals(listOf("my-xhttp", "my-vision", "my-hy2"), result.profiles.map { it.name })
        assertEquals(1, result.errors.size)
        assertEquals(5, result.errors[0].line)
    }

    @Test
    fun parseManyDecodesBase64Subscription() {
        val base64 = Base64.getEncoder().encodeToString("$XHTTP_LINK\n$HY2_LINK".toByteArray())
        assertEquals(2, LinkParser.parseMany(base64).profiles.size)
    }

    @ParameterizedTest
    @MethodSource("hopping")
    fun parsesHysteria2PortHopping(link: String, port: Int, hop: String?) {
        val p = LinkParser.parse(link) as Hysteria2Profile
        assertEquals(port, p.port)
        assertEquals(hop, p.hopPorts)
        assertEquals(if (hop == null) "Hysteria2" else "Hysteria2 (смена портов)", p.protocolLabel)
    }

    @Test
    fun parsesHysteria2Salamander() {
        val p = LinkParser.parse(
            "hy2://pass@203.0.113.10:8443?sni=www.example.nl&pinSHA256=$PIN&obfs=salamander&obfs-password=0bf5-s3cret&mport=30000-39999#obfs"
        ) as Hysteria2Profile
        assertEquals("0bf5-s3cret", p.obfsPassword)
        assertTrue("0bf5-s3cret" in p.secrets)
        assertEquals("Hysteria2 (salamander, смена портов)", p.protocolLabel)
        val e = assertThrows<LinkParseException> { LinkParser.parse("hy2://pass@203.0.113.10:443?obfs=salamander#x") }
        assertTrue(e.message!!.contains("obfs-password"))
    }

    @Test
    fun rejectsInvalidExtraJson() {
        assertThrows<LinkParseException> { LinkParser.parse(XHTTP_LINK.replace("#my-xhttp", "&extra=%7Bbroken#my-xhttp")) }
    }
}
