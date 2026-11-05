package cupoftea.tv.phone

import android.content.Context
import android.net.ConnectivityManager
import android.util.Log
import cupoftea.tv.data.Repository
import cupoftea.tv.data.diagnostics
import cupoftea.tv.data.receive
import kotlinx.coroutines.runBlocking
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.SecureRandom
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger

/**
 * Страница «Добавить с телефона»: маленький HTTP-сервер в домашней сети, пока открыт экран на ТВ. В адресе —
 * одноразовый ключ (без него — 404), отвечает только адресам локальной сети, сам выключается через 10 минут или
 * после 20 неверных ключей. Набирать ссылку подписки пультом не нужно: телефон вставляет её на странице.
 */
class PhoneServer(private val context: Context, private val onEvent: (String) -> Unit) {
    companion object {
        private const val TAG = "CupPhone"
        private const val MAX_BODY = 4 * 1024 * 1024
        const val LIFETIME_MS = 10 * 60_000L
        private const val MAX_BAD_TOKENS = 20
    }

    private val repo = Repository.get(context)
    private val token = ByteArray(12).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
    private var socket: ServerSocket? = null
    private val pool = Executors.newFixedThreadPool(3)
    private val badTokens = AtomicInteger()

    /** Адрес для QR-кода; null — нет локальной сети (или сервер не запустился). */
    @Volatile
    var url: String? = null
        private set

    fun start(): String? {
        val ip = lanAddress() ?: return null
        val server = ServerSocket()
        server.reuseAddress = true
        server.bind(InetSocketAddress(ip, 0), 8)
        // accept не ждёт дольше 5 с — чтобы сервер закрылся по времени, даже если к нему никто не подключался.
        server.soTimeout = 5_000
        socket = server
        url = "http://${ip.hostAddress}:${server.localPort}/$token"
        Thread({ acceptLoop(server) }, "phone-server").apply { isDaemon = true }.start()
        return url
    }

    fun stop() {
        try {
            socket?.close()
        } catch (_: Exception) {
        }
        socket = null
        pool.shutdownNow()
    }

    private fun acceptLoop(server: ServerSocket) {
        val deadline = System.currentTimeMillis() + LIFETIME_MS
        while (!server.isClosed) {
            if (System.currentTimeMillis() > deadline) {
                stop()
                onEvent("Страница для телефона закрылась: прошло 10 минут")
                break
            }
            val client = try {
                server.accept()
            } catch (e: java.net.SocketTimeoutException) {
                continue
            } catch (e: Exception) {
                break
            }
            if (!isLocal(client.inetAddress)) {
                client.close()
                continue
            }
            pool.execute { handle(client) }
        }
    }

    private fun handle(client: Socket) {
        client.use { s ->
            s.soTimeout = 15_000
            try {
                val input = BufferedInputStream(s.getInputStream())
                val out = s.getOutputStream()
                val requestLine = readLine(input) ?: return
                val parts = requestLine.split(' ')
                if (parts.size < 2) return
                val method = parts[0]
                val path = parts[1].substringBefore('?')
                val headers = HashMap<String, String>()
                while (true) {
                    val line = readLine(input) ?: return
                    if (line.isEmpty()) break
                    val i = line.indexOf(':')
                    if (i > 0) headers[line.substring(0, i).trim().lowercase()] = line.substring(i + 1).trim()
                }
                val segments = path.trim('/').split('/')
                if (segments.firstOrNull() != token) {
                    if (badTokens.incrementAndGet() >= MAX_BAD_TOKENS) {
                        stop()
                        onEvent("Страница для телефона закрыта: слишком много неверных запросов")
                    }
                    respond(out, 404, "text/plain; charset=utf-8", "Не найдено".toByteArray())
                    return
                }
                when {
                    method == "GET" && segments.size == 1 -> respond(out, 200, "text/html; charset=utf-8", page())
                    method == "GET" && segments.getOrNull(1) == "diag" -> {
                        onEvent("Диагностика скачана на телефон")
                        respond(
                            out, 200, "text/plain; charset=utf-8", repo.diagnostics(context).toByteArray(),
                            "Content-Disposition: attachment; filename=\"cupoftea-tv-diagnostics.txt\"",
                        )
                    }
                    method == "POST" && segments.getOrNull(1) in listOf("add", "dns") -> {
                        val length = headers["content-length"]?.toIntOrNull() ?: 0
                        if (length <= 0 || length > MAX_BODY) {
                            respond(out, 413, "text/plain; charset=utf-8", "Слишком большой текст".toByteArray())
                            return
                        }
                        val body = String(readExactly(input, length), Charsets.UTF_8)
                        val dns = segments[1] == "dns"
                        onEvent("Получено с телефона, обрабатываю…")
                        val message = runBlocking { repo.receive(context, body, dns) }
                        onEvent(message)
                        respond(out, 200, "text/plain; charset=utf-8", message.toByteArray())
                    }
                    else -> respond(out, 404, "text/plain; charset=utf-8", "Не найдено".toByteArray())
                }
            } catch (e: Exception) {
                Log.w(TAG, "запрос с телефона", e)
            }
        }
    }

    private fun page(): ByteArray = context.assets.open("phone/index.html").use { it.readBytes() }

    private fun respond(out: OutputStream, code: Int, type: String, body: ByteArray, extraHeader: String? = null) {
        val status = when (code) {
            200 -> "OK"; 404 -> "Not Found"; 413 -> "Payload Too Large"; else -> "Error"
        }
        val head = buildString {
            append("HTTP/1.1 $code $status\r\n")
            append("Content-Type: $type\r\n")
            append("Content-Length: ${body.size}\r\n")
            append("Cache-Control: no-store\r\n")
            append("X-Content-Type-Options: nosniff\r\n")
            append("Referrer-Policy: no-referrer\r\n")
            extraHeader?.let { append(it).append("\r\n") }
            append("Connection: close\r\n\r\n")
        }
        out.write(head.toByteArray(Charsets.UTF_8))
        out.write(body)
        out.flush()
    }

    private fun readLine(input: InputStream): String? {
        val buf = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b < 0) return if (buf.size() == 0) null else buf.toString("UTF-8")
            if (b == '\n'.code) break
            if (b != '\r'.code) buf.write(b)
            if (buf.size() > 8192) return null
        }
        return buf.toString("UTF-8")
    }

    private fun readExactly(input: InputStream, length: Int): ByteArray {
        val data = ByteArray(length)
        var read = 0
        while (read < length) {
            val n = input.read(data, read, length - read)
            if (n < 0) break
            read += n
        }
        return data.copyOf(read)
    }

    /** IPv4 адрес ТВ в домашней сети (физической: программа вне VPN, её сеть по умолчанию — Wi-Fi или кабель). */
    private fun lanAddress(): InetAddress? {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val network = cm.activeNetwork ?: return null
        val lp = cm.getLinkProperties(network) ?: return null
        return lp.linkAddresses.map { it.address }.firstOrNull { it is Inet4Address && !it.isLoopbackAddress }
    }

    /** Только локальные адреса: домашняя сеть, link-local, loopback. */
    private fun isLocal(a: InetAddress) = a.isSiteLocalAddress || a.isLinkLocalAddress || a.isLoopbackAddress ||
        (a.address.size == 16 && (a.address[0].toInt() and 0xFE) == 0xFC)
}
