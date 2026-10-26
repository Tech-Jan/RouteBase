package com.routebase.gpsrecorder.data

/** What to do about the active server session that just 409'd our session creation. */
enum class ConflictAction {
    /** A recording in OUR upload queue owns it — deliver and stop that one, freeing the slot. */
    DELIVER_QUEUED,

    /** It is our own session, from a start request whose response never reached us — adopt it. */
    ADOPT,

    /** A dead session nobody claims, blocking this recording — close it and take the slot. */
    CLOSE_STALE,

    /** Someone else's business. Leave it alone, keep capturing locally, retry later. */
    BACK_OFF,
}

/**
 * The single rule for resolving "the server already has an active session" (HTTP 409).
 *
 * The hard case is an active session that is OLDER than this recording and that nothing in our queue
 * claims. Two very different things wear that shape:
 *
 *  - a GHOST — a discarded queue entry whose abandon never reached the server, or state this phone lost.
 *    It blocks every future recording until something closes it.
 *  - the user's OWN live session on ANOTHER device — the web /Record page, a second phone.
 *
 * The phone cannot tell them apart: [SessionResponse] carries no source or client version. So the tiebreak
 * is not about the session at all, it is about US — is a human present?
 *
 * A MANUAL start means the user just tapped Record, so closing a leftover is what they want. An
 * AUTO-DETECTED start is unattended: nobody asked for it, and stopping the session would also FINALIZE it
 * server-side — ending someone's in-progress ride mid-ride, with no prompt and no undo. An unattended
 * process must not guess, so it backs off. That costs nothing: capture continues into the local queue and
 * the recording is delivered later as its own activity, and the moment the other session ends, the next
 * upload tick creates ours normally.
 *
 * Pure — unit-tested without Android deps, like CooldownPolicy.
 */
object SessionConflictPolicy {

    fun decide(queuedOwnsIt: Boolean, startedBeforeUs: Boolean, autoDetected: Boolean): ConflictAction = when {
        // Ours, and finished: it must be delivered and stopped, or the live recording can never get a
        // session — and adopting it would append this ride to a FINISHED ride's activity.
        queuedOwnsIt -> ConflictAction.DELIVER_QUEUED
        // Started with (or after) this recording → it is our own in-flight session.
        !startedBeforeUs -> ConflictAction.ADOPT
        // Older, unclaimed, and we are not a human decision. See above.
        autoDetected -> ConflictAction.BACK_OFF
        else -> ConflictAction.CLOSE_STALE
    }
}
