// live-recording-uploader.ts — pure-testable buffered upload logic for the Record page.
//
// Extracted from live-recording.ts (LR-01..04) so the wedge/data-loss handling is unit-testable
// without geolocation/DOM. Owns the GPS buffer, the in-flight "frozen" batch, the client sequence,
// and all failure handling (400 poison/oversize, 404 session-inactive, 429/network retry, and the
// dedup `inserted:0` writer-conflict resync via the AQ-S1 lastSequence contract).

export interface RawGpsSample {
	lat: number;
	lng: number;
	altitude: number | null;
	speed: number | null;
	timestamp: number; // ms epoch (geolocation Position.timestamp)
}

export interface GpsPoint {
	lat: number;
	lng: number;
	altitude?: number;
	speed?: number;
	timestampUtc: string;
}

export interface PostResult {
	ok: boolean;
	status: number;
	inserted?: number | undefined;
	lastSequence?: number | undefined;
	errorText?: string | undefined;
}

export type PostFn = (seq: number, points: GpsPoint[]) => Promise<PostResult>;

export interface UploaderDeps {
	post: PostFn;
	now?: () => number;
	onInserted?: (n: number) => void; // advance the stored-point counter
	onStatus?: (msg: string) => void; // surface a rejection to the GPS-status line
	onSessionInactive?: () => void; // 404: session paused/stopped elsewhere
	maxBatch?: number; // server cap, default 500
	initialSequence?: number; // client sequence at page load (data-last-sequence)
}

/**
 * Sanitize a raw geolocation sample. Returns null to drop the point entirely
 * (unusable coordinates); nulls-out (drops) an implausible altitude/speed while
 * keeping the point; clamps a future timestamp to `nowMs` to defend the server's
 * future-skew rejection. Pure — no I/O.
 */
export function sanitizePoint(raw: RawGpsSample, nowMs: number): GpsPoint | null {
	const { lat, lng } = raw;
	if (!Number.isFinite(lat) || !Number.isFinite(lng)) return null;
	if (lat < -90 || lat > 90 || lng < -180 || lng > 180) return null;

	const point: GpsPoint = {
		lat,
		lng,
		timestampUtc: new Date(Math.min(raw.timestamp, nowMs)).toISOString(),
	};

	const alt = raw.altitude;
	if (alt != null && Number.isFinite(alt) && alt >= -500 && alt <= 10000) point.altitude = alt;

	const spd = raw.speed;
	if (spd != null && Number.isFinite(spd) && spd >= 0 && spd <= 150) point.speed = spd;

	return point;
}

export interface Uploader {
	enqueue(point: GpsPoint): void;
	flush(): Promise<void>;
	drainForStop(maxAttempts: number, waitMs: number, wait: (ms: number) => Promise<void>): Promise<boolean>;
	/** Frozen batch + next seq for a keepalive beacon on pagehide (LR-04). null if nothing to send. */
	snapshotForBeacon(cap?: number): { clientSequence: number; points: GpsPoint[] } | null;
	hasUnsent(): boolean;
	bufferedCount(): number;
	getClientSequence(): number;
	/** Re-arm after a cancelled stop (LR-02) — clears the halt flag set by a 404. */
	resume(): void;
}

export function createUploader(deps: UploaderDeps): Uploader {
	const maxBatch = deps.maxBatch ?? 500;

	const buffer: GpsPoint[] = [];
	let pendingBatch: GpsPoint[] = []; // the frozen in-flight batch; identical across retries
	let pendingSeq = 0;
	let clientSequence = deps.initialSequence ?? 0;
	let sendInFlight = false;
	let consecutive400 = 0;
	let chunkCap = maxBatch;
	let halted = false; // set on 404 (session no longer active) — stops the retry loop

	function remaining(): number {
		return pendingBatch.length + buffer.length;
	}

	function buildPending(): boolean {
		if (pendingBatch.length > 0) return true; // already frozen — resend identical
		if (buffer.length === 0) return false;
		const take = Math.min(buffer.length, chunkCap);
		pendingBatch = buffer.splice(0, take);
		pendingSeq = clientSequence + 1;
		return true;
	}

	async function flush(): Promise<void> {
		if (sendInFlight || halted) return;
		if (!buildPending()) return;
		sendInFlight = true;
		try {
			const res = await deps.post(pendingSeq, pendingBatch);
			if (res.ok) {
				clientSequence = Math.max(pendingSeq, res.lastSequence ?? pendingSeq);
				if ((res.inserted ?? 0) === 0 && pendingBatch.length > 0) {
					// Dedup / writer-conflict: this exact frozen batch is already stored (by us before a
					// post-commit timeout) or was stolen by a second recorder — indistinguishable, at most
					// one batch wide. Drop it and resync rather than resend forever (LR-03).
					if ((res.lastSequence ?? 0) > pendingSeq) {
						deps.onStatus?.('Sync conflict — another recorder active?');
					}
				} else {
					deps.onInserted?.(res.inserted ?? 0);
				}
				pendingBatch = [];
				consecutive400 = 0;
				chunkCap = maxBatch;
				return;
			}
			if (res.status === 400) {
				if (/too large/i.test(res.errorText ?? '')) {
					// Oversize (should not happen once chunked, but defensive): halve and re-chunk.
					chunkCap = Math.max(1, Math.floor(pendingBatch.length / 2));
					buffer.unshift(...pendingBatch);
					pendingBatch = [];
					return;
				}
				// Deterministic validation failure — a poison point survives resends. Drop after 3 tries
				// and surface it so the wedge is no longer silent (LR-01).
				consecutive400++;
				if (consecutive400 >= 3) {
					const dropped = pendingBatch.length;
					pendingBatch = [];
					consecutive400 = 0;
					deps.onStatus?.(`Upload rejected (${dropped} points dropped)`);
				}
				return;
			}
			if (res.status === 404) {
				halted = true;
				deps.onSessionInactive?.();
				return;
			}
			// 429 / 5xx / other — keep the frozen batch, retry next tick.
			return;
		} catch {
			// Network error — keep the frozen batch, retry next tick.
			return;
		} finally {
			sendInFlight = false;
		}
	}

	async function drainForStop(
		maxAttempts: number,
		waitMs: number,
		wait: (ms: number) => Promise<void>,
	): Promise<boolean> {
		for (let i = 0; i < maxAttempts; i++) {
			if (remaining() === 0) return true;
			if (halted) return false;
			if (sendInFlight) {
				await wait(waitMs); // an interval flush is still in flight — let it settle, don't no-op
				continue;
			}
			const before = remaining();
			await flush();
			// If nothing drained (429 within the 1 s limiter window, or a network blip), wait past the
			// limiter window before retrying the identical frozen batch.
			if (remaining() > 0 && remaining() >= before) await wait(waitMs);
		}
		return remaining() === 0;
	}

	function snapshotForBeacon(cap = 400): { clientSequence: number; points: GpsPoint[] } | null {
		if (pendingBatch.length > 0) return { clientSequence: pendingSeq, points: pendingBatch.slice(0, cap) };
		if (buffer.length > 0) return { clientSequence: clientSequence + 1, points: buffer.slice(0, cap) };
		return null;
	}

	return {
		enqueue(point) {
			buffer.push(point);
		},
		flush,
		drainForStop,
		snapshotForBeacon,
		hasUnsent: () => remaining() > 0,
		bufferedCount: remaining,
		getClientSequence: () => clientSequence,
		resume() {
			halted = false;
		},
	};
}
