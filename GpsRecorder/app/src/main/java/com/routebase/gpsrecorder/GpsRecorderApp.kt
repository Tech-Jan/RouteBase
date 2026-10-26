package com.routebase.gpsrecorder

import android.app.Application
import android.util.Log
import androidx.room.withTransaction
import com.routebase.gpsrecorder.data.AppDatabase
import com.routebase.gpsrecorder.data.LegacyQueueMigration
import com.routebase.gpsrecorder.data.RecordingRepository
import com.routebase.gpsrecorder.data.Transactor
import com.routebase.gpsrecorder.service.GpsRecordingService
import com.routebase.gpsrecorder.util.EventLog
import com.routebase.gpsrecorder.util.PrefsManager
import com.routebase.gpsrecorder.work.UploadWorker
import com.google.gson.GsonBuilder
import kotlinx.coroutines.runBlocking
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.OkHttpClient
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import java.util.concurrent.TimeUnit

class GpsRecorderApp : Application() {

    lateinit var prefs: PrefsManager
        private set

    lateinit var repository: RecordingRepository
        private set

    // Single shared client — the interceptor reads the API key dynamically, so only the Retrofit
    // base URL needs rebuilding when credentials change (rebuilding the client leaks its pools).
    private val httpClient: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            // Stop endpoint awaits server-side finalization before responding — allow extra time.
            .readTimeout(90, TimeUnit.SECONDS)
            .writeTimeout(30, TimeUnit.SECONDS)
            .addInterceptor { chain ->
                val apiKey = prefs.apiKey
                if (BuildConfig.DEBUG) {
                    Log.d("GpsRecorder", "Request: ${chain.request().method} ${chain.request().url}")
                    Log.d("GpsRecorder", "API key present: ${apiKey != null}")
                }
                val request = chain.request().newBuilder()
                    .apply { apiKey?.let { addHeader("X-Api-Key", it) } }
                    .build()
                val response = chain.proceed(request)
                if (BuildConfig.DEBUG) {
                    Log.d("GpsRecorder", "Response: ${response.code} for ${request.url}")
                }
                response
            }
            // AND-13: NETWORK interceptor, deliberately — it runs per physical request, INCLUDING the
            // follow-ups OkHttp issues for redirects, which clone the application interceptor's headers
            // (OkHttp strips only Authorization on a cross-host redirect, never custom headers). Without
            // this, an open redirect on the reverse proxy would hand the long-lived key to any host a
            // 301/302 points at.
            .addNetworkInterceptor { chain ->
                val request = chain.request()
                val serverHost = prefs.serverUrl?.toHttpUrlOrNull()?.host
                if (serverHost == null || request.url.host == serverHost) {
                    chain.proceed(request)
                } else {
                    Log.w("GpsRecorder", "Stripping X-Api-Key: request to ${request.url.host} != $serverHost")
                    chain.proceed(request.newBuilder().removeHeader("X-Api-Key").build())
                }
            }
            .build()
    }

    override fun onCreate() {
        super.onCreate()
        EventLog.init(this)
        prefs = PrefsManager(this)
        rebuildApi()
        migrateLegacyQueue()
        sweepOrphanedPoints()
        // Background safety net: drains the upload queue on any network + recovers a kill mid-recording.
        UploadWorker.schedulePeriodic(this)
    }

    /**
     * v13 upload-queue migration. Deliberately blocking: Application.onCreate runs before any service,
     * receiver or activity in this process, so doing it here — and only here — guarantees nothing ever
     * observes half-migrated recording state (a receiver reading the old prefs while the queue adopts them
     * would start a "fresh" recording on top of the one being migrated). One boolean read once it has run;
     * a failure leaves the flag unset so the next launch retries.
     *
     * AND-42 (assessed 2026-07-31): the ANR concern does NOT apply here. This is one-shot per install-upgrade
     * to v13 — after it runs, [PrefsManager.queueMigrated] short-circuits it to a single boolean read on every
     * later launch, forever. The one blocking pass does a COUNT + at most one upsert + one owner-reassign over
     * a single un-uploaded recording's points (see LegacyQueueMigration); there is no armed-but-empty common
     * case to optimise away, unlike the sweep below. Backgrounding it would re-open the half-migrated-state
     * race the paragraph above exists to close. Verdict: accept as documented — do not move it off the main
     * thread.
     */
    private fun migrateLegacyQueue() {
        if (prefs.queueMigrated) return
        val db = AppDatabase.getInstance(this)
        val migration = LegacyQueueMigration(db.gpsPointDao(), db.pendingRecordingDao(), prefs)
        runCatching { runBlocking { migration.runAsync() } }
            .onFailure { Log.e("GpsRecorder", "Upload-queue migration failed; retrying next launch", it) }
    }

    /**
     * Reaps points orphaned by the park zero-point race — but only when [PrefsManager.clearSession] armed the
     * flag, i.e. after a recording pointer was dropped (the only moment an orphan row can be created).
     * Unguarded, this opened the DB and ran a write transaction on the MAIN thread at every process start:
     * every AR-receiver wake, every WorkManager wake, and the startForegroundService path — which must reach
     * startForeground() within ~5 s.
     *
     * Still blocking, like [migrateLegacyQueue] and for the same reason: onCreate runs before any service or
     * receiver, so reaping here guarantees the sweep sees a stable currentRecordingId and finishes before a
     * START_STICKY reconnect could add fresh rows. Moving it to a background dispatcher would re-open a
     * data-loss race (the sweep reads the OLD recording id, a new recording starts and enqueues points, then
     * the DELETE erases them). A failure leaves the flag armed, so the next launch retries.
     *
     * AND-42 (assessed 2026-07-31): moving this off the main thread is rejected for the data-loss reason
     * above. What WAS actionable: the flag is armed on every recording-pointer drop while a real orphan is
     * rare, so the common armed case reaped nothing yet still opened a write transaction on the critical
     * path. [RecordingRepository.sweepOrphanedPointsAsync] now does a COUNT (read) first and skips the
     * DELETE's transaction when there is nothing to reap, so an armed-but-empty launch costs a read, not a
     * write. The blocking guarantee stands; the wasted write is gone.
     */
    private fun sweepOrphanedPoints() {
        if (!prefs.orphanSweepPending) return
        runCatching { runBlocking { repository.sweepOrphanedPointsAsync() } }
            .onSuccess { prefs.orphanSweepPending = false }
            .onFailure { Log.w("GpsRecorder", "Orphan sweep failed", it) }
    }

    // Rebuilds the Retrofit-backed repository. Called on launch and whenever credentials change.
    fun rebuildApi() {
        // AQ-10: `repository` is read from IO coroutines while this runs on the main thread — swapping it
        // mid-recording has no happens-before guarantee and strands in-flight work on the old base URL/key.
        // Credentials only change on the setup screen, which is unreachable while recording (AU-05 also
        // guards Reset) — encode that invariant instead of half-fixing memory visibility. The initial
        // onCreate build (repository not yet initialized) must always proceed.
        if (::repository.isInitialized && (prefs.isCapturing || GpsRecordingService.isServiceActive)) return

        // Fall back to a placeholder base URL when unconfigured so `repository` is ALWAYS
        // initialized — no real requests fire until the user saves a server URL (isConfigured).
        val url = prefs.serverUrl?.trimEnd('/')
        val baseUrl = if (!url.isNullOrBlank()) "$url/" else "http://localhost/"

        val gson = GsonBuilder().create()
        val api = Retrofit.Builder()
            .baseUrl(baseUrl)
            .client(httpClient)
            .addConverterFactory(GsonConverterFactory.create(gson))
            .build()
            .create(com.routebase.gpsrecorder.data.ApiService::class.java)

        val db = AppDatabase.getInstance(this)
        repository = RecordingRepository(
            api,
            db.gpsPointDao(),
            db.pendingRecordingDao(),
            prefs,
            db.pendingAbandonDao(),
            object : Transactor {
                override suspend fun <R> transaction(block: suspend () -> R): R = db.withTransaction(block)
            },
            // ARCH-AND-B: the guard that used to live inside RecordingRepository as
            // `prefs.isCapturing || GpsRecordingService.isServiceActive` is supplied from here instead —
            // byte-identical logic, but the data layer no longer reaches UP into the service's statics.
            // GpsRecorderApp legitimately knows about the service (it also reads the same expression at :122).
            { prefs.isCapturing || GpsRecordingService.isServiceActive }
        )
    }
}
