package cupoftea.core.geo

import cupoftea.core.JObj
import cupoftea.core.Js
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/** Источник баз geoip/geosite: релизы на GitHub, рядом с каждым файлом — его SHA256. */
data class GeoSource(val key: String, val title: String, val geoipUrl: String, val geositeUrl: String, val note: String)

class GeoException(message: String) : Exception(message)

/**
 * Базы geoip.dat и geosite.dat (по ним работают правила geoip:/geosite: в маршрутах). На ТВ их нет в APK — они
 * скачиваются, только когда их требует сервер или профиль маршрутизации. Только GitHub по HTTPS, SHA256 из .sha256sum.
 * Порт src/PVpn.Core/Cores/GeoUpdater.cs.
 */
object Geo {
    const val MAX_FILE_BYTES = 160L * 1024 * 1024
    val files = listOf("geoip.dat", "geosite.dat")

    val sources = listOf(
        // Те же базы, что в релизе xray: v2fly (domain-list-community собирается в dlc.dat).
        GeoSource(
            "v2fly", "v2fly (как в xray)",
            "https://github.com/v2fly/geoip/releases/latest/download/geoip.dat",
            "https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat",
            "около 25 МБ",
        ),
        // Для России: всё из v2fly плюс списки заблокированного в РФ (geosite:ru-blocked и др.).
        GeoSource(
            "runetfreedom", "Россия (runetfreedom)",
            "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geoip.dat",
            "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geosite.dat",
            "около 100 МБ, на слабых приставках заметно больше памяти",
        ),
    )

    fun sourceOf(key: String?) = sources.firstOrNull { it.key == key } ?: sources[0]

    private val allowedHosts = setOf("github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com")

    /** Есть ли в конфиге правила geoip:/geosite: — тогда без баз xray не запустится. */
    fun needed(config: JObj): Boolean {
        fun walk(node: Any?): Boolean = when (node) {
            is Map<*, *> -> node.values.any(::walk)
            is List<*> -> node.any(::walk)
            is String -> node.startsWith("geoip:") || node.startsWith("geosite:")
            else -> false
        }
        return walk(config["routing"]) || walk(config["dns"])
    }

    fun installed(dir: File) = files.all { File(dir, it).length() > 0 }

    /** Скачивает обе базы во временные файлы и заменяет прежние только после проверки SHA256 обеих. */
    fun download(source: GeoSource, dir: File, progress: (String) -> Unit = {}) {
        dir.mkdirs()
        val staged = mutableListOf<Pair<File, File>>()
        try {
            for ((name, url) in listOf("geoip.dat" to source.geoipUrl, "geosite.dat" to source.geositeUrl)) {
                progress("Скачиваю $name (${source.title})")
                val part = File(dir, "$name.part")
                downloadVerified(url, part)
                staged.add(part to File(dir, name))
            }
            for ((part, target) in staged) {
                if (!part.renameTo(target)) {
                    target.delete()
                    if (!part.renameTo(target)) throw GeoException("Не удалось сохранить ${target.name}")
                }
            }
            File(dir, "geo-source.txt").writeText(source.key)
        } finally {
            staged.forEach { it.first.delete() }
        }
    }

    fun installedSource(dir: File): String? = File(dir, "geo-source.txt").takeIf { it.exists() }?.readText()?.trim()

    private fun downloadVerified(url: String, target: File) {
        val expected = Regex("(?m)^[0-9a-fA-F]{64}").find(getText("$url.sha256sum"))?.value?.lowercase()
            ?: throw GeoException("В ${url.substringAfterLast('/')}.sha256sum нет SHA256 — без неё не ставим")
        val sha = MessageDigest.getInstance("SHA-256")
        open(url).let { c ->
            try {
                var total = 0L
                c.inputStream.use { input ->
                    target.outputStream().use { output ->
                        val buf = ByteArray(64 * 1024)
                        while (true) {
                            val n = input.read(buf)
                            if (n < 0) break
                            total += n
                            if (total > MAX_FILE_BYTES) throw GeoException("${url.substringAfterLast('/')} больше ${MAX_FILE_BYTES / 1024 / 1024} МБ")
                            sha.update(buf, 0, n)
                            output.write(buf, 0, n)
                        }
                    }
                }
            } finally {
                c.disconnect()
            }
        }
        val actual = sha.digest().joinToString("") { "%02x".format(it) }
        if (actual != expected)
            throw GeoException("SHA256 ${url.substringAfterLast('/')} не совпадает с опубликованным — файл повреждён или подменён")
    }

    private fun getText(url: String): String {
        val c = open(url)
        try {
            // Файл .sha256sum крошечный; больше 4 КБ не читаем.
            val buf = ByteArray(4096)
            var len = 0
            c.inputStream.use { input ->
                while (len < buf.size) {
                    val n = input.read(buf, len, buf.size - len)
                    if (n < 0) break
                    len += n
                }
            }
            return String(buf, 0, len, Charsets.UTF_8)
        } finally {
            c.disconnect()
        }
    }

    /** GET с переадресациями только по хостам GitHub. */
    private fun open(url: String): HttpURLConnection {
        var current = URL(url)
        repeat(6) {
            if (current.protocol != "https" || current.host !in allowedHosts) throw GeoException("Базы качаются только с GitHub, а не с ${current.host}")
            val c = current.openConnection() as HttpURLConnection
            c.instanceFollowRedirects = false
            c.connectTimeout = 30_000
            c.readTimeout = 60_000
            c.setRequestProperty("User-Agent", cupoftea.core.Product.ID)
            val code = try {
                c.responseCode
            } catch (e: IOException) {
                c.disconnect()
                throw GeoException("Не удалось скачать базы: ${e.message}")
            }
            if (code in 300..399) {
                val location = c.getHeaderField("Location")
                c.disconnect()
                current = URL(current, location ?: throw GeoException("GitHub ответил $code без адреса"))
                return@repeat
            }
            if (code != 200) {
                c.disconnect()
                throw GeoException("GitHub ответил $code при скачивании ${url.substringAfterLast('/')}")
            }
            return c
        }
        throw GeoException("Слишком много переадресаций")
    }

    /** Есть ли в тексте конфига ссылки на базы (быстрая проверка без разбора). */
    fun neededInText(config: String) = Js.parseObj(config)?.let(::needed) ?: false
}
