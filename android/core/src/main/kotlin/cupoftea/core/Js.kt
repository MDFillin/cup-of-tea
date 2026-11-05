package cupoftea.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.longOrNull

/** Изменяемый объект JSON (порядок ключей сохраняется) — аналог JsonObject из System.Text.Json.Nodes. */
typealias JObj = LinkedHashMap<String, Any?>

/** Изменяемый массив JSON. */
typealias JArr = ArrayList<Any?>

/**
 * Изменяемое дерево JSON для сборки и правки конфигов xray: объекты — [JObj], массивы — [JArr],
 * значения — String, Int/Long/Double, Boolean, null.
 */
object Js {
    private val pretty = Json { prettyPrint = true }

    /** Разбор текста; IllegalArgumentException (SerializationException) — не JSON. */
    fun parse(text: String): Any? = fromElement(Json.parseToJsonElement(text))

    fun parseObj(text: String): JObj? = try {
        parse(text) as? JObj
    } catch (e: IllegalArgumentException) {
        null
    }

    fun stringify(value: Any?, indented: Boolean = false): String =
        if (indented) pretty.encodeToString(JsonElement.serializer(), toElement(value)) else toElement(value).toString()

    fun obj(vararg pairs: Pair<String, Any?>): JObj = JObj().apply { pairs.forEach { (k, v) -> put(k, v) } }
    fun arr(vararg items: Any?): JArr = JArr(items.toList())
    fun arrOf(items: Iterable<Any?>): JArr = JArr(items.toList())

    /** Глубокая копия. */
    @Suppress("UNCHECKED_CAST")
    fun <T> clone(value: T): T = when (value) {
        is Map<*, *> -> JObj().apply { value.forEach { (k, v) -> put(k as String, clone(v)) } } as T
        is List<*> -> JArr(value.map { clone(it) }) as T
        else -> value
    }

    fun fromElement(e: JsonElement): Any? = when (e) {
        is JsonNull -> null
        is JsonObject -> JObj().apply { e.forEach { (k, v) -> put(k, fromElement(v)) } }
        is JsonArray -> JArr(e.map { fromElement(it) })
        is JsonPrimitive -> when {
            e.isString -> e.content
            e.booleanOrNull != null -> e.booleanOrNull
            else -> {
                val l = e.longOrNull
                when {
                    l != null && l in Int.MIN_VALUE..Int.MAX_VALUE -> l.toInt()
                    l != null -> l
                    else -> e.doubleOrNull
                }
            }
        }
    }

    fun toElement(v: Any?): JsonElement = when (v) {
        null -> JsonNull
        is JsonElement -> v
        is Map<*, *> -> JsonObject(v.entries.associate { (k, x) -> k as String to toElement(x) })
        is List<*> -> JsonArray(v.map { toElement(it) })
        is Array<*> -> JsonArray(v.map { toElement(it) })
        is String -> JsonPrimitive(v)
        is Boolean -> JsonPrimitive(v)
        is Number -> JsonPrimitive(v)
        else -> JsonPrimitive(v.toString())
    }
}

// Доступ по ключам без бесконечных приведений типов

fun Any?.asObj(): JObj? = this as? JObj
fun Any?.asArr(): JArr? = this as? JArr
fun JObj.obj(key: String): JObj? = this[key] as? JObj
fun JObj.arr(key: String): JArr? = this[key] as? JArr

/** Строка по ключу (только если там строка). */
fun JObj.str(key: String): String? = this[key] as? String

fun JObj.int(key: String): Int? = when (val v = this[key]) {
    is Int -> v
    is Long -> if (v in Int.MIN_VALUE..Int.MAX_VALUE) v.toInt() else null
    is Double -> if (v % 1.0 == 0.0) v.toInt() else null
    else -> null
}

fun JObj.bool(key: String): Boolean? = this[key] as? Boolean

/** Вложенный объект; создаётся, если его нет или там не объект. */
fun JObj.objOrPut(key: String): JObj = obj(key) ?: JObj().also { put(key, it) }

fun JObj.arrOrPut(key: String): JArr = arr(key) ?: JArr().also { put(key, it) }

fun JArr.objects(): List<JObj> = filterIsInstance<JObj>()

/** Значение как строка: строка — как есть, число и логическое — текстом, остальное — null. */
fun Any?.scalarText(): String? = when (this) {
    is String -> this
    is Int, is Long, is Boolean -> toString()
    is Double -> if (this % 1.0 == 0.0) toLong().toString() else toString()
    else -> null
}
