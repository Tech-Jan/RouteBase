// style-filter-sanitizer.ts — null-guard the numeric filters shipped by external basemap styles.
//
// Vector basemap styles (OpenMapTiles / Liberty, etc.) ship layer filters that do typed numeric
// comparisons on per-feature properties — e.g. poi_r* `[">=",["get","rank"],20]`, boundary_3
// `[">=",["get","admin_level"],3]`. When a vector-tile feature carries that property as `null`,
// MapLibre's typed `>`/`>=`/`<`/`<=` throw inside the worker during bucket `populate`:
//   "Expected value to be of type number, but found null instead."
// The map still renders (the throwing feature is dropped), but the console fills with the error.
//
// Fix: wrap each `["get", prop]` operand of a numeric comparison as `["to-number", ["get",prop], -1]`.
// `to-number` with a fallback never throws on null; the sentinel -1 sits below every Liberty lower
// bound, so a null-`rank`/`admin_level` feature is cleanly excluded (the intended outcome). We apply
// this on every `style.load`, so it covers both raw-URL constructor loads and object style switches.
//
// Only the modern `["get", …]` expression form is transformed (what our basemaps ship). Legacy
// `[op, "key", val]` filters and non-numeric operators (`==`, `has`, `match`, `all`, …) pass through
// untouched. `maplibregl` is a page global (CDN). Sibling classes (e.g. fill-extrusion-height on a
// null render_height) can be guarded the same way here if they ever surface.

import type { FilterSpecification } from "maplibre-gl";

/** MapLibre comparison operators that assert a numeric operand and throw on null. */
const NUMERIC_OPS = new Set([">", ">=", "<", "<="]);

/** Value substituted for a null property — below every basemap lower bound, so nulls are excluded. */
const NULL_SENTINEL = -1;

export interface SanitizedFilter {
	/** The rewritten filter when `changed`, otherwise the original reference (identity preserved). */
	filter: unknown;
	/** True iff at least one `["get", …]` operand of a numeric comparison was wrapped. */
	changed: boolean;
}

/** A modern property lookup: `["get", "propName"]`. */
function isGetExpr(node: unknown): boolean {
	return Array.isArray(node) && node[0] === "get" && typeof node[1] === "string";
}

/**
 * Return a null-safe copy of a MapLibre filter expression. Pure — no MapLibre dependency, so it is
 * unit-testable against plain arrays. See file header for the why.
 */
export function sanitizeNumericFilterExpr(filter: unknown): SanitizedFilter {
	let changed = false;

	function walk(node: unknown): unknown {
		if (!Array.isArray(node) || node.length === 0) return node;
		const op = node[0];

		if (typeof op === "string" && NUMERIC_OPS.has(op)) {
			// Numeric comparison: guard each `["get", …]` operand; recurse into any other sub-expression.
			return node.map((operand, i) => {
				if (i === 0) return operand;
				if (isGetExpr(operand)) {
					changed = true;
					return ["to-number", operand, NULL_SENTINEL];
				}
				return walk(operand);
			});
		}

		// Non-numeric operator (all / any / == / match / case / …): recurse into its children.
		return node.map((child, i) => (i === 0 ? child : walk(child)));
	}

	const result = walk(filter);
	return { filter: changed ? result : filter, changed };
}

/**
 * Rewrite every null-intolerant numeric filter across a fetched style's layers, **in place**. For the
 * pre-`setStyle` fetch path (`fetchStyleJson`), so object-based style loads are already clean before
 * they reach the worker. The `style.load` installer below covers raw-URL/constructor loads instead.
 */
export function sanitizeStyleFilters(style: { layers?: unknown[] } | null | undefined): void {
	const layers = style?.layers;
	if (!Array.isArray(layers)) return;
	for (const layer of layers) {
		const l = layer as { filter?: unknown } | null;
		if (!l || l.filter === undefined) continue;
		const { filter, changed } = sanitizeNumericFilterExpr(l.filter);
		if (changed) l.filter = filter;
	}
}

/**
 * Register a `style.load` handler that rewrites every null-intolerant numeric filter in the loaded
 * style to its null-safe form. Uses `on` (not `once`) so it re-fires on every style switch. Mirrors
 * the per-map installer pattern of `installMissingImageFallback`.
 */
export function installStyleFilterSanitizer(map: maplibregl.Map): void {
	map.on("style.load", () => {
		const layers = map.getStyle()?.layers;
		if (!Array.isArray(layers)) return;
		for (const layer of layers) {
			const l = layer as { id?: string; filter?: unknown } | null;
			if (!l || l.id === undefined || l.filter === undefined) continue;
			const { filter, changed } = sanitizeNumericFilterExpr(l.filter);
			if (!changed) continue;
			try {
				map.setFilter(l.id, filter as FilterSpecification);
			} catch (e: unknown) {
				console.warn("[style-filter-sanitizer] setFilter failed for", l.id, e);
			}
		}
	});
}
