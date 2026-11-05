package cupoftea.tv.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import cupoftea.core.config.BypassPreset
import cupoftea.core.config.DnsOptions
import cupoftea.core.profiles.ProfileData
import cupoftea.core.routing.RoutingProfile
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** JSON для хранения: тип сервера — в поле kind, незнакомые поля (из будущих версий) пропускаются. */
val storeJson = Json {
    classDiscriminator = "kind"
    ignoreUnknownKeys = true
    encodeDefaults = true
}

/**
 * Серверы и подписки (в ссылках подписок — личные токены) в files/profiles.dat, зашифрованные AES-GCM ключом из
 * Android Keystore: ключ не покидает устройство и недоступен другим приложениям. Аналог DPAPI на десктопе.
 */
class ProfileStore(context: Context) {
    private val file = File(context.filesDir, "profiles.dat")

    fun load(): ProfileData {
        if (!file.exists()) return ProfileData()
        val bytes = file.readBytes()
        val iv = bytes.copyOfRange(1, 1 + IV_SIZE)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
        val plain = cipher.doFinal(bytes, 1 + IV_SIZE, bytes.size - 1 - IV_SIZE)
        return storeJson.decodeFromString(ProfileData.serializer(), String(plain, Charsets.UTF_8))
    }

    /** Запись через временный файл: при сбое питания остаётся прежняя версия. */
    fun save(data: ProfileData) {
        val plain = storeJson.encodeToString(ProfileData.serializer(), data).toByteArray(Charsets.UTF_8)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val iv = cipher.iv
        val out = byteArrayOf(VERSION) + iv + cipher.doFinal(plain)
        val tmp = File(file.parentFile, file.name + ".tmp")
        tmp.writeBytes(out)
        if (!tmp.renameTo(file)) {
            file.delete()
            tmp.renameTo(file)
        }
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    private companion object {
        const val ALIAS = "cupoftea.profiles.v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
        const val VERSION: Byte = 1
    }
}

/** Раздельное туннелирование по приложениям. */
@Serializable
enum class SplitMode { Off, Exclude, Only }

/** Настройки программы (без секретов) — files/settings.json. */
@Serializable
data class AppSettings(
    val bypass: BypassPreset = BypassPreset.Standard,
    val dns: DnsOptions = DnsOptions(),
    /** Свой профиль маршрутизации для серверов по ссылкам; null — всё через VPN. */
    val routing: RoutingProfile? = null,
    /** Откуда обновлять свой профиль маршрутизации (autorouting) — раз в сутки; null — профиль постоянный. */
    val routingUrl: String? = null,
    /** Когда профиль по routingUrl обновлялся (секунды Unix). */
    val routingUpdated: Long = 0,
    /** Маршруты серверов с полным конфигом из подписки. */
    val useServerRouting: Boolean = true,
    val splitMode: SplitMode = SplitMode.Off,
    /** Пакеты приложений для раздельного туннелирования. */
    val splitApps: Set<String> = emptySet(),
    /** Подключаться при включении ТВ (если перед выключением VPN был включён). */
    val connectOnBoot: Boolean = true,
    /** Сообщать панели подписки идентификатор устройства (x-hwid и т. п.). */
    val sendHwid: Boolean = true,
    val geoSource: String = "v2fly",
    /** Сортировка серверов: subscription, ping, name. */
    val sortOrder: String = "subscription",
    val collapsedGroups: Set<String> = emptySet(),
    /** VPN был включён (для подключения после перезагрузки). */
    val wasConnected: Boolean = false,
    /** Предел памяти ядра, МБ (мягкий, для сборщика мусора Go); 0 — без предела. */
    val coreMemoryMb: Int = 96,
)

class SettingsStore(context: Context) {
    private val file = File(context.filesDir, "settings.json")

    fun load(): AppSettings = try {
        if (file.exists()) storeJson.decodeFromString(AppSettings.serializer(), file.readText()) else AppSettings()
    } catch (e: Exception) {
        AppSettings()
    }

    fun save(s: AppSettings) {
        val tmp = File(file.parentFile, file.name + ".tmp")
        tmp.writeText(storeJson.encodeToString(AppSettings.serializer(), s))
        if (!tmp.renameTo(file)) {
            file.delete()
            tmp.renameTo(file)
        }
    }
}
