package cupoftea.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Выбор релиза ТВ среди релизов десктопа и ТВ в одном репозитории. */
class AppUpdaterTest {
    private val sha = "a".repeat(64)

    private fun asset(name: String, digest: String? = "sha256:$sha") =
        """{"name":"$name","size":15000000,"browser_download_url":"https://github.com/MDFillin/cup-of-tea/releases/download/x/$name"${digest?.let { ",\"digest\":\"$it\"" } ?: ""}}"""

    private fun release(tag: String, vararg assets: String, prerelease: Boolean = false) =
        """{"tag_name":"$tag","draft":false,"prerelease":$prerelease,"html_url":"https://github.com/MDFillin/cup-of-tea/releases/tag/$tag","body":"Что нового","assets":[${assets.joinToString(",")}]}"""

    private val list = "[" + listOf(
        release("v0.8.0", asset("CupOfTea-Setup-0.8.0.exe")),
        release("tv-v0.3.0", asset("CupOfTea-TV-0.3.0-armeabi-v7a.apk"), prerelease = true),
        release("tv-v0.2.0", asset("CupOfTea-TV-0.2.0-arm64-v8a.apk"), asset("CupOfTea-TV-0.2.0-armeabi-v7a.apk")),
        release("tv-v0.1.0", asset("CupOfTea-TV-0.1.0-armeabi-v7a.apk")),
    ).joinToString(",") + "]"

    @Test
    fun picksNewestTvReleaseIgnoringDesktopAndPrereleases() {
        val r = AppUpdater.pick(list, "0.1.0", listOf("armeabi-v7a", "armeabi"))!!
        assertEquals("0.2.0", r.version)
        assertTrue(r.apkUrl.endsWith("CupOfTea-TV-0.2.0-armeabi-v7a.apk"))
        assertEquals(sha, r.sha256)
        assertEquals("Что нового", r.notes)
    }

    @Test
    fun nothingWhenUpToDate() {
        assertNull(AppUpdater.pick(list, "0.2.0", listOf("armeabi-v7a")))
        assertNull(AppUpdater.pick("[" + release("v0.9.0", asset("CupOfTea-Setup-0.9.0.exe")) + "]", "0.1.0", listOf("arm64-v8a")))
    }

    @Test
    fun refusesWithoutDigestOrMatchingAbi() {
        val noDigest = "[" + release("tv-v0.5.0", asset("CupOfTea-TV-0.5.0-arm64-v8a.apk", digest = null)) + "]"
        assertThrows<AppUpdateException> { AppUpdater.pick(noDigest, "0.1.0", listOf("arm64-v8a")) }
        assertThrows<AppUpdateException> { AppUpdater.pick(list, "0.1.0", listOf("x86_64")) }
    }

    @Test
    fun comparesVersionsNumerically() {
        assertTrue(AppUpdater.compareVersions("0.10.0", "0.9.3") > 0)
        assertEquals(0, AppUpdater.compareVersions("1.0", "1.0.0"))
    }
}
