package cupoftea.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.Base64

/** Разбор присланного с телефона: подписки, ссылки, профили маршрутизации, DNS. Адреса — фиктивные. */
class IntakeTest {
    private fun b64(s: String) = Base64.getEncoder().encodeToString(s.toByteArray())

    @Test
    fun subscriptionUrlAndAppLinks() {
        assertEquals(Intake.Subscription("https://panel.example.com/sub/abc"), IntakeParser.parse("  https://panel.example.com/sub/abc \n"))
        assertEquals(Intake.Subscription("https://panel.example.com/sub/abc"), IntakeParser.parse("cupoftea://add/https://panel.example.com/sub/abc"))
        assertEquals(
            Intake.Subscription("https://panel.example.com/sub/abc"),
            IntakeParser.parse("anyapp://import/" + b64("https://panel.example.com/sub/abc")),
        )
    }

    @Test
    fun serverLinksAndConfigs() {
        assertTrue(IntakeParser.parse(LinkParserTest.VISION_LINK) is Intake.Servers)
        assertTrue(IntakeParser.parse("cupoftea://add/" + b64(LinkParserTest.HY2_LINK)) is Intake.Servers)
        assertTrue(IntakeParser.parse("[" + ServerConfigTest.SAMPLE + "]") is Intake.Servers)
    }

    @Test
    fun routingProfiles() {
        val json = """{"Name":"Тест","DirectSites":["geosite:category-ru"],"GlobalProxy":"true"}"""
        val r = IntakeParser.parse(json) as Intake.Routing
        assertEquals("Тест", r.profile.name)
        assertTrue(IntakeParser.parse("cupoftea://routing/onadd/" + b64(json)) is Intake.Routing)
        assertEquals(
            Intake.Autorouting("https://rules.example.com/ru.json"),
            IntakeParser.parse("cupoftea://autorouting/onadd/https://rules.example.com/ru.json"),
        )
    }

    @Test
    fun customDnsAndGarbage() {
        assertEquals(Intake.Dns("https://dns.example.com/dns-query"), IntakeParser.parse("dns.example.com", dns = true))
        assertTrue(IntakeParser.parse("ftp://x", dns = true) is Intake.Unknown)
        assertTrue(IntakeParser.parse("   ") is Intake.Unknown)
        assertTrue(IntakeParser.parse("""{"foo":1}""") is Intake.Unknown)
    }
}
