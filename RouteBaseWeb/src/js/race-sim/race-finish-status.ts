/**
 * What the results file said about an entrant's race — as filter options, a table cell and a sidebar
 * label. ONE table, five properties per status, and every consumer reads it.
 *
 * The vocabulary is NOT declared here: {@link RaceFinishStatus} comes out of the generated wire types
 * (`Services/Races/RaceFinishStatus.cs` → `[WireValues]` → an OpenAPI enum → a TS literal union). That is
 * the whole point of the type. `RACE_FINISH_STATUS` is a `Record` over that union, so adding a status
 * server-side is a COMPILE ERROR here until it is given a label, a badge and a ranking rule — where the
 * old boolean would have grown a silently dead filter value instead.
 *
 * This file used to be a boolean ("did he reach the line?"). Real timing data does not work that way:
 * Black Forest publishes four riders as DNF who also carry a finish-mat time, and every serious standard
 * separates "did not reach the line" from "reached it but does not count". See the C# file for the rest.
 *
 * Not to be confused with `Sample.finished`, which means "past his last time" — for an abandoned rider
 * that is where he STOPPED, not the line. This file is about the result.
 */

import { escapeHtml } from '../core/html';
import type { RaceParticipantDto } from './race-api';

/** The closed set, straight from codegen — never a hand-written copy. */
export type RaceFinishStatus = NonNullable<RaceParticipantDto['finishStatus']>;

export interface RaceFinishStatusSpec {
	/** The filter option's label, and the badge's tooltip. */
	readonly label: string;
	/** The follow sidebar's gap slot, which is three characters wide. */
	readonly short: string;
	/**
	 * The badge's modifier class, or null for NO badge. Blank is deliberate for the statuses that are
	 * unremarkable: 41 badges in an 864-row table read as data, 864 read as noise.
	 */
	readonly badgeClass: string | null;
	/** May he hold a finishing position? A DSQ has a time and no place — that is what DSQ means. */
	readonly ranks: boolean;
	/**
	 * Does he take the course at all? False only for a DNS: he is on the start list, so the results
	 * table shows him, but there is no rider to draw and no anchor to draw him from.
	 */
	readonly onCourse: boolean;
}

/**
 * The replay semantics, stated once.
 *
 * `onCourse` decides whether buildRace places him at all; `ranks` decides whether assignPositions can
 * give him a number. The middle group (`dnf`, `missingPunch`, `otl`) needs no special case in the model:
 * a rider whose splits stop early already gets fewer anchors, and the pacing model already holds him at
 * his last checkpoint rather than walking him to a line he never reached.
 */
export const RACE_FINISH_STATUS: Record<RaceFinishStatus, RaceFinishStatusSpec> = {
	finished: { label: 'Finished', short: 'fin', badgeClass: null, ranks: true, onCourse: true },
	provisional: { label: 'Provisional', short: 'prv', badgeClass: 'is-muted', ranks: true, onCourse: true },
	moved: { label: 'Moved up', short: 'mvd', badgeClass: 'is-muted', ranks: true, onCourse: true },
	dnf: { label: 'DNF', short: 'DNF', badgeClass: 'is-dnf', ranks: false, onCourse: true },
	missingPunch: { label: 'Missing punch', short: 'MP', badgeClass: 'is-dnf', ranks: false, onCourse: true },
	otl: { label: 'Over time limit', short: 'OTL', badgeClass: 'is-dnf', ranks: false, onCourse: true },
	dsq: { label: 'Disqualified', short: 'DSQ', badgeClass: 'is-dsq', ranks: false, onCourse: true },
	notCompeting: { label: 'Not competing', short: 'NC', badgeClass: 'is-muted', ranks: false, onCourse: true },
	dns: { label: 'DNS', short: 'DNS', badgeClass: 'is-dns', ranks: false, onCourse: false },
	// The file published a token we could not read. Ranking falls back to the derived rule (he ranks if
	// he has a time), because refusing to place him would be a claim the data never made.
	unknown: { label: 'Unknown', short: '?', badgeClass: 'is-muted', ranks: true, onCourse: true },
};

/** The order the filter offers statuses in — declaration order of the table, which is severity order. */
const STATUS_ORDER = Object.keys(RACE_FINISH_STATUS) as RaceFinishStatus[];

export const specOf = (status: RaceFinishStatus): RaceFinishStatusSpec => RACE_FINISH_STATUS[status];

/**
 * A participant's status: what the file PUBLISHED, else the derived rule.
 *
 * The published value wins, and that ordering is the fix for the whole bug class — a rider with a
 * finish-mat time and a published DNF is a DNF. Null means the file carried no status column at all,
 * which is every race imported before this existed, so they keep behaving exactly as they did.
 */
export function statusOf(p: RaceParticipantDto, reachedFinish: boolean): RaceFinishStatus {
	return p.finishStatus ?? (reachedFinish ? 'finished' : 'dnf');
}

/**
 * The filter's options, built from the statuses THIS race actually contains.
 *
 * The invariant this file exists to hold: offered values ≡ emitted values. A filter that silently
 * matches nothing is the failure mode, and offering all ten on a race where everyone finished is the
 * same dead control seen from the other side — which is also exactly what the results table's
 * `partitions()` relevance rule wants to see.
 */
export function finishStatusOptions(
	statuses: Iterable<RaceFinishStatus>,
): { value: RaceFinishStatus; label: string }[] {
	const present = new Set(statuses);
	return STATUS_ORDER.filter((s) => present.has(s)).map((s) => ({ value: s, label: RACE_FINISH_STATUS[s].label }));
}

/**
 * The Status column's cell. Blank for a status that carries no badge, and for `unknown` the file's OWN
 * token rather than a label we would be inventing — which is the entire reason the raw column is stored.
 */
export function finishStatusCell(status: RaceFinishStatus, raw: string | null = null): string {
	const spec = RACE_FINISH_STATUS[status];
	if (spec.badgeClass == null) return '';
	const text = status === 'unknown' && raw ? raw : spec.label;
	return `<span class="race-badge ${spec.badgeClass}" title="${escapeHtml(spec.label)}">${escapeHtml(text)}</span>`;
}

/** The follow sidebar's gap slot. Here, not there, so the table's wording and the sidebar's cannot drift. */
export const finishStatusShort = (status: RaceFinishStatus): string => RACE_FINISH_STATUS[status].short;
