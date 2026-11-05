package cupoftea.core

/** Путь по дереву JSON в тестах: at("outbounds", 0, "settings", "address"). */
fun Any?.at(vararg path: Any): Any? {
    var node: Any? = this
    for (p in path) {
        node = when (p) {
            is Int -> (node as? List<*>)?.getOrNull(p)
            is String -> (node as? Map<*, *>)?.get(p)
            else -> null
        }
    }
    return node
}

@Suppress("UNCHECKED_CAST")
fun Any?.objAt(vararg path: Any): JObj = at(*path) as JObj

@Suppress("UNCHECKED_CAST")
fun Any?.arrAt(vararg path: Any): JArr = at(*path) as JArr
