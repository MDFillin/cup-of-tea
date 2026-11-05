package cupoftea.core.routing

import cupoftea.core.config.isIpv4

/** Маршрут туннеля: адрес сети и длина префикса. */
data class Route(val address: String, val prefix: Int) {
    override fun toString() = "$address/$prefix"
}

/**
 * Какие адреса IPv4 отдавать в туннель. Локальные сети (приставка в домашней сети: DLNA, трансляция экрана, NAS)
 * идут мимо VPN, как разрешал kill switch десктопа; IP текущего сервера — тоже мимо, чтобы его панель управления
 * открывалась при подключении (0.7.1). На Android 13+ это делает excludeRoute, а на старых нужен список
 * подсетей-дополнений — его и считает [include].
 */
object TunRoutes {
    /** Адрес туннеля и его DNS — вне локальных диапазонов (198.18.0.0/15 — сеть для тестов, в интернете её нет). */
    const val TUN_ADDRESS = "198.18.0.1"
    const val TUN_PREFIX = 30
    const val TUN_DNS = "198.18.0.2"

    val localNetworks = listOf(
        Route("10.0.0.0", 8), Route("172.16.0.0", 12), Route("192.168.0.0", 16),
        Route("169.254.0.0", 16), Route("224.0.0.0", 4), Route("255.255.255.255", 32),
    )

    /** Подсети, покрывающие весь IPv4 за вычетом [excluded]. */
    fun include(excluded: List<Route>): List<Route> {
        // Интервалы [from, to] в 32-битном пространстве, отсортированные и слитые.
        val cut = excluded.map { span(it) }.sortedBy { it.first }
        val merged = mutableListOf<LongArray>()
        for ((from, to) in cut) {
            val last = merged.lastOrNull()
            if (last != null && from <= last[1] + 1) last[1] = maxOf(last[1], to) else merged.add(longArrayOf(from, to))
        }
        val result = mutableListOf<Route>()
        var start = 0L
        for ((from, to) in merged) {
            if (from > start) result.addAll(toCidrs(start, from - 1))
            start = maxOf(start, to + 1)
        }
        if (start <= MAX) result.addAll(toCidrs(start, MAX))
        return result
    }

    /** Исключения: локальные сети и адреса серверов (только IPv4, домены пропускаются). */
    fun excluded(serverIps: Collection<String>): List<Route> =
        localNetworks + serverIps.filter(::isIpv4).distinct().map { Route(it, 32) }

    private const val MAX = 0xFFFFFFFFL

    private fun span(r: Route): Pair<Long, Long> {
        val base = toLong(r.address) and mask(r.prefix)
        return base to (base or (mask(r.prefix).inv() and MAX))
    }

    private fun mask(prefix: Int): Long = if (prefix == 0) 0L else (MAX shl (32 - prefix)) and MAX

    private fun toCidrs(fromInclusive: Long, toInclusive: Long): List<Route> {
        val result = mutableListOf<Route>()
        var start = fromInclusive
        while (start <= toInclusive) {
            // Самый большой блок, выровненный по start и не выходящий за toInclusive.
            var size = if (start == 0L) 32 else java.lang.Long.numberOfTrailingZeros(start).coerceAtMost(32)
            while (size > 0 && start + (1L shl size) - 1 > toInclusive) size--
            result.add(Route(fromLong(start), 32 - size))
            start += 1L shl size
        }
        return result
    }

    private fun toLong(ip: String): Long = ip.split('.').fold(0L) { acc, part -> (acc shl 8) or part.toLong() }

    private fun fromLong(v: Long) = "${(v shr 24) and 255}.${(v shr 16) and 255}.${(v shr 8) and 255}.${v and 255}"
}
