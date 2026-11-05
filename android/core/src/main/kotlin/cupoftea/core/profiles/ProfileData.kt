package cupoftea.core.profiles

import kotlinx.serialization.Serializable

/** Всё, что хранится зашифрованным: серверы, выбранный сервер, подписки. Порт ProfileData из ProfileStore.cs. */
@Serializable
data class ProfileData(
    val profiles: MutableList<Profile> = mutableListOf(),
    var selectedId: String? = null,
    val subscriptions: MutableList<Subscription> = mutableListOf(),
) {
    /**
     * Добавляет профиль или заменяет профиль с тем же именем (Id сохраняется). Имя без флага-эмодзи тоже считается
     * тем же («🇳🇱 my-hy2» и «my-hy2»), если такой сервер один. true — добавлен новый.
     */
    fun upsert(profile: Profile): Boolean {
        var index = profiles.indexOfFirst { it.name.equals(profile.name, ignoreCase = true) }
        if (index < 0) {
            val plain = profiles.filter { plainName(it.name).equals(plainName(profile.name), ignoreCase = true) }
            if (plain.size == 1) index = profiles.indexOf(plain[0])
        }
        if (index < 0) {
            profiles.add(profile)
            return true
        }
        profiles[index] = profile.withIdentity(id = profiles[index].id)
        return false
    }

    /** Ищет по Id, по полному имени или по имени без флага-эмодзи впереди. */
    fun find(nameOrId: String): Profile? =
        profiles.firstOrNull { it.id == nameOrId }
            ?: profiles.firstOrNull { it.name.equals(nameOrId, ignoreCase = true) }
            ?: profiles.firstOrNull { plainName(it.name).equals(nameOrId, ignoreCase = true) }

    fun setSubscription(subscription: Subscription) {
        val i = subscriptions.indexOfFirst { it.id == subscription.id }
        if (i < 0) subscriptions.add(subscription) else subscriptions[i] = subscription
    }

    /** Удаляет подписку вместе с её серверами. */
    fun removeSubscription(id: String) {
        val ids = profiles.filter { it.subscriptionId == id }.map { it.id }.toSet()
        profiles.removeAll { it.id in ids }
        subscriptions.removeAll { it.id == id }
        if (selectedId in ids) selectedId = null
    }

    fun findSubscription(nameOrId: String): Subscription? =
        subscriptions.firstOrNull { it.id == nameOrId } ?: subscriptions.firstOrNull { it.name.equals(nameOrId, ignoreCase = true) }

    companion object {
        /** Имя без начальных эмодзи, пробелов и знаков. */
        fun plainName(name: String): String {
            var i = 0
            while (i < name.length) {
                val cp = name.codePointAt(i)
                if (Character.isLetterOrDigit(cp)) break
                i += Character.charCount(cp)
            }
            return name.substring(i)
        }
    }
}

/**
 * Флаги-эмодзи в именах серверов («Финляндия🇫🇮 xhttp»). Флаг — две буквы Regional Indicator (🇫 + 🇮 = FI).
 * Порт Flags.cs: интерфейс ТВ рисует флаги картинками Twemoji, а в уведомлениях флаги убираются.
 */
object Flags {
    private const val FIRST = 0x1F1E6
    private const val LAST = 0x1F1FF

    /** Кусок имени: текст или флаг страны (country — код ISO, «FI»). */
    data class Segment(val text: String, val country: String?)

    fun split(text: String): List<Segment> {
        val result = mutableListOf<Segment>()
        val sb = StringBuilder()
        val cps = text.codePoints().toArray()
        var i = 0
        while (i < cps.size) {
            val c = cps[i]
            if (isIndicator(c) && i + 1 < cps.size && isIndicator(cps[i + 1])) {
                if (sb.isNotEmpty()) {
                    result.add(Segment(sb.toString(), null))
                    sb.clear()
                }
                val code = "${letter(c)}${letter(cps[i + 1])}"
                result.add(Segment(String(Character.toChars(c)) + String(Character.toChars(cps[i + 1])), code))
                i += 2
                continue
            }
            // Одинокая буква-индикатор, флаги регионов (🏴 + теги) и вариационные селекторы — не текст.
            if (!(isIndicator(c) || c == 0x1F3F4 || c == 0xFE0F || c in 0xE0020..0xE007F)) sb.appendCodePoint(c)
            i++
        }
        if (sb.isNotEmpty()) result.add(Segment(sb.toString(), null))
        return result
    }

    /** Код страны первого флага в имени; null — флагов нет. */
    fun firstCountry(text: String): String? = split(text).firstOrNull { it.country != null }?.country

    /** Имя без флагов. «Финляндия🇫🇮🇫🇮 xhttp» → «Финляндия xhttp». */
    fun strip(text: String): String {
        val joined = split(text).filter { it.country == null }.map { it.text.trim() }.filter { it.isNotEmpty() }.joinToString(" ")
        return joined.ifEmpty { text.trim() }
    }

    private fun isIndicator(c: Int) = c in FIRST..LAST
    private fun letter(c: Int) = 'A' + (c - FIRST)
}
