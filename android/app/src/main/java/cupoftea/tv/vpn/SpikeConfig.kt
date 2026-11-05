package cupoftea.tv.vpn

import cupoftea.core.Js
import cupoftea.core.config.ServerConfigs
import cupoftea.core.config.XrayConfigBuilder
import cupoftea.core.config.XrayMode
import cupoftea.core.config.XrayOptions
import cupoftea.core.profiles.Profile

/**
 * Проба (этап 0): серверы из `export --configs` (JSON-массив полных конфигов xray) — через настоящий
 * сборщик :core (ServerConfigs.buildRuntime) с входом tun. Уйдёт, когда появится хранилище и подписки.
 */
object SpikeConfig {
    fun profiles(text: String): List<Profile> = ServerConfigs.parseMany(text).profiles

    fun names(text: String): List<String> = profiles(text).map { it.name }

    fun build(text: String, index: Int, logPath: String): String {
        val profile = profiles(text)[index]
        val config = XrayConfigBuilder.build(profile, XrayOptions(mode = XrayMode.Tun, logPath = logPath, stats = true))
        return Js.stringify(config)
    }
}
