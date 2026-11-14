package cupoftea.tv

import android.app.Application
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.VpnService
import android.util.Log
import cupoftea.tv.data.Repository
import cupoftea.tv.data.refreshAutorouting
import cupoftea.tv.vpn.CupVpnService
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

class CupApp : Application() {
    override fun onCreate() {
        super.onCreate()
        val repo = Repository.get(this)
        // Подписки по сроку (profile-update-interval, по умолчанию 12 ч; после ошибки — через 30 мин).
        // Проверка раз в 15 минут, пока жив процесс (а пока включён VPN, он жив всегда).
        // Обновления (0.8.0 на ПК): сразу при запуске, а если сети ещё нет (ТВ только включился) — повторы через
        // 10 с, 30 с и 2 мин; дальше каждые 3 ч.
        repo.scope.launch {
            val retries = longArrayOf(10_000, 30_000, 120_000)
            var attempt = 0
            while (isActive) {
                val result = cupoftea.tv.update.Updates.check(this@CupApp, notify = true)
                if (result is cupoftea.tv.update.UpdateState.Failed && attempt < retries.size) {
                    delay(retries[attempt++])
                    continue
                }
                attempt = retries.size
                delay(3 * 3600_000L)
            }
        }
        repo.scope.launch {
            if (BuildConfig.DEBUG) repo.importDevFile()
            delay(5_000)
            while (isActive) {
                try {
                    repo.updateDueSubscriptions().lastOrNull()?.let { repo.say(it) }
                    repo.refreshAutorouting(this@CupApp)
                } catch (e: Exception) {
                    Log.w("CupApp", "обновление подписок", e)
                }
                delay(15 * 60_000L)
            }
        }
    }
}

/**
 * Включение ТВ: если перед выключением VPN был включён и разрешение на VPN ещё действует — подключаемся снова
 * (на десктопе служба после перезагрузки только писала «было прервано»).
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != "android.intent.action.QUICKBOOT_POWERON") return
        val settings = Repository.get(context).settings.value
        if (!settings.connectOnBoot || !settings.wasConnected) return
        if (VpnService.prepare(context) != null) return
        CupVpnService.connect(context)
    }
}
