package cupoftea.tv.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEvent
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.type
import kotlin.random.Random

/**
 * Пасхалки (как MainViewModel.Eggs на ПК): ничего не меняют в работе VPN. Пять нажатий OK на названии в меню,
 * секретный код ↑↑↓↓←→←→ OK OK (на пульте нет B и A), поздравление на Новый год, снег зимой.
 */
object Eggs {
    private val teaLines = listOf(
        "☕ Чай заварен. Пакетики проходят через туннель без досмотра.",
        "☕ Улун, пуэр, сенча — а сервер всё равно лучший тот, что ближе.",
        "☕ Совет дня: пинг ниже, когда чай горячее. Научно не доказано.",
        "☕ Лучшая защита — постоянный VPN и кружка тёплого чая.",
        "☕ В этой версии чайник не закипает сам. Пока.",
        "☕ Если долго смотреть на кнопку питания, она начинает смотреть на вас.",
        "☕ Ни одна чаинка не пострадала при шифровании этого трафика.",
        "☕ Хватит жать на чашку, она горячая!",
    )

    /** Сообщение пасхалки (показывается на главной несколько секунд). */
    var message by mutableStateOf<String?>(null)
        private set

    /** Растёт при каждой пасхалке — главная запускает снегопад. */
    var celebrations by mutableStateOf(0)
        private set

    private var clicks = 0
    private var clicksStarted = 0L
    private var line = -1

    fun logoPressed() {
        val now = System.currentTimeMillis()
        if (now - clicksStarted > 3000) {
            clicks = 0
            clicksStarted = now
        }
        if (++clicks < 5) return
        clicks = 0
        line = (line + 1 + Random.nextInt(teaLines.size - 1)) % teaLines.size
        celebrate(teaLines[line])
    }

    private val code = listOf(
        Key.DirectionUp, Key.DirectionUp, Key.DirectionDown, Key.DirectionDown,
        Key.DirectionLeft, Key.DirectionRight, Key.DirectionLeft, Key.DirectionRight,
        Key.DirectionCenter, Key.DirectionCenter,
    )
    private val arrows = code.indexOf(Key.DirectionCenter)
    private var progress = 0
    private var swallowUp = false

    /**
     * Все нажатия пульта. Стрелки не перехватываются; OK после полностью набранных стрелок кода поглощается
     * (возвращает true), иначе он нажал бы кнопку под фокусом — например, включил VPN.
     */
    fun onKey(event: KeyEvent): Boolean {
        val key = if (event.key == Key.Enter) Key.DirectionCenter else event.key
        if (event.type == KeyEventType.KeyUp) {
            if (key == Key.DirectionCenter && swallowUp) {
                swallowUp = false
                return true
            }
            return false
        }
        if (event.type != KeyEventType.KeyDown) return false
        progress = if (key == code[progress]) progress + 1 else if (key == code[0]) 1 else 0
        val swallow = key == Key.DirectionCenter && progress > arrows
        if (swallow) swallowUp = true
        if (progress < code.size) return swallow
        progress = 0
        celebrate(
            if (Palette.winter) "❄ ↑↑↓↓←→←→: снегопад по заказу. Скорость та же, зато красиво."
            else "☕ ↑↑↓↓←→←→: секретный сорт «Улун-турбо» заварен. Скорость та же, на душе теплее."
        )
        return true
    }

    private fun celebrate(text: String) {
        message = text
        celebrations++
    }

    fun clearMessage() {
        message = null
    }
}

private class Flake(var x: Float, var y: Float, val r: Float, val speed: Float, val drift: Float, val phase: Float)

/**
 * Снег на главной (Snowfall на ПК): спокойный, 40 снежинок; после пасхалки — минуту гуще. Рисуется одним Canvas,
 * без отдельных элементов — слабому процессору приставки это почти ничего не стоит.
 */
@Composable
fun Snowfall(modifier: Modifier = Modifier, burst: Int = 0) {
    val flakes = remember { mutableListOf<Flake>() }
    var frame by remember { mutableLongStateOf(0L) }
    var burstUntil by remember { mutableLongStateOf(0L) }
    LaunchedEffect(burst) { if (burst > 0) burstUntil = System.currentTimeMillis() + 60_000 }
    LaunchedEffect(Unit) {
        var last = 0L
        while (true) {
            withFrameNanos { now ->
                val dt = if (last == 0L) 0f else ((now - last) / 1e9f).coerceAtMost(0.1f)
                last = now
                for (f in flakes) {
                    f.y += f.speed * dt
                    f.x += kotlin.math.sin((now / 1e9f) + f.phase) * f.drift * dt
                }
                frame = now
            }
        }
    }
    Canvas(modifier.fillMaxSize()) {
        val target = if (System.currentTimeMillis() < burstUntil) 140 else 40
        while (flakes.size < target) {
            flakes.add(
                Flake(
                    x = Random.nextFloat() * size.width,
                    y = if (flakes.isEmpty() && frame == 0L) Random.nextFloat() * size.height else -10f - Random.nextFloat() * size.height * 0.3f,
                    r = 1.5f + Random.nextFloat() * 3f,
                    speed = 30f + Random.nextFloat() * 60f,
                    drift = 10f + Random.nextFloat() * 25f,
                    phase = Random.nextFloat() * 6.28f,
                )
            )
        }
        val it = flakes.iterator()
        while (it.hasNext()) {
            val f = it.next()
            if (f.y > size.height + 10) {
                if (flakes.size > target) it.remove() else {
                    f.y = -10f
                    f.x = Random.nextFloat() * size.width
                }
            }
            drawCircle(Color.White.copy(alpha = 0.55f), f.r, Offset(f.x, f.y))
        }
        frame // чтение — чтобы Canvas перерисовывался каждый кадр
    }
}

/** Колпак над названием в меню зимой (на ПК — колпак на названии). */
@Composable
fun SantaHat(modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val hat = Path().apply {
            moveTo(w * 0.1f, h * 0.85f)
            quadraticTo(w * 0.45f, h * 0.05f, w * 0.9f, h * 0.2f)
            lineTo(w * 0.8f, h * 0.85f)
            close()
        }
        drawPath(hat, Color(0xFFE11D48))
        drawRoundRect(Color.White, topLeft = Offset(0f, h * 0.75f), size = androidx.compose.ui.geometry.Size(w, h * 0.25f),
            cornerRadius = androidx.compose.ui.geometry.CornerRadius(h * 0.12f))
        drawCircle(Color.White, w * 0.12f, Offset(w * 0.9f, h * 0.2f))
    }
}
