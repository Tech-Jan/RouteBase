/**
 * Pure parsing of the grid filter expression grammars — the TypeScript mirror of the C#
 * `RouteBaseWeb/Helpers/GridFilterParsing.cs`. Keep the two in sync (same grammar + same test cases).
 *
 * Grammar:
 *   or-list    := term ("|" term)*                ; text: each term keeps *,? + contains fallback
 *   term       := ["!" | "!="] pattern            ; leading ! (or !=) negates the term (NOT-contains)
 *   range-expr := token (("," | "|") token)*
 *   token      := A "-" B   closed [A,B] | ">="N | ">"N | N"-"  (>=N) | "<="N | "<"N | "-"N  (<=N) | N (exact)
 *   N          := optional-sign digits ["." digits] ; "." decimal, "," is a separator
 */

export interface NumRange {
	min: number | null;
	max: number | null;
}

/**
 * Strips a leading negation operator (`!` or `!=`) off a text term. A negated term matches rows that do
 * NOT match the (wildcard) pattern; a bare `!` leaves an empty pattern (a no-op the callers drop). Mirrors
 * the C# `GridFilterParsing.StripNegation` — keep the two in sync.
 */
export function stripNegation(term: string): { negated: boolean; pattern: string } {
	let t = term;
	let negated = false;
	if (t.startsWith('!')) {
		negated = true;
		t = t.slice(1);
		if (t.startsWith('=')) t = t.slice(1); // accept both `!x` and `!=x`
	}
	return { negated, pattern: t.trim() };
}

/**
 * Parses a numeric range expression into (min,max) bounds (a null bound is open-ended). Tokens are
 * OR-separated by `,` or `|`. Invalid tokens (parse failure, min>max, fully unbounded) are dropped.
 */
export function parseRanges(input: string | null | undefined): NumRange[] {
	const result: NumRange[] = [];
	if (!input || !input.trim()) return result;

	for (const raw of input.split(/[,|]/)) {
		const token = raw.trim();
		if (!token) continue;
		const r = parseToken(token);
		if (r && isValid(r)) result.push(r);
	}
	return result;
}

function parseToken(t: string): NumRange | null {
	if (t.startsWith('>=')) return bound(t.slice(2), true);
	if (t.startsWith('<=')) return bound(t.slice(2), false);
	if (t.startsWith('>')) return bound(t.slice(1), true);
	if (t.startsWith('<')) return bound(t.slice(1), false);

	// "5-" → >=5
	if (t.length > 1 && t.endsWith('-')) return bound(t.slice(0, -1), true);

	// "-10" (leading '-', no further '-') → <=10. Negative lower bounds only appear in explicit ranges.
	if (t.length > 1 && t[0] === '-' && t.indexOf('-', 1) < 0) return bound(t.slice(1), false);

	// Closed range "A-B": split at the '-' whose left side parses as a number (A may be negative).
	for (let i = 1; i < t.length; i++) {
		if (t[i] !== '-') continue;
		const left = parseNum(t.slice(0, i));
		const right = parseNum(t.slice(i + 1));
		if (left !== null && right !== null) return { min: left, max: right };
	}

	const n = parseNum(t);
	return n !== null ? { min: n, max: n } : null;
}

function bound(s: string, lower: boolean): NumRange | null {
	const n = parseNum(s);
	if (n === null) return null;
	return lower ? { min: n, max: null } : { min: null, max: n };
}

function isValid(r: NumRange): boolean {
	if (r.min === null && r.max === null) return false;
	if (r.min !== null && r.max !== null && r.min > r.max) return false;
	return true;
}

function parseNum(s: string): number | null {
	const t = s.trim();
	// Match a plain float ('.' decimal, optional sign/exponent) — no thousands separators.
	if (!/^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(t)) return null;
	const n = Number(t);
	return Number.isFinite(n) ? n : null;
}
