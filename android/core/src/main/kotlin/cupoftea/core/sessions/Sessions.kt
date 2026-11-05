package cupoftea.core.sessions

import cupoftea.core.JArr
import cupoftea.core.JObj
import cupoftea.core.Js
import cupoftea.core.config.AutoSelect
import cupoftea.core.config.BypassOptions
import cupoftea.core.config.ConfigException
import cupoftea.core.config.ServerConfigs
import cupoftea.core.profiles.Profile
import cupoftea.core.profiles.XrayConfigProfile

/** Задержка сервера: миллисекунды или ошибка. */
data class DelayResult(val milliseconds: Int?, val error: String?) {
    val ok get() = milliseconds != null
    override fun toString() = if (ok) "$milliseconds мс" else "ошибка: $error"
}

/** Сервер для пинга и его настройки обхода (свои или с поправками панели подписки). */
data class PingTarget(val profile: Profile, val bypass: BypassOptions)

/**
 * Пинг многих серверов одним xray: у каждого сервера свой выход с тегом, проверки идут через ядро по тегу
 * (без локальных портов), до 8 параллельно. Порт src/PVpn.Core/Sessions/BatchPing.cs.
 */
object BatchPing {
    const val MAX_PARALLEL = 8
    const val PROBE_URL = "https://www.gstatic.com/generate_204"

    /** Конфиг для пинга и теги выходов по Id сервера; серверы, которые не удалось описать, — в errors. */
    data class Plan(val config: JObj, val tags: Map<String, String>, val errors: Map<String, DelayResult>)

    fun plan(targets: List<PingTarget>): Plan {
        val outbounds = JArr()
        val tags = LinkedHashMap<String, String>()
        val errors = LinkedHashMap<String, DelayResult>()
        for ((i, target) in targets.withIndex()) {
            try {
                val tag = "out-$i"
                outbounds(target.profile, tag, target.bypass).forEach { outbounds.add(it) }
                tags[target.profile.id] = tag
            } catch (e: ConfigException) {
                errors[target.profile.id] = DelayResult(null, e.message)
            }
        }
        // Без входов и правил: каждую проверку ведёт ядро в выход по тегу.
        val config = Js.obj(
            "log" to Js.obj("loglevel" to "error", "access" to "none"),
            "outbounds" to outbounds,
        )
        return Plan(config, tags, errors)
    }

    /** Выходы одного сервера с уникальными тегами: сам сервер и его выход фрагментации или цепочки. */
    private fun outbounds(profile: Profile, tag: String, bypass: BypassOptions): List<JObj> =
        if (profile is XrayConfigProfile) ServerConfigs.pingOutbounds(profile, tag)
        else AutoSelect.serverOutbounds(profile, tag, "$tag-fragment", bypass)
}

enum class WatchdogAction { None, Restart }

/**
 * Сторож подключения: по результатам проверок через туннель решает, когда перезапускать xray. Две неудачи подряд
 * (при живой сети) — перезапуск; после MAX_RESTARTS перезапусков без удачной проверки сторож только наблюдает —
 * дело, скорее всего, в сервере. Удачная проверка сбрасывает счётчики. Порт ProxyWatchdog.cs.
 */
class Watchdog {
    companion object {
        const val FAILURES_TO_RESTART = 2
        const val MAX_RESTARTS = 3
        const val CHECK_INTERVAL_MS = 30_000L
        const val RECHECK_INTERVAL_MS = 10_000L
    }

    private var failures = 0
    var restarts = 0
        private set

    /** Перезапуски исчерпаны, а сервер так и не ответил. */
    val gaveUp get() = restarts >= MAX_RESTARTS && failures > 0

    /** После неудачи проверяем чаще, чтобы не ждать перезапуска минуту. */
    val nextCheckInMs get() = if (failures > 0) RECHECK_INTERVAL_MS else CHECK_INTERVAL_MS

    fun report(ok: Boolean, networkAvailable: Boolean): WatchdogAction {
        if (ok) {
            failures = 0
            restarts = 0
            return WatchdogAction.None
        }
        // Сети нет совсем — перезапуск не поможет; ждём, пока она появится.
        if (!networkAvailable) {
            failures = 0
            return WatchdogAction.None
        }
        failures++
        if (failures < FAILURES_TO_RESTART || restarts >= MAX_RESTARTS) return WatchdogAction.None
        failures = 0
        restarts++
        return WatchdogAction.Restart
    }

    /** xray остановился сам: перезапускаем, пока не исчерпан лимит. */
    fun coreExited(): WatchdogAction {
        if (restarts >= MAX_RESTARTS) return WatchdogAction.None
        restarts++
        return WatchdogAction.Restart
    }

    fun reset() {
        failures = 0
        restarts = 0
    }
}
