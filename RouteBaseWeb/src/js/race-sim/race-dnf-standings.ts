/**
 * What a rider who never finished is left holding: the position he had WHEN HE STOPPED, and which number
 * he was to stop.
 *
 * The follow sidebar orders non-finishers by chainage, so an abandoned rider's row number kept climbing
 * for the rest of the replay as the live field rode past his parked marker — by the end it read like a
 * finishing place he never had, and a worse one the longer the race went on. His race ended when he
 * stopped, so his number should too.
 *
 * Both values are STATIC for a build. Neither is captured live, and that is the point: watching for the
 * moment a rider stops only works if the viewer happens to be playing through it, so a seek to the end
 * would have frozen every rider at his end-of-race rank — exactly the number this exists to avoid. Both
 * are instead derived from `field.ends`, which the RaceField constructor already computed.
 */

import type { RaceField } from './race-field';
import { specOf, type RaceFinishStatus } from './race-finish-status';

export interface DnfStanding {
	/** His position in the field at the instant he stopped. Frozen — it never sinks afterwards. */
	readonly position: number;
	/**
	 * The nth rider to stop with THIS status, in the order they dropped out: DNF 1, DNF 2, DSQ 1. Counted
	 * per status because the number is rendered next to the status label, where a shared counter would
	 * read as "the 4th DNF" on a rider who is the first DSQ.
	 */
	readonly ordinal: number;
}

/**
 * Standings for every rider whose status carries no place (see RACE_FINISH_STATUS.ranks) — DNF, DSQ, OTL,
 * missing punch, not competing. Riders who rank are absent from the map; so is a rider still riding.
 *
 * ⚠ This SAMPLES the field: it advances it to each abandon time in turn and leaves it parked at the last
 * one. Call it at build time, before anything renders — `buildRace` does, and the replay's first
 * `advanceTo` overwrites the leftover state anyway.
 *
 * Cost is one pass per abandoning rider, once per build: ~40 abandons in an 865-rider field is under
 * 70k comparisons total, against the O(P·logP) sort the sidebar already runs 5× a second.
 */
export function computeDnfStandings(
	field: RaceField, statuses: readonly RaceFinishStatus[],
): Map<number, DnfStanding> {
	const standings = new Map<number, DnfStanding>();

	const quitters: number[] = [];
	for (let i = 0; i < field.size; i++) {
		if (!specOf(statuses[i] ?? 'finished').ranks) quitters.push(i);
	}
	// Ascending, which is both the numbering order AND what keeps the sampling cheap: advanceTo only ever
	// walks forward, so no cursor in the field is ever reset.
	quitters.sort((a, b) => (field.ends[a] ?? 0) - (field.ends[b] ?? 0));

	const countByStatus = new Map<RaceFinishStatus, number>();
	for (const index of quitters) {
		field.advanceTo(field.ends[index] ?? 0);

		// Rank by chainage, the same rule the sidebar orders live riders by — so the frozen number means
		// what the number above it means, rather than being a second definition of "position".
		const mine = field.chainages[index] ?? Number.NEGATIVE_INFINITY;
		let ahead = 0;
		for (let j = 0; j < field.size; j++) {
			if ((field.chainages[j] ?? Number.NEGATIVE_INFINITY) > mine) ahead++;
		}

		const status = statuses[index] ?? 'dnf';
		const ordinal = (countByStatus.get(status) ?? 0) + 1;
		countByStatus.set(status, ordinal);
		standings.set(index, { position: ahead + 1, ordinal });
	}

	return standings;
}
