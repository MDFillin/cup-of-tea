package cupoftea.core

/**
 * Убирает из текста секреты (UUID, пароли, ссылки подписок с токенами, адреса серверов) перед тем, как он покинет
 * устройство (диагностика). Как LogSanitizer на десктопе: длинные строки заменяются первыми.
 */
class LogSanitizer(secrets: Collection<String>) {
    private val items = secrets.filter { it.length >= 4 }.distinct().sortedByDescending { it.length }

    fun clean(text: String): String {
        var result = text
        for (s in items) result = result.replace(s, "***", ignoreCase = true)
        // Ссылки серверов и подписок целиком — даже если секрета нет в списке.
        result = linkRegex.replace(result) { m -> m.value.substringBefore("://") + "://***" }
        return result
    }

    private companion object {
        val linkRegex = Regex("(?i)\\b(?:vless|vmess|trojan|ss|hy2|hysteria2|https?)://\\S+")
    }
}
