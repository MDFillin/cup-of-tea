package cupoftea.tv.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.darkColorScheme

/**
 * Палитра десктопа: обычная (Palette.xaml) и зимняя (WinterTheme.xaml, 0.8.3). Цвета читают [winter] — смена
 * оформления перерисовывает экран без перезапуска.
 */
object Palette {
    var winter by mutableStateOf(false)

    private fun pick(normal: Long, cold: Long) = Color(if (winter) cold else normal)

    val Bg get() = pick(0xFF090B0A, 0xFF080B10)
    val Side get() = pick(0xFF0D100E, 0xFF0B0F15)
    val Surface get() = pick(0xFF131715, 0xFF10151C)
    val Surface2 get() = pick(0xFF1A1F1C, 0xFF161D26)
    val Surface3 get() = pick(0xFF222925, 0xFF1E2731)
    val Line get() = pick(0xFF1F2622, 0xFF1B232D)
    val Text1 get() = pick(0xFFF2F5F3, 0xFFF1F5F9)
    val Text2 get() = pick(0xFF9BA59F, 0xFF9AA6B5)
    val Text3 get() = pick(0xFF626B66, 0xFF5E6A79)
    val Accent get() = pick(0xFF34D399, 0xFF5CC8F2)
    val OnAccent get() = pick(0xFF08100B, 0xFF061018)
    val Up get() = pick(0xFF7AA7FF, 0xFFA5B4FC)
    val Warn = Color(0xFFF5B544)
    val Error = Color(0xFFF87171)
    val Heart = Color(0xFFF43F5E)
}

/** Зимнее оформление (как Season.cs на ПК): «auto» — с 1 декабря по 28 февраля, «on» — всегда, «off» — никогда. */
object Season {
    const val AUTO = "auto"
    const val ALWAYS = "on"
    const val NEVER = "off"

    fun isWinter(month: Int) = month == 12 || month == 1 || month == 2

    fun winterFor(mode: String?, now: java.util.Calendar = java.util.Calendar.getInstance()) = when (mode) {
        ALWAYS -> true
        NEVER -> false
        else -> isWinter(now.get(java.util.Calendar.MONTH) + 1)
    }

    /** Поздравление над кнопкой на главной: 30–31 декабря и новогодние каникулы. */
    fun holidayGreeting(now: java.util.Calendar = java.util.Calendar.getInstance()): String? {
        val month = now.get(java.util.Calendar.MONTH) + 1
        val day = now.get(java.util.Calendar.DAY_OF_MONTH)
        return when {
            month == 12 && day >= 30 -> "С наступающим Новым годом!"
            month == 1 && day <= 8 -> "С Новым годом!"
            else -> null
        }
    }
}

@Composable
fun CupTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Palette.Accent,
            onPrimary = Palette.OnAccent,
            background = Palette.Bg,
            surface = Palette.Surface,
            onSurface = Palette.Text1,
            surfaceVariant = Palette.Surface2,
            onSurfaceVariant = Palette.Text2,
            inverseSurface = Palette.Text1,
            inverseOnSurface = Palette.Bg,
            border = Palette.Line,
            error = Palette.Error,
        ),
        content = content,
    )
}

/** Цвет задержки: быстро — зелёный, терпимо — жёлтый, медленно — красный. */
fun pingColor(ms: Int?): Color = when {
    ms == null -> Palette.Text3
    ms < 150 -> Palette.Accent
    ms < 400 -> Palette.Warn
    else -> Palette.Error
}

fun formatSpeed(bytesPerSecond: Long): String = when {
    bytesPerSecond >= 1_000_000 -> "%.1f Мбит/с".format(bytesPerSecond * 8 / 1_000_000.0)
    else -> "%d Кбит/с".format(bytesPerSecond * 8 / 1000)
}

fun formatBytes(bytes: Long): String = when {
    bytes >= 1L shl 30 -> "%.2f ГБ".format(bytes / (1L shl 30).toDouble())
    bytes >= 1L shl 20 -> "%.1f МБ".format(bytes / (1L shl 20).toDouble())
    else -> "%d КБ".format(bytes / 1024)
}

fun formatDuration(ms: Long): String {
    val s = ms / 1000
    return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%02d:%02d".format(s / 60, s % 60)
}
