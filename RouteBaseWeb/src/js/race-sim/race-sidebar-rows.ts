/**
 * The follow sidebar's rows as pure data — the WHOLE field in true race-position order. Extracted from
 * race-replay-view so the position/gap maths (the silently-regressing kind: rank off-by-one, gap sign)
 * is testable without a DOM mount. The overlay virtualizes the rows; the view just hands them over.
 */

import type { RaceField } from './race-field';
import { must } from '../core/invariant';
import type { RaceParticipantDto } from './race-api';
import { specOf, type RaceFinishStatus } from './race-finish-status';
import type { DnfStanding } from './race-dnf-standings';

/** One rider's row in the follow sidebar — the whole ordered field is handed over; the list virtualizes it. */
export interface SidebarRow {
	readonly index: number;
	/**
	 * The number shown on the row. His live race position while he is riding — and, once he has stopped
	 * without a place, the position he held AT THAT MOMENT, which then never moves again.
	 */
	readonly position: number;
	/** True when `position` is the frozen one. The row draws it faded: it is a memory, not a standing. */
	readonly positionFrozen: boolean;
	readonly name: string;
	readonly gapMetres: number;
	readonly isReal: boolean;
	/** Emphasised by hand — pinned above the field alongside the real riders. See RaceParticipant.Emphasised. */
	readonly emphasised: boolean;
	/** The owner hid this rider's call-out: he is not pinned, whichever of the two earned it. */
	readonly calloutOff: boolean;
	/**
	 * He is no longer moving — the playhead is past his last time. NOT "he finished": for an abandoned
	 * rider that last time is where he QUIT. Pair it with {@link SidebarRow.status} to tell the two apart.
	 */
	readonly stopped: boolean;
	/** His published-or-derived result. Static for the whole replay; `stopped` is what times its display. */
	readonly status: RaceFinishStatus;
	/**
	 * Which number he was to stop with this status — 1 for the first DNF, 2 for the second. Null for a
	 * rider who ranks, and for one still riding. Rendered beside the label: `DNF 2`.
	 */
	readonly statusOrdinal: number | null;
}

/**
 * The three states a rider can be in, in list order. The number IS the order — lower sits higher up.
 *
 * `field.finished` alone cannot tell FINISHED from ABANDONED: it means "past his last time", and an
 * abandoned rider is that too. Only his status separates them, which is why this lives here rather than in
 * RaceField.order() — the field knows timing and geometry, not what a result means.
 */
const FINISHED = 0;
const RIDING = 1;
const ABANDONED = 2;

function groupOf(field: RaceField, statuses: readonly RaceFinishStatus[], i: number): number {
	if (field.finished[i] !== 1) return RIDING;
	return specOf(statuses[i] ?? 'finished').ranks ? FINISHED : ABANDONED;
}

/**
 * Race order: who is where, right now.
 *
 * Chainage decides it only for riders still OUT THERE. For everyone else it is the wrong question, in two
 * different ways:
 *
 *   finishers — a real rider's chainage is a DTW projection accurate to tens of metres at the ends
 *               (RaceRealParticipantService's own test admits ±40 m) while every simulated finisher is
 *               parked on the exact finish node, so ordering a finished field by chainage put the one
 *               rider whose data is REAL below all 823 whose position is modelled. His TIME places him,
 *               which is what "position" means in a result list. Equal times share a place — two riders
 *               can hold 824th, and that is a fact about the race, not a defect.
 *   abandoned — his marker is parked where he quit, so chainage kept him wedged among riders who are
 *               still racing, drifting down as they passed him. He is not in the race any more; he goes
 *               to the BOTTOM, in the order they dropped out — which is the order their DNF numbers run
 *               in, so the block reads DNF 1, DNF 2, DNF 3 down the page.
 *
 * A rider whose result is a DNF but who has not reached his abandon moment yet is still RIDING, and is
 * ordered with the rest of the field. The bottom block fills up as the replay runs, one rider at a time.
 */
function raceOrder(field: RaceField, statuses: readonly RaceFinishStatus[]): number[] {
	return Array.from({ length: field.size }, (_, i) => i).sort((a, b) => {
		const group = groupOf(field, statuses, a);
		const other = groupOf(field, statuses, b);
		if (group !== other) return group - other;
		if (group === RIDING) return (field.chainages[b] ?? NaN) - (field.chainages[a] ?? NaN);
		// Both settled: their own clock orders them — finishing order, or the order they dropped out.
		return (field.ends[a] ?? 0) - (field.ends[b] ?? 0);
	});
}

/**
 * The ordered field as sidebar rows: rank from raceOrder(), gap = metres behind the leader (clamped ≥ 0).
 *
 * `dnfStandings` is what stops an abandoned rider's number from climbing for the rest of the replay — it
 * is precomputed and static, never captured from the frames going past. See race-dnf-standings.
 */
export function buildSidebarRows(
	field: RaceField,
	participants: readonly RaceParticipantDto[],
	statuses: readonly RaceFinishStatus[],
	dnfStandings: ReadonlyMap<number, DnfStanding> = new Map(),
): SidebarRow[] {
	const order = raceOrder(field, statuses);
	const leadIdx = order[0];
	const lead = leadIdx !== undefined ? field.chainages[leadIdx] ?? 0 : 0;
	return order.map((index, rank) => {
		const p = must(participants[index], 'order() indices map into participants');
		const stopped = field.finished[index] === 1;
		// Only once he has actually stopped: until then he is riding, and his live position is the true one.
		const frozen = stopped ? dnfStandings.get(index) : undefined;
		return {
			index,
			position: frozen?.position ?? rank + 1,
			positionFrozen: frozen != null,
			name: p.name ?? p.bib ?? '—',
			gapMetres: Math.max(0, lead - (field.chainages[index] ?? NaN)),
			isReal: p.kind === 'real',
			emphasised: p.emphasised === true,
			calloutOff: p.calloutOff === true,
			stopped,
			// `?? 'finished'` is unreachable — statuses is field-aligned by construction in buildRace —
			// and is the arm noUncheckedIndexedAccess demands rather than a `!`.
			status: statuses[index] ?? 'finished',
			statusOrdinal: frozen?.ordinal ?? null,
		};
	});
}
