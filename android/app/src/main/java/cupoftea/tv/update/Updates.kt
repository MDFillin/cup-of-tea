package cupoftea.tv.update

import android.app.Activity
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.util.Log
import cupoftea.core.AppUpdater
import cupoftea.core.TvRelease
import cupoftea.tv.BuildConfig
import cupoftea.tv.MainActivity
import cupoftea.tv.data.Repository
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withContext
import java.io.File

sealed interface UpdateState {
    data object Idle : UpdateState
    data object Checking : UpdateState
    data object UpToDate : UpdateState
    data class Available(val release: TvRelease) : UpdateState
    data class Downloading(val release: TvRelease, val percent: Int) : UpdateState
    data class Installing(val release: TvRelease) : UpdateState
    data class Failed(val message: String) : UpdateState
}

/**
 * Обновление программы из неё самой (0.7.2 на десктопе): проверка через 15 с после запуска и каждые 3 ч,
 * плашка на «Главной», уведомление один раз на версию; установка — системным установщиком Android
 * (PackageInstaller), он сам спросит подтверждение.
 */
object Updates {
    private const val TAG = "CupUpdate"
    private const val CHANNEL = "updates"
    private const val NOTIFICATION_ID = 3
    const val ACTION_INSTALL = "cupoftea.tv.INSTALL_UPDATE"

    private val _state = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val state: StateFlow<UpdateState> = _state

    /** Архитектуры устройства в порядке предпочтения (SberBox — только armeabi-v7a при 64-битном процессоре). */
    private val abis get() = Build.SUPPORTED_ABIS.toList()

    suspend fun check(context: Context, notify: Boolean): UpdateState = withContext(Dispatchers.IO) {
        val busy = _state.value
        if (busy is UpdateState.Downloading || busy is UpdateState.Installing) return@withContext busy
        _state.value = UpdateState.Checking
        val result = try {
            AppUpdater.check(BuildConfig.VERSION_NAME, abis)?.let { UpdateState.Available(it) } ?: UpdateState.UpToDate
        } catch (e: Exception) {
            UpdateState.Failed(e.message ?: "проверка не удалась")
        }
        _state.value = result
        if (notify && result is UpdateState.Available) notifyOnce(context, result.release)
        result
    }

    /** Уведомление о версии — один раз (в фоне); плашка на «Главной» видна при каждом запуске, пока версия устарела. */
    private fun notifyOnce(context: Context, release: TvRelease) {
        val repo = Repository.get(context)
        if (repo.settings.value.notifiedVersion == release.version) return
        repo.updateSettings { it.copy(notifiedVersion = release.version) }
        val nm = context.getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26 && nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Обновления", NotificationManager.IMPORTANCE_DEFAULT))
        val open = PendingIntent.getActivity(
            context, 1, Intent(context, MainActivity::class.java).setAction(ACTION_INSTALL), PendingIntent.FLAG_IMMUTABLE,
        )
        val b = if (Build.VERSION.SDK_INT >= 26) android.app.Notification.Builder(context, CHANNEL)
        else @Suppress("DEPRECATION") android.app.Notification.Builder(context)
        nm.notify(
            NOTIFICATION_ID,
            b.setContentTitle("The Cup of Tea ${release.version}")
                .setContentText("Доступна новая версия — откройте, чтобы установить")
                .setSmallIcon(android.R.drawable.stat_sys_download_done)
                .setContentIntent(open).setAutoCancel(true).build(),
        )
    }

    /** Можно ли ставить APK: на Android 8+ это отдельное разрешение «Установка неизвестных приложений». */
    fun canInstall(context: Context) = Build.VERSION.SDK_INT < 26 || context.packageManager.canRequestPackageInstalls()

    /** Открыть системное разрешение на установку для нашей программы (на некоторых ТВ такого экрана нет). */
    fun askInstallPermission(activity: Activity): Boolean = try {
        activity.startActivity(
            Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${activity.packageName}"))
        )
        true
    } catch (e: Exception) {
        false
    }

    /** Скачать, проверить SHA256 и отдать системному установщику. */
    suspend fun install(context: Context, release: TvRelease) = withContext(Dispatchers.IO) {
        try {
            val dir = File(context.cacheDir, "updates").apply { mkdirs() }
            dir.listFiles()?.forEach { it.delete() }
            val apk = File(dir, AppUpdater.assetName(release.version, "update"))
            _state.value = UpdateState.Downloading(release, 0)
            AppUpdater.download(release, apk) { done, total ->
                if (total > 0) _state.value = UpdateState.Downloading(release, (done * 100 / total).toInt())
            }
            _state.value = UpdateState.Installing(release)
            val installer = context.packageManager.packageInstaller
            val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
            params.setAppPackageName(context.packageName)
            val sessionId = installer.createSession(params)
            installer.openSession(sessionId).use { session ->
                session.openWrite("base.apk", 0, apk.length()).use { out ->
                    apk.inputStream().use { it.copyTo(out) }
                    session.fsync(out)
                }
                val status = PendingIntent.getBroadcast(
                    context, sessionId, Intent(context, InstallReceiver::class.java),
                    PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0),
                )
                session.commit(status.intentSender)
            }
        } catch (e: Exception) {
            Log.e(TAG, "обновление", e)
            _state.value = UpdateState.Failed(e.message ?: "не удалось установить")
        }
    }

    internal fun failed(message: String) {
        _state.value = UpdateState.Failed(message)
    }
}

/** Итог установки от PackageInstaller: подтверждение пользователя или ошибка. */
class InstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT) ?: return
                context.startActivity(confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            PackageInstaller.STATUS_SUCCESS -> {} // программа перезапустится новой версией
            else -> Updates.failed(intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "установка отменена")
        }
    }
}
