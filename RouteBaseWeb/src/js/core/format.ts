/**
 * format.ts — Time / duration / size formatting helpers.
 *
 * Extracted from the old utils god-file (CB-01 dissolution). Pure functions, no I/O.
 */

/**
 * Format seconds into MM:SS or HH:MM:SS.
 */
export function formatTime(seconds: number): string {
	if (seconds == null || isNaN(seconds)) return "--:--";
	const totalSecs = Math.floor(Math.abs(seconds));
	const h = Math.floor(totalSecs / 3600);
	const m = Math.floor((totalSecs % 3600) / 60);
	const s = totalSecs % 60;
	if (h > 0) {
		return `${h}:${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}`;
	}
	return `${m}:${s.toString().padStart(2, "0")}`;
}

/**
 * `1:16:59` / `59:12` — an elapsed race/replay clock.
 *
 * Deliberately NOT formatTime: a negative or non-finite value here means "no time yet" (a rider who
 * has not started, a gap that does not exist) and must read as such, where formatTime's callers want
 * the magnitude of a signed delta. Two contracts, both pinned.
 */
export function formatElapsed(seconds: number): string {
	if (!Number.isFinite(seconds) || seconds < 0) return '—';
	const total = Math.round(seconds);
	const h = Math.floor(total / 3600);
	const m = Math.floor((total % 3600) / 60);
	const s = total % 60;
	const mm = h > 0 ? String(m).padStart(2, '0') : String(m);
	return `${h > 0 ? `${h}:` : ''}${mm}:${String(s).padStart(2, '0')}`;
}

/** `1.2 km` / `340 m` — how far back someone is. */
export function formatGap(metres: number): string {
	if (metres < 1) return '—';
	return metres >= 1000 ? `${(metres / 1000).toFixed(1)} km` : `${Math.round(metres)} m`;
}

/**
 * Format a byte count as KB / MB / GB (decimal units, matching what OS file managers report).
 */
export function formatBytes(bytes: number): string {
	if (bytes == null || isNaN(bytes) || bytes < 0) return "--";
	if (bytes < 1000) return `${Math.round(bytes)} B`;
	const kb = bytes / 1000;
	if (kb < 1000) return `${kb < 10 ? kb.toFixed(1) : Math.round(kb)} KB`;
	const mb = kb / 1000;
	if (mb < 1000) return `${mb < 10 ? mb.toFixed(1) : Math.round(mb)} MB`;
	return `${(mb / 1000).toFixed(1)} GB`;
}
