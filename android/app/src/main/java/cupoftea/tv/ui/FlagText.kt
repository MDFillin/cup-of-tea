package cupoftea.tv.ui

import android.content.Context
import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Text
import cupoftea.core.profiles.Flags

/**
 * Флаги — картинки Twemoji (CC-BY 4.0, assets/flags): шрифты ТВ рисуют эмодзи-флаги по-разному, а то и
 * буквами. Как на десктопе (src/PVpn.App/Assets/Flags).
 */
object FlagImages {
    private val cache = HashMap<String, ImageBitmap?>()

    fun get(context: Context, code: String): ImageBitmap? = synchronized(cache) {
        cache.getOrPut(code) {
            try {
                context.assets.open("flags/$code.png").use { BitmapFactory.decodeStream(it)?.asImageBitmap() }
            } catch (e: Exception) {
                null
            }
        }
    }
}

/** Имя сервера: флаг картинкой (первый в имени) и текст без флагов. */
@Composable
fun FlagName(
    name: String,
    modifier: Modifier = Modifier,
    fontSize: TextUnit = 20.sp,
    color: Color = Palette.Text1,
) {
    val context = LocalContext.current
    val country = Flags.firstCountry(name)
    val image = country?.let { FlagImages.get(context, it) }
    Row(modifier, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        if (image != null) {
            Image(image, contentDescription = country, modifier = Modifier.height((fontSize.value * 1.05f).dp).width((fontSize.value * 1.4f).dp))
        }
        Text(Flags.strip(name), fontSize = fontSize, color = color, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}
