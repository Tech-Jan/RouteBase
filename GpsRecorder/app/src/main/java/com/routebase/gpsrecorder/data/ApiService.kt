package com.routebase.gpsrecorder.data

import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Path

// ── Request / Response models ──────────────────────────────────────────────

data class StartSessionRequest(
    val activityType: String,
    val visibility: String,
    val clientVersion: String,
    val timezoneOffsetMinutes: Int,
    // True when the session was started by always-on auto-detection (not a manual tap). The server
    // stamps source='auto-recording' + review_state='pending' so it lands in the web review queue.
    val autoDetected: Boolean = false
)

data class GpsPointDto(
    val lat: Double,
    val lng: Double,
    val altitude: Double?,
    val speed: Double?,
    // Raw barometric pressure (hPa); null on devices without a barometer. Maps to server
    // RecordingGpsPoint.Pressure → its own track blob channel.
    val pressure: Double?,
    val timestampUtc: String
)

data class AppendPointsRequest(
    val clientSequence: Int,
    val points: List<GpsPointDto>
)

// EVERY parameter defaulted, deliberately: Gson only runs these defaults when the class has a no-arg
// constructor, which Kotlin emits only when nothing is required. With `inserted` non-defaulted, Gson
// fell back to Unsafe instantiation and an absent `watched` landed on the JVM default FALSE — the
// exact inversion the fail-safe below exists to prevent. Pinned by AppendPointsResponseGsonTest.
data class AppendPointsResponse(
    val inserted: Int = 0,
    // AQ-S1: server's post-append LastSequence. Lets the client tell a dedup-eaten batch
    // (inserted == 0 because seq <= server LastSequence) from a genuinely stored one — delete
    // the rows only when lastSequence >= the seq we sent (AQ-04). Gson defaults 0 on older servers.
    val lastSequence: Int = 0,
    // Is anyone on the live view of this session right now? Drives the upload cadence: fresh points are
    // only worth a 5 s radio wake-up while someone is actually watching (for an auto-recording: never).
    //
    // DEFAULT true, NOT false — this is the fail-safe direction. An older server omits the field, and Gson
    // then uses this default: the phone keeps today's fast cadence rather than silently going idle and
    // making the live view lag for everyone. A wrong `true` costs battery; a wrong `false` breaks a feature.
    val watched: Boolean = true
)

// EVERY parameter defaulted, same rule as AppendPointsResponse above: with any required parameter
// Gson instantiates via Unsafe and an absent key lands NULL in a non-null field — the 409-adopt NPE
// path. A malformed row now yields a blank sessionId instead, which the adopt seams reject.
// Mirrors the server's RecordingSessionDto field-for-field (RecorderWireParityTests guards the sets).
data class SessionResponse(
    val sessionId: String = "",
    val trackId: Long = 0L,
    // Session status ("recording"/"paused"/…). The server sends it on EVERY session response
    // (RecordingSessionDto has no optional fields); blank means a malformed/pre-status payload.
    // AQ-08 consumers treat exactly "paused" specially (a paused session 404s appends).
    val status: String = "",
    val activityType: String = "",
    val visibility: String = "",
    val startedAt: String = "",
    val stoppedAt: String? = null,
    val lastPointAt: String? = null,
    val pointCount: Int = 0,
    // AQ-04: last server-accepted client sequence, so a reconnect/409-adopt resyncs clientSequence
    // instead of restarting from 0 (which the server dedup would silently eat).
    val lastSequence: Int = 0
)

// AND-24: response of the phone live-share mint. `url` is the RELATIVE "/s/{token}" path — the server never
// returns an absolute URL (its Host header is spoofable), so the app prefixes its configured server origin.
// All parameters defaulted for the same Gson no-arg-constructor rule as SessionResponse; the caller
// treats a blank url as a failed mint.
data class ShareLinkResponse(
    val url: String = "",
    val expiresAt: String? = null
)

// ── Retrofit interface ─────────────────────────────────────────────────────

interface ApiService {

    @POST("api/recording/sessions")
    suspend fun startSession(@Body request: StartSessionRequest): SessionResponse

    @POST("api/recording/sessions/{id}/points")
    suspend fun appendPoints(
        @Path("id") sessionId: String,
        @Body request: AppendPointsRequest
    ): AppendPointsResponse

    @POST("api/recording/sessions/{id}/pause")
    suspend fun pauseSession(@Path("id") sessionId: String): SessionResponse

    @POST("api/recording/sessions/{id}/resume")
    suspend fun resumeSession(@Path("id") sessionId: String): SessionResponse

    @POST("api/recording/sessions/{id}/stop")
    suspend fun stopSession(@Path("id") sessionId: String): SessionResponse

    /** Marks a session abandoned (server purges it later) — used when the user discards the upload queue. */
    @POST("api/recording/sessions/{id}/abandon")
    suspend fun abandonSession(@Path("id") sessionId: String): SessionResponse

    @GET("api/recording/sessions/active")
    suspend fun getActiveSessions(): List<SessionResponse>

    /**
     * AND-24: mint a PUBLIC live-share (/s/{token}) for the caller's OWN active session. Owner-authenticated
     * by the same X-Api-Key; the server hardcodes kind=live and a 24h TTL and only mints for an active,
     * owned session. Returns the relative share path to show/copy.
     */
    @POST("api/recording/sessions/{id}/share-live")
    suspend fun mintLiveShare(@Path("id") sessionId: String): ShareLinkResponse
}
