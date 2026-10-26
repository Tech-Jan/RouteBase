// page-bootstrap.ts — the ONE reader for the Razor→TS JSON-island bridge.
//
// Six pages hand server data to their entry module through a `<script type="application/json" id="…">`
// island. Each page used to parse it by hand — five with a `?? '{}'` / `'[]'` / `''` idiom that silently
// defaulted, and one (activity-details-page.ts) with `JSON.parse(el.textContent ?? '')`, which THROWS on
// an empty island and took the whole Details page down instead of falling back. This is the single, tested
// reader they now share.

/**
 * Read a `<script type="application/json" id="…">` island and parse it. Returns `fallback` when the
 * element is missing, empty/whitespace, or holds malformed JSON — it never throws.
 *
 * Absent or empty islands are an expected, quiet state: a page with no rows renders no island (the Razor
 * `@if (Model…Any())` guard), so the reader must degrade silently there. Malformed JSON is NOT quiet — it
 * means the server emitted broken markup, so we log once before falling back. Swallowing a syntax error
 * would hide a real server-side bug behind an empty-looking page (absent ≠ broken).
 */
export function readIsland<T>(id: string, fallback: T): T {
	const raw = document.getElementById(id)?.textContent;
	if (raw == null || raw.trim() === '') return fallback;
	try {
		return JSON.parse(raw) as T;
	} catch (err) {
		console.error(`[page-bootstrap] #${id} holds malformed JSON — using fallback`, err);
		return fallback;
	}
}
