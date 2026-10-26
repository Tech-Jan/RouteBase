/**
 * shape-filter/shape-summary.ts — builds the live legend sentence describing the current rules.
 *
 * Pure formatting: reads shapes + extent + preview counts, produces one human sentence.
 * e.g. "Efforts run G1 → G2, passing Z1 or Z2, staying inside Z3, avoiding Z4. 34 tracks · 61 efforts."
 */

import type { GateShape, MatchMode, Shape, ZoneShape } from './shape-model';
import { badgeLabel } from './shape-model';
import { DEFAULT_GATE_DEBOUNCE_SECONDS } from './rule-document';

export interface SummaryCounts {
	count: number;
	/** false = fast bbox estimate (may over-count); true = precise point-inside count. */
	exact: boolean;
	/** Total efforts across all tracks (≥ count under a gates extent); omitted before the exact pass. */
	effortCount?: number | undefined;
}

export function buildRuleSummary(
	shapes: readonly Shape[],
	matchMode: MatchMode,
	/** Pre-formatted adopted-filter clause ("distance 10-20 km, name morning"); '' when none. */
	filtersText: string,
	counts: SummaryCounts,
	/** Same-gate crossing debounce; spelled out in the gates clause when it is not the default. */
	gateDebounceSeconds: number = DEFAULT_GATE_DEBOUNCE_SECONDS,
): string {
	const zones = shapes.filter((s): s is ZoneShape => s.kind === 'zone');
	const gates = shapes.filter((s): s is GateShape => s.kind === 'gate');
	const badges = (list: readonly Shape[]): string[] => list.map((s) => badgeLabel(shapes, s.id));

	const clauses: string[] = [extentClause(matchMode, zones, gates, badges, gateDebounceSeconds)];

	const all = badges(zones.filter((z) => z.role === 'all'));
	if (all.length > 0) clauses.push(`passing through ${all.join(' and ')}`);

	const anyGroups = new Map<number, ZoneShape[]>();
	for (const z of zones) {
		if (z.role !== 'any') continue;
		const list = anyGroups.get(z.groupId) ?? [];
		list.push(z);
		anyGroups.set(z.groupId, list);
	}
	for (const list of anyGroups.values()) clauses.push(`passing ${badges(list).join(' or ')}`);

	const within = badges(zones.filter((z) => z.role === 'within'));
	if (within.length > 0) clauses.push(`staying inside ${within.join(', ')}`);

	const avoid = badges(zones.filter((z) => z.role === 'avoid'));
	if (avoid.length > 0) clauses.push(`avoiding ${avoid.join(', ')}`);

	const startsIn = badges(zones.filter((z) => z.role === 'start'));
	if (startsIn.length > 0) clauses.push(`starting in ${startsIn.join(' or ')}`);

	const endsIn = badges(zones.filter((z) => z.role === 'finish'));
	if (endsIn.length > 0) clauses.push(`finishing in ${endsIn.join(' or ')}`);

	const eitherIn = badges(zones.filter((z) => z.role === 'either'));
	if (eitherIn.length > 0) clauses.push(`starting or finishing in ${eitherIn.join(' or ')}`);

	// Adopted activities-table filters (pre-formatted upstream).
	if (filtersText) clauses.push(filtersText);

	const approx = counts.exact ? '' : '≈';
	const trackPart = `${approx}${counts.count} track${counts.count === 1 ? '' : 's'}`;
	const effortPart = counts.effortCount === undefined ? '' : ` · ${counts.effortCount} effort${counts.effortCount === 1 ? '' : 's'}`;

	return `${clauses.join(', ')}. ${trackPart}${effortPart}.`;
}

function extentClause(
	matchMode: MatchMode,
	zones: readonly ZoneShape[],
	gates: readonly GateShape[],
	badges: (list: readonly Shape[]) => string[],
	gateDebounceSeconds: number,
): string {
	if (matchMode === 'gates') {
		const starts = badges(gates.filter((g) => g.role === 'start' || g.role === 'both'));
		const finishes = badges(gates.filter((g) => g.role === 'finish' || g.role === 'both'));
		// The debounce silently merges crossings, so say so whenever it is not the default the
		// user never chose — a lap gate under-counting laps otherwise has no visible explanation.
		const gap = gateDebounceSeconds === DEFAULT_GATE_DEBOUNCE_SECONDS ? '' : ` (crossings ≥${gateDebounceSeconds}s apart)`;
		if (starts.length > 0 && finishes.length > 0) return `Efforts run ${starts.join('/')} → ${finishes.join('/')}${gap}`;
		return 'Efforts run between gates (draw a start and a finish)';
	}
	if (matchMode === 'clip') {
		const clipZones = badges(zones.filter((z) => z.role !== 'avoid'));
		return clipZones.length > 0 ? `Efforts run through ${clipZones.join(', ')}` : 'Efforts run through the clipped area';
	}
	return 'Efforts use the whole track';
}
