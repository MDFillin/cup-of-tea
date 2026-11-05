package cupoftea.core

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.util.Base64

/** Мелочи для текста, которые в .NET были встроенными (Uri.UnescapeDataString, Convert.FromBase64String). */
object Text {
    /**
     * Base64 (обычный и URL-safe, без выравнивания «=», с переводами строк) → UTF-8.
     * null — не base64 или это явно ссылка (есть «://»).
     */
    fun decodeBase64(text: String): String? {
        if (text.isEmpty() || text.contains("://")) return null
        var s = text.replace('-', '+').replace('_', '/').filterNot { it == '\n' || it == '\r' || it == ' ' || it == '\t' }
        s = s.padEnd(s.length + (4 - s.length % 4) % 4, '=')
        return try {
            String(Base64.getDecoder().decode(s), Charsets.UTF_8)
        } catch (e: IllegalArgumentException) {
            null
        }
    }

    fun encodeBase64(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)

    /** Процентная кодировка как Uri.UnescapeDataString: «+» не пробел, неверные последовательности остаются как были. */
    fun unescape(s: String): String {
        if (!s.contains('%')) return s
        val out = StringBuilder(s.length)
        var i = 0
        while (i < s.length) {
            // Подряд идущие %XX собираем в байты и пробуем как UTF-8
            val start = i
            val bytes = ByteArrayOutputStream()
            while (isEscape(s, i)) {
                bytes.write(hex(s[i + 1]) * 16 + hex(s[i + 2]))
                i += 3
            }
            if (i == start) {
                out.append(s[i])
                i++
                continue
            }
            val decoded = decodeUtf8(bytes.toByteArray())
            if (decoded != null) out.append(decoded) else out.append(s, start, i)
        }
        return out.toString()
    }

    private fun isEscape(s: String, i: Int) =
        i + 2 < s.length && s[i] == '%' && hex(s[i + 1]) >= 0 && hex(s[i + 2]) >= 0

    private fun decodeUtf8(bytes: ByteArray): String? = try {
        Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes)).toString()
    } catch (e: CharacterCodingException) {
        null
    }

    private fun hex(c: Char): Int = when (c) {
        in '0'..'9' -> c - '0'
        in 'a'..'f' -> c - 'a' + 10
        in 'A'..'F' -> c - 'A' + 10
        else -> -1
    }

    private const val UNRESERVED = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"

    /** Как Uri.EscapeDataString: всё, кроме незарезервированных символов RFC 3986, — в %XX (UTF-8). */
    fun escapeDataString(s: String): String {
        val out = StringBuilder()
        for (b in s.toByteArray(Charsets.UTF_8)) {
            val c = (b.toInt() and 0xFF).toChar()
            if (b >= 0 && UNRESERVED.indexOf(c) >= 0) out.append(c)
            else out.append('%').append("%02X".format(b.toInt() and 0xFF))
        }
        return out.toString()
    }
}
