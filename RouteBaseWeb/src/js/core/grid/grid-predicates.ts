/**
 * Pure client-side matching helpers, mirroring the server's grid filter predicates.
 * Used by the in-memory data source (unpaged client grids) and shareable for any DOM matching.
 */

import type { NumRange } from './grid-filter-parse';
import { stripNegation } from './grid-filter-parse';

/** Converts a user wildcard pattern (`*`/`?`, case-insensitive) into a RegExp. No wildcard → contains. */
export function wildcardToRegex(pattern: string): RegExp {
	const hasWildcard = pattern.includes('*') || pattern.includes('?');
	// Escape regex metacharacters, then map the wildcards.
	const escaped = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&');
	const body = escaped.replace(/\*/g, '.*').replace(/\?/g, '.');
	return hasWildcard ? new RegExp(`^${body}$`, 'i') : new RegExp(body, 'i');
}

/** Compiled `|`-pattern. Memoized: grid filters evaluate the SAME pattern once per row (~700 rows on the
 *  big efforts table), so parsing + RegExp-compiling per call was pure rework. The regexes carry only the
 *  `i` flag (no `g`), so `.test` is stateless and sharing them is safe. */
interface CompiledTextPattern {
	positives: RegExp[];
	negatives: RegExp[];
}

const compiledPatterns = new Map<string, CompiledTextPattern>();

function compileTextPattern(pattern: string): CompiledTextPattern {
	const hit = compiledPatterns.get(pattern);
	if (hit) return hit;
	const positives: RegExp[] = [];
	const negatives: RegExp[] = [];
	for (const raw of pattern.split('|')) {
		const { negated, pattern: term } = stripNegation(raw.trim());
		if (!term) continue; // bare `!` / empty term → no constraint
		(negated ? negatives : positives).push(wildcardToRegex(term));
	}
	const compiled = { positives, negatives };
	if (compiledPatterns.size >= 256) compiledPatterns.clear(); // bound the memo; a recompile is cheap
	compiledPatterns.set(pattern, compiled);
	return compiled;
}

export function matchesText(value: string | null | undefined, pattern: string | undefined): boolean {
	if (!pattern || !pattern.trim()) return true;
	// `|`-separated alternatives (e.g. `*run|bike`); each term keeps wildcard/contains semantics and may be
	// negated with a leading `!` (or `!=`). Positives OR together; negatives are exclusions AND'd on top. A
	// NULL/empty value never matches a positive term but IS included by a negation (it can't contain the term).
	const { positives, negatives } = compileTextPattern(pattern);
	if (positives.length === 0 && negatives.length === 0) return true;

	const v = value == null ? null : String(value);
	const posOk = positives.length === 0 || (v !== null && positives.some((r) => r.test(v)));
	const negOk = negatives.every((r) => v === null || !r.test(v));
	return posOk && negOk;
}

/**
 * Multi-range match: true when the value falls in ANY range (open bounds allowed). Mirrors the server's
 * `WhereInRanges`. NULL never matches. Ranges are in the same unit as `value` (the caller scales if needed).
 */
export function matchesRanges(value: number | null | undefined, ranges: NumRange[] | undefined): boolean {
	if (!ranges || ranges.length === 0) return true;
	if (value == null) return false; // NULL never matches, mirroring SQL
	return ranges.some((r) => (r.min == null || value >= r.min) && (r.max == null || value <= r.max));
}

export function matchesSet(value: string | null | undefined, selected: string[] | undefined): boolean {
	if (!selected || selected.length === 0) return true;
	if (value == null) return false;
	return selected.includes(value);
}

/**
 * Combo match: true when the value matches the typed wildcard text OR is one of the checked options.
 * Mirrors the server's `ComboColumn` (text-LIKE OR exact-IN). An absent part imposes no constraint, so an
 * empty combo (no text, nothing checked) matches everything.
 */
export function matchesCombo(
	value: string | null | undefined,
	text: string | undefined,
	selected: string[] | undefined,
): boolean {
	const hasText = !!(text && text.trim());
	const hasSelected = !!(selected && selected.length);
	if (!hasText && !hasSelected) return true;
	return (hasSelected && matchesSet(value, selected)) || (hasText && matchesText(value, text));
}

/** Local calendar day (`YYYY-MM-DD`) of a date/datetime, or null when unparseable. */
function localDayString(value: Date | string): string | null {
	const d = value instanceof Date ? value : new Date(String(value));
	if (Number.isNaN(d.getTime())) return null;
	const m = String(d.getMonth() + 1).padStart(2, '0');
	const day = String(d.getDate()).padStart(2, '0');
	return `${d.getFullYear()}-${m}-${day}`;
}

/** Normalises a filter bound to a calendar day. Canonical `YYYY-MM-DD` is used verbatim (never
 *  epoch-parsed — `Date.parse` would anchor it to UTC midnight); anything else parses local. */
function boundDayString(bound: string): string | null {
	const t = bound.trim();
	if (/^\d{4}-\d{2}-\d{2}$/.test(t)) return t;
	return localDayString(t);
}

/**
 * Date-range match on **local calendar days** — the same day the date column displays
 * (`formatDateDMY` reads local components), and the same semantics as the server's naive
 * DateTime compare. Comparing epochs instead would anchor date-only bounds to UTC midnight and
 * mis-filter boundary-day rows in every non-UTC timezone. Both bounds inclusive; an
 * unparseable bound imposes no constraint.
 */
export function matchesDateRange(
	value: Date | string | null | undefined,
	from: string | undefined,
	to: string | undefined,
): boolean {
	if ((!from || !from.trim()) && (!to || !to.trim())) return true;
	if (value == null) return false;
	const day = localDayString(value);
	if (day == null) return false;
	const fromDay = from && from.trim() ? boundDayString(from) : null;
	const toDay = to && to.trim() ? boundDayString(to) : null;
	if (fromDay != null && day < fromDay) return false;
	if (toDay != null && day > toDay) return false;
	return true;
}
