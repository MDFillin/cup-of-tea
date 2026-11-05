package cupoftea.tv.ui

import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.darkColorScheme

/** Палитра десктопа (Theme.xaml): нейтральный тёмный фон, зелёный акцент. */
object Palette {
    val Bg = Color(0xFF090B0A)
    val Side = Color(0xFF0D100E)
    val Surface = Color(0xFF131715)
    val Surface2 = Color(0xFF1A1F1C)
    val Surface3 = Color(0xFF222925)
    val Line = Color(0xFF1F2622)
    val Text1 = Color(0xFFF2F5F3)
    val Text2 = Color(0xFF9BA59F)
    val Text3 = Color(0xFF626B66)
    val Accent = Color(0xFF34D399)
    val OnAccent = Color(0xFF08100B)
    val Up = Color(0xFF7AA7FF)
    val Warn = Color(0xFFF5B544)
    val Error = Color(0xFFF87171)
    val Heart = Color(0xFFF43F5E)
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
