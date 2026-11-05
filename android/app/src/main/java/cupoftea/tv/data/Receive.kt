package cupoftea.tv.data

import android.content.Context
import android.os.Build
import cupoftea.core.Intake
import cupoftea.core.IntakeParser
import cupoftea.core.LogSanitizer
import cupoftea.core.config.DnsOptions
import cupoftea.core.config.DnsProvider
import cupoftea.core.config.ServerConfigs
import cupoftea.core.profiles.RoutingFetch
import cupoftea.core.profiles.XrayConfigProfile
import cupoftea.core.routing.RoutingProfiles
import cupoftea.tv.BuildConfig
import cupoftea.tv.vpn.CupVpnService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** Приём присланного с телефона. Возвращает сообщение для пользователя (и телефона, и ТВ). */
suspend fun Repository.receive(context: Context, text: String, dns: Boolean): String = withContext(Dispatchers.IO) {
    val message = when (val intake = IntakeParser.parse(text, dns)) {
        is Intake.Subscription -> addSubscription(intake.url).also { pingAll() }
        is Intake.Servers -> import(intake.text).also { pingAll() }
        is Intake.Routing -> {
            updateSettings { it.copy(routing = RoutingProfiles.validate(intake.profile), routingUrl = null) }
            CupVpnService.reconnectIfOn(context)
            "Профиль маршрутизации «${intake.profile.name}» включён (${intake.profile.ruleCount} правил)"
        }
        is Intake.Autorouting -> {
            val profile = try {
                RoutingFetch.fetch(intake.url)
            } catch (e: Exception) {
                null
            }
            if (profile == null) "По адресу ${intake.url} нет профиля маршрутизации"
            else {
                updateSettings {
                    it.copy(routing = RoutingProfiles.validate(profile), routingUrl = intake.url, routingUpdated = System.currentTimeMillis() / 1000)
                }
                CupVpnService.reconnectIfOn(context)
                "Профиль «${profile.name}» включён; будет обновляться раз в сутки"
            }
        }
        is Intake.Dns -> {
            updateSettings { it.copy(dns = DnsOptions(DnsProvider.Custom, intake.url)) }
            CupVpnService.reconnectIfOn(context)
            "Свой DNS: ${intake.url}"
        }
        is Intake.Unknown -> "Не понял, что это: ${intake.reason}"
    }
    say(message)
    message
}

/** Обновить свой профиль маршрутизации по адресу (autorouting), если прошли сутки. */
suspend fun Repository.refreshAutorouting(context: Context) = withContext(Dispatchers.IO) {
    val s = settings.value
    val url = s.routingUrl ?: return@withContext
    val now = System.currentTimeMillis() / 1000
    if (now - s.routingUpdated < 24 * 3600) return@withContext
    val profile = try {
        RoutingFetch.fetch(url)
    } catch (e: Exception) {
        null
    }
    // Не скачался — остаётся прежний; попробуем через сутки.
    updateSettings { it.copy(routing = profile?.let(RoutingProfiles::validate) ?: it.routing, routingUpdated = now) }
    if (profile != null && profile != s.routing) CupVpnService.reconnectIfOn(context)
}

/**
 * Диагностика для скачивания на телефон: версии, настройки, список серверов без адресов и журнал ядра —
 * без ключей, UUID, ссылок подписок и адресов серверов.
 */
fun Repository.diagnostics(context: Context): String {
    val d = data.value
    val secrets = buildList {
        d.profiles.forEach { p ->
            addAll(p.secrets)
            add(p.host)
            if (p is XrayConfigProfile) addAll(ServerConfigs.serverHosts(p))
        }
        d.subscriptions.forEach { add(it.url) }
        settings.value.routingUrl?.let { add(it) }
    }
    val clean = LogSanitizer(secrets)
    val log = try {
        CupVpnService.logFile(context).takeIf { it.exists() }?.readLines()?.takeLast(300)?.joinToString("\n") ?: "(журнала нет)"
    } catch (e: Exception) {
        "(журнал не прочитался: ${e.message})"
    }
    val s = settings.value
    return clean.clean(
        buildString {
            appendLine("The Cup of Tea для Android TV ${BuildConfig.VERSION_NAME} · xray ${libcup.Libcup.version()}")
            appendLine("Устройство: ${Build.MANUFACTURER} ${Build.MODEL}, Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}), ${Build.SUPPORTED_ABIS.joinToString()}")
            appendLine("Состояние: ${CupVpnService.state.value}")
            appendLine("Настройки: обход ${s.bypass}, DNS ${s.dns.provider}, маршруты ${s.routing?.name ?: "всё через VPN"}, маршруты подписки ${s.useServerRouting}, приложения ${s.splitMode} (${s.splitApps.size}), базы ${s.geoSource}")
            appendLine()
            appendLine("Подписки:")
            d.subscriptions.forEach { appendLine("  ${it.name}: серверов ${d.profiles.count { p -> p.subscriptionId == it.id }}, ошибка: ${it.lastError ?: "нет"}") }
            appendLine("Серверы: ${d.profiles.size}")
            d.profiles.forEach { appendLine("  ${it.name} · ${it.protocolLabel}") }
            appendLine()
            appendLine("Журнал ядра (последние строки):")
            appendLine(log)
        }
    )
}
