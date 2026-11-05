package cupoftea.core

import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/** Новая версия для ТВ: APK под архитектуру устройства из релиза GitHub и его SHA256. */
data class TvRelease(val version: String, val apkUrl: String, val sha256: String, val size: Long, val page: String, val notes: String?)

class AppUpdateException(message: String) : Exception(message)

/**
 * Обновление ТВ-версии из неё самой. Релизы ТВ — в том же репозитории, что и десктоп ([Product.UPDATE_REPOSITORY]),
 * с тегами tv-v<версия> и пометкой «не последний» (десктоп берёт releases/latest и ждёт тег x.y.z). Поэтому здесь
 * читается список релизов и выбирается самый новый tv-v*. APK — CupOfTea-TV-<версия>-<abi>.apk; SHA256 — из digest,
 * который указывает GitHub (без него не ставим). Порт идеи AppUpdater.cs.
 */
object AppUpdater {
    const val TAG_PREFIX = "tv-v"
    const val MAX_APK_BYTES = 150L * 1024 * 1024
    private val versionRegex = Regex("^\\d+\\.\\d+\\.\\d+$")
    private val githubHosts = setOf("github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com")

    fun assetName(version: String, abi: String) = "CupOfTea-TV-$version-$abi.apk"

    /** Самый новый релиз ТВ новее [current] с APK под одну из [abis] (в порядке предпочтения); null — обновлений нет. */
    fun check(current: String, abis: List<String>, repository: String = Product.UPDATE_REPOSITORY): TvRelease? {
        val json = get("https://api.github.com/repos/$repository/releases?per_page=30")
        return pick(json, current, abis, repository)
    }

    /** Разбор ответа GitHub (вынесен для тестов). */
    fun pick(json: String, current: String, abis: List<String>, repository: String = Product.UPDATE_REPOSITORY): TvRelease? {
        val releases = Js.parse(json) as? List<*> ?: throw AppUpdateException("GitHub вернул не список релизов")
        val candidates = releases.mapNotNull { it as? Map<*, *> }
            .filter { it["draft"] != true && it["prerelease"] != true }
            .mapNotNull { r ->
                val tag = r["tag_name"] as? String ?: return@mapNotNull null
                if (!tag.startsWith(TAG_PREFIX)) return@mapNotNull null
                val version = tag.removePrefix(TAG_PREFIX)
                if (!versionRegex.matches(version)) null else version to r
            }
        val (version, release) = candidates.maxWithOrNull { a, b -> compareVersions(a.first, b.first) } ?: return null
        if (compareVersions(version, current) <= 0) return null

        val assets = (release["assets"] as? List<*>)?.mapNotNull { it as? Map<*, *> } ?: emptyList()
        for (abi in abis) {
            val name = assetName(version, abi)
            val asset = assets.firstOrNull { it["name"] == name } ?: continue
            val digest = asset["digest"] as? String
            val sha = digest?.takeIf { it.startsWith("sha256:") }?.removePrefix("sha256:")?.lowercase()
                ?.takeIf { it.length == 64 && it.all { c -> c in '0'..'9' || c in 'a'..'f' } }
                ?: throw AppUpdateException("GitHub не указал SHA256 для $name — без него не ставлю")
            val url = asset["browser_download_url"] as? String ?: continue
            if (!isGithub(url)) throw AppUpdateException("Ссылка на APK ведёт не на GitHub")
            val size = (asset["size"] as? Number)?.toLong() ?: 0
            val page = (release["html_url"] as? String)?.takeIf(::isGithub) ?: "https://github.com/$repository/releases"
            val notes = (release["body"] as? String)?.trim()?.takeIf { it.isNotEmpty() }?.let { if (it.length > 600) it.take(600) + "…" else it }
            return TvRelease(version, url, sha, size, page, notes)
        }
        throw AppUpdateException("В релизе $TAG_PREFIX$version нет APK для этого устройства (${abis.joinToString()})")
    }

    /** «0.10.0» > «0.9.3». */
    fun compareVersions(a: String, b: String): Int {
        val x = a.split('.').map { it.toIntOrNull() ?: 0 }
        val y = b.split('.').map { it.toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(x.size, y.size)) {
            val d = (x.getOrElse(i) { 0 }).compareTo(y.getOrElse(i) { 0 })
            if (d != 0) return d
        }
        return 0
    }

    /** Скачивает APK в [target] и сверяет SHA256; при несовпадении файл удаляется. */
    fun download(release: TvRelease, target: File, progress: (Long, Long) -> Unit = { _, _ -> }) {
        if (release.size > MAX_APK_BYTES) throw AppUpdateException("APK слишком большой (${release.size / 1024 / 1024} МБ)")
        val c = open(release.apkUrl)
        val sha = MessageDigest.getInstance("SHA-256")
        val part = File(target.parentFile, target.name + ".part")
        try {
            var total = 0L
            c.inputStream.use { input ->
                part.outputStream().use { output ->
                    val buf = ByteArray(64 * 1024)
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        total += n
                        if (total > MAX_APK_BYTES) throw AppUpdateException("APK больше ${MAX_APK_BYTES / 1024 / 1024} МБ")
                        sha.update(buf, 0, n)
                        output.write(buf, 0, n)
                        progress(total, release.size)
                    }
                }
            }
        } finally {
            c.disconnect()
        }
        val actual = sha.digest().joinToString("") { "%02x".format(it) }
        if (actual != release.sha256) {
            part.delete()
            throw AppUpdateException("SHA256 APK не совпадает с указанным GitHub — файл повреждён или подменён")
        }
        target.delete()
        if (!part.renameTo(target)) throw AppUpdateException("Не удалось сохранить APK")
    }

    private fun isGithub(url: String) = try {
        val u = URL(url)
        u.protocol == "https" && u.host in githubHosts
    } catch (e: Exception) {
        false
    }

    private fun get(url: String): String {
        val c = open(url)
        try {
            return c.inputStream.use { String(it.readBytes(), Charsets.UTF_8) }
        } finally {
            c.disconnect()
        }
    }

    /** GET с переадресациями только по хостам GitHub. */
    private fun open(url: String): HttpURLConnection {
        var current = URL(url)
        repeat(6) {
            if (current.protocol != "https" || current.host !in githubHosts) throw AppUpdateException("Обновления только с GitHub, а не с ${current.host}")
            val c = current.openConnection() as HttpURLConnection
            c.instanceFollowRedirects = false
            c.connectTimeout = 20_000
            c.readTimeout = 60_000
            c.setRequestProperty("User-Agent", Product.ID)
            c.setRequestProperty("Accept", "application/vnd.github+json")
            val code = try {
                c.responseCode
            } catch (e: IOException) {
                c.disconnect()
                throw AppUpdateException("GitHub недоступен: ${e.message}")
            }
            if (code in 300..399) {
                val location = c.getHeaderField("Location")
                c.disconnect()
                current = URL(current, location ?: throw AppUpdateException("GitHub ответил $code без адреса"))
                return@repeat
            }
            if (code == 404) {
                c.disconnect()
                throw AppUpdateException("На GitHub пока нет релизов")
            }
            if (code != 200) {
                c.disconnect()
                throw AppUpdateException("GitHub ответил $code")
            }
            return c
        }
        throw AppUpdateException("Слишком много переадресаций")
    }
}
