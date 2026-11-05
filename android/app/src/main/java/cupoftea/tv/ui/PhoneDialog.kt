package cupoftea.tv.ui

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.tv.material3.Surface
import androidx.tv.material3.SurfaceDefaults
import androidx.tv.material3.Text
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import cupoftea.tv.phone.PhoneServer

/** QR-код (чёрное на белом, с тихой зоной — так его читает любая камера). */
fun qrBitmap(text: String, size: Int = 512): ImageBitmap {
    val matrix = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, mapOf(EncodeHintType.MARGIN to 2))
    val pixels = IntArray(size * size) { i -> if (matrix[i % size, i / size]) 0xFF000000.toInt() else 0xFFFFFFFF.toInt() }
    return Bitmap.createBitmap(pixels, size, size, Bitmap.Config.ARGB_8888).asImageBitmap()
}

/** «Добавить с телефона»: сервер в домашней сети работает, пока открыт этот экран. */
@Composable
fun PhoneDialog(onDismiss: () -> Unit) {
    val context = LocalContext.current
    val events = remember { mutableStateListOf<String>() }
    var url by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    DisposableEffect(Unit) {
        val server = PhoneServer(context) { e -> events.add(0, e) }
        try {
            url = server.start()
            if (url == null) error = "ТВ не подключён к домашней сети (Wi-Fi или кабель)"
        } catch (e: Exception) {
            error = "Не удалось открыть страницу: ${e.message}"
        }
        onDispose { server.stop() }
    }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(shape = RoundedCornerShape(20.dp), colors = SurfaceDefaults.colors(containerColor = Palette.Surface2)) {
            Row(Modifier.padding(36.dp), horizontalArrangement = Arrangement.spacedBy(36.dp)) {
                Box(Modifier.size(340.dp).background(Color.White, RoundedCornerShape(12.dp)), contentAlignment = Alignment.Center) {
                    val u = url
                    if (u != null) Image(remember(u) { qrBitmap(u) }, "QR-код страницы", Modifier.size(320.dp))
                    else Text(error ?: "Открываю…", color = Color.Black, fontSize = 16.sp, modifier = Modifier.padding(20.dp))
                }
                Column(Modifier.width(560.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Добавить с телефона", fontSize = 28.sp, color = Palette.Text1)
                    Text(
                        "Наведите камеру телефона на код и откройте страницу. Вставьте там ссылку подписки или серверов — " +
                            "телевизор получит её сразу. Телефон должен быть в той же Wi-Fi сети.",
                        fontSize = 17.sp, color = Palette.Text2,
                    )
                    url?.let { Text(it, fontSize = 16.sp, color = Palette.Accent) }
                    Hint("Страница работает, пока открыт этот экран (до 10 минут). «Назад» — закрыть.", Palette.Text3)
                    for (e in events.take(5)) Text("• $e", fontSize = 16.sp, color = Palette.Text1)
                    CupButton("Готово", Modifier.padding(top = 8.dp)) { onDismiss() }
                }
            }
        }
    }
}
