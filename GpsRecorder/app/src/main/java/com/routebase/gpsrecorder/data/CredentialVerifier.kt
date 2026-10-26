package com.routebase.gpsrecorder.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

/** What a setup-time ping proved about the ENTERED (not yet persisted) server URL + API key. */
enum class CredentialVerdict {
    /** The server answered the authenticated endpoint with its contract — the credentials work. */
    OK,

    /** The server answered and REJECTED the key. Definitive: saving these credentials cannot help. */
    BAD_KEY,

    /** No RouteBase answer — offline, DNS/TLS failure, wrong URL, proxy page. The offline escape
     *  hatch ("save anyway") is legitimate here, because the key was never actually judged. */
    UNREACHABLE,
}

/**
 * B2/AND-07: verifies candidate credentials BEFORE they are persisted, so a typo'd key can no longer be
 * saved with queued rides behind it (the only way out was discarding the queue to reach setup again).
 *
 * Ping = `GET api/recording/sessions/active`, the cheapest existing authenticated endpoint (a tiny JSON
 * list, usually empty). Deliberately NOT [ApiService]/the app-wide client: its interceptors read the
 * STORED credentials (GpsRecorderApp attaches `prefs.apiKey` per request), so verifying unpersisted ones
 * through it is impossible. A throwaway client also keeps the check away from the upload queue and any
 * live recording state — it proves reachability + auth and nothing else.
 *
 * Like the old repository-side check, this deliberately does not ADOPT an active session it may see in
 * the response: a session with no local recording behind it has no recording id, so its points would
 * belong to nobody. A leftover server session is resolved by the next start's 409 conflict policy.
 */
object CredentialVerifier {

    /**
     * Pure verdict rule, unit-tested. `bodyPrefix` guards the 200 case: only the endpoint's own contract
     * (a JSON list) proves a RouteBase server — an https intermediary answering 200 with an HTML status
     * page must not validate a key it never checked.
     */
    fun verdictFor(code: Int, bodyPrefix: String): CredentialVerdict = when {
        code == 401 || code == 403 -> CredentialVerdict.BAD_KEY
        // A UTF-8 BOM (U+FEFF) is not whitespace, so trimStart() would leave it in front of the '[' — drop
        // leading whitespace AND any BOM, or a conformant server that prefixes its JSON with one reads as
        // UNREACHABLE (AND-43). The 0xFEFF code keeps this ASCII in source — no invisible literal.
        code in 200..299 && bodyPrefix.dropWhile { it.isWhitespace() || it.code == 0xFEFF }.startsWith("[") -> CredentialVerdict.OK
        else -> CredentialVerdict.UNREACHABLE
    }

    suspend fun verify(serverUrl: String, apiKey: String): CredentialVerdict = withContext(Dispatchers.IO) {
        // No redirect following: the app client strips the key on cross-host redirects (AND-13); this
        // one simply refuses — a redirect is not a RouteBase answer, and the key must not travel.
        val client = OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .followRedirects(false)
            .build()
        val request = Request.Builder()
            .url(serverUrl.trimEnd('/') + "/api/recording/sessions/active")
            .header("X-Api-Key", apiKey)
            .get()
            .build()
        try {
            client.newCall(request).execute().use { response ->
                // 64 bytes, not 8: enough for a leading BOM + a little whitespace to still leave the '['
                // inside the sample that verdictFor inspects (AND-43).
                verdictFor(response.code, response.peekBody(64).string())
            }
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (_: Exception) {
            CredentialVerdict.UNREACHABLE
        } finally {
            // One-shot client: release its threads/sockets now instead of waiting out their idle timers.
            client.dispatcher.executorService.shutdown()
            client.connectionPool.evictAll()
        }
    }
}
