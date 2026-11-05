package cupoftea.core

import cupoftea.core.config.BypassOptions
import cupoftea.core.geo.Geo
import cupoftea.core.profiles.LinkParser
import cupoftea.core.routing.Route
import cupoftea.core.routing.TunRoutes
import cupoftea.core.sessions.BatchPing
import cupoftea.core.sessions.PingTarget
import cupoftea.core.sessions.Watchdog
import cupoftea.core.sessions.WatchdogAction
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/** Пинг, сторож (порт WatchdogTests.cs), маршруты туннеля и базы geo. */
class SessionTest {
    @Test
    fun batchPingGivesEachServerItsOwnTaggedOutbound() {
        val targets = listOf(
            PingTarget(LinkParser.parse(LinkParserTest.XHTTP_LINK), BypassOptions.Default),
            PingTarget(LinkParser.parse(LinkParserTest.HY2_LINK), BypassOptions.Default),
            PingTarget(ServerConfigTest.server(), BypassOptions.Default),
        )
        val plan = BatchPing.plan(targets)
        assertEquals(3, plan.tags.size)
        val tags = plan.config.arrAt("outbounds").map { it.at("tag") }
        // VLESS REALITY — со своим выходом фрагментации; у всех теги уникальны.
        assertTrue("out-0" in tags && "out-0-fragment" in tags && "out-1" in tags && "out-2" in tags)
        assertEquals(tags.size, tags.toSet().size)
        // Входов нет: проверки идут через ядро по тегу, без открытых портов.
        assertTrue(plan.config["inbounds"] == null)
    }

    @Test
    fun watchdogRestartsAfterTwoFailuresAndGivesUp() {
        val w = Watchdog()
        assertEquals(WatchdogAction.None, w.report(ok = false, networkAvailable = true))
        assertEquals(WatchdogAction.Restart, w.report(ok = false, networkAvailable = true))
        // Без сети не перезапускаем.
        assertEquals(WatchdogAction.None, w.report(ok = false, networkAvailable = false))
        repeat(2) {
            w.report(ok = false, networkAvailable = true)
            w.report(ok = false, networkAvailable = true)
        }
        assertEquals(Watchdog.MAX_RESTARTS, w.restarts)
        assertEquals(WatchdogAction.None, w.report(ok = false, networkAvailable = true))
        assertTrue(w.gaveUp)
        assertEquals(WatchdogAction.None, w.report(ok = true, networkAvailable = true))
        assertFalse(w.gaveUp)
        assertEquals(0, w.restarts)
    }

    @Test
    fun tunRoutesCoverEverythingExceptLocalNetworksAndServer() {
        val routes = TunRoutes.include(TunRoutes.excluded(listOf("203.0.113.10", "example.com")))
        fun covered(ip: String) = routes.any { r ->
            val m = if (r.prefix == 0) 0L else (0xFFFFFFFFL shl (32 - r.prefix)) and 0xFFFFFFFFL
            fun v(s: String) = s.split('.').fold(0L) { a, p -> (a shl 8) or p.toLong() }
            (v(ip) and m) == v(r.address)
        }
        assertTrue(covered("8.8.8.8"))
        assertTrue(covered("203.0.113.11"))
        assertTrue(covered(TunRoutes.TUN_DNS))
        assertFalse(covered("203.0.113.10"))
        assertFalse(covered("192.168.0.50"))
        assertFalse(covered("10.1.2.3"))
        assertFalse(covered("172.20.0.1"))
        assertFalse(covered("239.255.255.250"))
        // Без исключений — один маршрут на всё.
        assertEquals(listOf(Route("0.0.0.0", 0)), TunRoutes.include(emptyList()))
    }

    @Test
    fun geoBasesAreNeededOnlyForGeoRules() {
        assertTrue(Geo.needed(ServerConfigTest.sample()))
        val plain = cupoftea.core.config.XrayConfigBuilder.build(
            LinkParser.parse(LinkParserTest.HY2_LINK), ConfigBuilderTest.tunOptions(),
        )
        assertFalse(Geo.needed(plain))
        val withProfile = cupoftea.core.config.XrayConfigBuilder.build(
            LinkParser.parse(LinkParserTest.HY2_LINK),
            ConfigBuilderTest.tunOptions().copy(routing = cupoftea.core.routing.RoutingProfiles.russiaDirect),
        )
        assertTrue(Geo.needed(withProfile))
    }
}
