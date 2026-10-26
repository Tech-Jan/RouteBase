/**
 * html.ts — Small DOM/HTML string helpers.
 *
 * Extracted from the old utils god-file (CB-01 dissolution).
 */

/**
 * Escape a string for safe insertion into HTML. Canonical escaper — the sole
 * implementation under src/js (CR2-01 merged the core/grid twin into this body
 * verbatim; core/grid/grid-view.ts re-exports it).
 *
 * Contract:
 * - Safe for HTML **text** and **double-quoted attribute value** contexts (`&<>"'` all escaped).
 * - `'` is escaped too, so single-quoted attributes are also safe — but generated attributes
 *   in this codebase must stay double-quoted regardless (gate-grepped, see G4/G5 below).
 * - Backtick is deliberately NOT escaped: it is not an HTML attribute delimiter in any
 *   spec-compliant parser (only a legacy IE≤8 unquoted-attribute quirk), and unquoted
 *   attribute interpolation is banned by G5.
 * - NOT for URL context (use `encodeURIComponent`) or CSS/JS contexts (use app
 *   constants/palettes, never raw interpolation).
 * - Null-safe: `null`/`undefined` → `""`.
 *
 * Standing guard greps (CR2-03; must be empty — G2-G5 pre-existing, re-run at S7 closeout):
 * ```bash
 * # G1 exactly one escaper implementation (this file)
 * grep -rn "function escapeHtml" RouteBaseWeb/src/js --include="*.ts" | grep -v "core/html.ts"
 * # G2 no hand-rolled escaper bodies outside the canonical one
 * grep -rn "replace(/&/g" RouteBaseWeb/src/js --include="*.ts" | grep -v "core/html.ts"
 * # G3 no local esc/escHtml definitions (dashboard-shared's alias is a re-export line, doesn't match)
 * grep -rnE "function esc[A-Za-z]*\(|const esc[A-Za-z]* =" RouteBaseWeb/src/js --include="*.ts" | grep -v "core/html.ts"
 * # G4 no single-quoted attribute interpolation in template literals
 * grep -rn "='\${" RouteBaseWeb/src/js --include="*.ts"
 * # G5 no unquoted attribute interpolation in template literals (tag-scoped: the attr must sit
 * #    inside an open <tag ...> after whitespace, so URL query strings like `?tier=${t}` and
 * #    query params inside quoted attrs (href="...?id=${id}") don't false-positive)
 * grep -rnE '<[^>]*\s[a-zA-Z-]+=\$\{' RouteBaseWeb/src/js --include="*.ts"
 * ```
 * Review guards (not emptiness gates — check when reviewing new UI code): `onclick=` in
 * `Pages/**\/*.cshtml` (GL-01 ban) · `Replace("'"` in `Pages/` · any new `innerHTML`/`setHTML`/
 * `insertAdjacentHTML` sink in a diff — check interpolants against the escaper contract above.
 */
export function escapeHtml(s: string | null | undefined): string {
	if (s == null) return "";
	return String(s).replace(
		/[&<>"']/g,
		(c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c] as string,
	);
}

/**
 * Get a ranking badge HTML string.
 */
export function getRankingBadge(rank: number): string {
	if (rank === 1) return '<span class="badge bg-warning text-dark">🥇 #1</span>';
	if (rank === 2) return '<span class="badge bg-secondary">🥈 #2</span>';
	if (rank === 3) return '<span class="badge bg-secondary">🥉 #3</span>';
	return `<strong>#${rank}</strong>`;
}
