/**
 * The replay payload → the rows of the results table. Pure: no DOM, no grid, no fetch.
 *
 * The table answers a different question from the leaderboard. The leaderboard (race-replay-overlay.ts) is live
 * and positional — "who is where RIGHT NOW". This is the finished record — "what did everyone do" — so
 * it is computed once per payload and never per tick.
 *
 * Two totals live side by side here, and they are not the same number:
 *
 *   accumulated — the sum of the rider's legs. What he is REPLAYED on, and simply his last split.
 *   official    — what the results file PUBLISHED.
 *
 * They disagree whenever a race compresses its own clock: Ski to Sea starts the next leg racer when the
 * previous one misses a cutoff, so two legs run concurrently and the published total is SHORTER than the
 * athletes' efforts actually took — 36 of 508 teams, by up to 2.3 h. The replay races the legs (see
 * RaceLegAccumulator), so the gap is surfaced rather than reconciled away.
 */

import type { RaceCheckpointDto, RaceParticipantDto } from './race-api';
import { must } from '../core/invariant';
import { specOf, type RaceFinishStatus } from './race-finish-status';

/**
 * How a rider's leg sum compares with his published finish.
 *
 *   over  — the legs sum PAST the published total: a forced start overlapped two legs, so the published
 *           clock is compressed. The common disagreement, and the only one Ski to Sea produces.
 *   under — the legs sum SHORT of it: time passed that no leg was counting — an untimed transition
 *           between legs. Absent from Ski to Sea (every diff there is positive) but real elsewhere.
 *   match — within tolerance. Nothing to say.
 */
export type RaceClockAgreement = 'over' | 'under' | 'match';

/** Seconds of disagreement below which the two clocks are telling the same story. */
export const CLOCK_TOLERANCE_SECONDS = 60;

/** One leg = the interval between two consecutive checkpoints. A leg needs no entity; it IS the gap. */
export interface RaceLegColumn {
	/** The checkpoint the leg ENDS at — the key its duration is derived from. */
	readonly checkpointId: number;
	/** The leg's own name if it has one, else the name of the checkpoint that ends it. */
	readonly label: string;
	readonly color: string | null;
}

/**
 * One entrant as the RESULTS see him: the payload row, plus where (if anywhere) he sits in the field.
 *
 * The roster is deliberately wider than the field. A DNS entrant is on the start list and in the results
 * table, and is on no part of the course — so he has a row and no `fieldIndex`. Before Phase 1 he was
 * dropped at import and never existed at all, which is how 113 of 1387 Schauinsland entrants vanished.
 */
export interface RaceRosterEntry {
	readonly participant: RaceParticipantDto;
	/** Index into `BuiltRace.field`/`meta`/`participants`, or null for an entrant who never takes the course. */
	readonly fieldIndex: number | null;
	readonly status: RaceFinishStatus;
}

export interface RaceResultRow {
	readonly participantId: number;
	/**
	 * Index into the field / meta arrays — what the map and the follow-camera key on. NULL for an
	 * entrant who is not in the field (a DNS): there is no dot to follow, paint or dim, and a number
	 * here would address the rider standing in that slot instead.
	 */
	readonly index: number | null;
	readonly name: string;
	readonly bib: string | null;
	readonly category: string | null;
	/** Which race within the event — eleven of them in one Schauinsland start list. */
	readonly discipline: string | null;
	readonly gender: string | null;
	readonly club: string | null;
	readonly isReal: boolean;
	/** The painted colour, or null for the default. */
	readonly color: string | null;
	/** Emphasised: bigger dot, drawn in front. The second presentation channel beside the colour. */
	readonly emphasised: boolean;
	/** The owner hid his call-out — no name label, no pinned row, no speed-chart line. */
	readonly calloutOff: boolean;

	/** Finishing position among riders whose status RANKS and who have a finish time. Null otherwise. */
	readonly position: number | null;
	/** Reached the finish checkpoint. A fact about his splits — the status is the fact about his RESULT. */
	readonly finished: boolean;
	/** What the file published, or the derived fallback. See race-finish-status.ts. */
	readonly status: RaceFinishStatus;
	/** The file's original status token — shown instead of a label when the status is `unknown`. */
	readonly statusRaw: string | null;

	/** The sum of his legs — what he is replayed on. Null if he never reached the finish. */
	readonly accumulatedSeconds: number | null;
	/** What the results file published. Null if it carried no total for him. */
	readonly officialSeconds: number | null;
	/** accumulated − official. Null unless BOTH exist — a delta against a missing number is not a delta. */
	readonly deltaSeconds: number | null;
	readonly agreement: RaceClockAgreement | null;

	/** Duration of each leg, index-aligned with the leg columns. Null where the leg cannot be derived. */
	readonly legSeconds: readonly (number | null)[];
}

/**
 * The leg columns of a race: one per checkpoint AFTER the start, in seq order.
 *
 * A race with no intermediate checkpoints yields exactly one column (start→finish), which is the
 * degenerate case working by itself rather than by a special path.
 */
export function buildLegColumns(checkpoints: readonly RaceCheckpointDto[]): RaceLegColumn[] {
	const bySeq = [...checkpoints].sort((a, b) => a.seq - b.seq);

	// A leg is named and coloured on the checkpoint it FOLLOWS, so the label of the leg ENDING at
	// bySeq[i] lives one row back — at bySeq[i - 1].
	return bySeq.slice(1).map((end, i) => {
		const prev = must(bySeq[i], 'slice(1) index i maps into bySeq');
		return {
			checkpointId: end.checkpointId,
			label: prev.legName ?? end.name ?? `CP ${end.seq}`,
			color: prev.legColor,
		};
	});
}

/**
 * Build every row of the table.
 *
 * Takes the field's OWN participant list (`BuiltRace.participants`), never `payload.participants` — and
 * that is the whole point of the parameter. `index` is what the table uses to drive the map: clicking a
 * row follows that rider, painting a filtered group recolours his dot, and both address `race.field` /
 * `race.meta` by index.
 *
 * `buildRace` COMPACTS the field — a rider it cannot place on the course is skipped — so a payload index
 * is NOT a field index the moment anyone is dropped, and every row from that rider on addressed the
 * person in front of the one it displayed. Passing the kept list makes the two the same array, so the
 * bug cannot come back: there is no longer an un-compacted list in scope to pass by mistake.
 */
/**
 * The checkpoint a finish is measured at: the end of the last leg. Null when the race has no leg at
 * all (fewer than two checkpoints) — with no line defined, nobody can be said to have missed it.
 */
export function finishCheckpointId(checkpoints: readonly RaceCheckpointDto[]): number | null {
	const legs = buildLegColumns(checkpoints);
	return legs.length > 0 ? must(legs[legs.length - 1], 'non-empty legs (guarded)').checkpointId : null;
}

/**
 * Did he reach the finish checkpoint, or stop short? The ONE definition — the results row, the table's
 * Status facet and the sidebar's label all read it, so the three cannot disagree about the same rider.
 *
 * A null `finishId` is TRUE, not false: with no line defined there is nothing to miss, so nobody is a
 * DNF. That also makes the Status facet uniform, which ships the column hidden — exactly what the
 * relevance rule in race-results-columns.ts wants. The two callers used to answer this opposite ways
 * (buildRace filled `true`, buildResultRows filled `false`) and each had a test pinning its own half;
 * the contract is "one definition", so it is stated here and nowhere else.
 */
export function reachedFinish(p: RaceParticipantDto, finishId: number | null): boolean {
	return finishId == null || p.splits.some((s) => s.checkpointId === finishId);
}

export function buildResultRows(
	roster: readonly RaceRosterEntry[],
	checkpoints: readonly RaceCheckpointDto[],
): RaceResultRow[] {
	const legs = buildLegColumns(checkpoints);
	const finishId = finishCheckpointId(checkpoints);

	const rows = roster.map(({ participant: p, fieldIndex, status }) => {
		const elapsed = elapsedByCheckpoint(p);
		const accumulated = finishId != null ? elapsed.get(finishId) ?? null : null;
		const official = p.officialFinishSeconds;

		// A delta needs BOTH numbers. Treating a missing official as 0 would report every DNF as a
		// two-hour overrun — a fabricated finding, and the loudest kind.
		const delta = accumulated != null && official != null ? accumulated - official : null;

		return {
			participantId: p.participantId,
			index: fieldIndex,
			name: p.name ?? p.bib ?? `#${p.participantId}`,
			bib: p.bib,
			category: p.category,
			discipline: p.discipline,
			gender: p.gender,
			club: p.club,
			isReal: p.kind === 'real',
			color: p.color,
			emphasised: p.emphasised === true,
			calloutOff: p.calloutOff === true,
			position: null as number | null,   // assigned below, once the field is ordered
			// Same predicate as `accumulated != null`: `elapsed` is built from p.splits, so "has a time
			// at the finish checkpoint" and "has a split row for it" are one fact, not two.
			finished: reachedFinish(p, finishId),
			status,
			statusRaw: p.finishStatusRaw,
			accumulatedSeconds: accumulated,
			officialSeconds: official,
			deltaSeconds: delta,
			agreement: delta == null ? null : agreementOf(delta),
			legSeconds: legDurations(legs, elapsed),
		};
	});

	return assignPositions(rows);
}

export function agreementOf(deltaSeconds: number): RaceClockAgreement {
	if (deltaSeconds > CLOCK_TOLERANCE_SECONDS) return 'over';
	if (deltaSeconds < -CLOCK_TOLERANCE_SECONDS) return 'under';
	return 'match';
}

/** Elapsed-from-start by checkpoint id. The splits are the model's anchors, so they are also the truth here. */
function elapsedByCheckpoint(p: RaceParticipantDto): Map<number, number> {
	return new Map(p.splits.map((s) => [s.checkpointId, s.elapsedSeconds]));
}

/**
 * A leg's duration is the difference between the elapsed times at its two ends. Both must be known: a
 * rider whose splits have a hole cannot have the leg that straddles it, and guessing one would invent a
 * number the results file never published.
 */
function legDurations(
	legs: readonly RaceLegColumn[], elapsed: ReadonlyMap<number, number>,
): (number | null)[] {
	let previousId: number | null = null;
	return legs.map((leg) => {
		const end = elapsed.get(leg.checkpointId);
		// The first leg starts at the line: everyone leaves at 0, so its "previous" elapsed is 0.
		const start = previousId == null ? 0 : elapsed.get(previousId);
		previousId = leg.checkpointId;
		return end != null && start != null ? end - start : null;
	});
}

/**
 * Finishers ranked by their accumulated time — the clock they are actually replayed on, so the table's
 * order matches what the map shows. A rider with no finish has no position; he is not last, he is
 * unplaced, and sorting him as "slowest" would be a claim the data does not make.
 *
 * A TIME is no longer sufficient on its own. A disqualified rider crossed the line and has a full set of
 * splits — and has no place, because that is what disqualified means. Same for hors classement and for
 * a finish outside the cutoff. The status table owns that call (`ranks`), so the vocabulary and the
 * ranking rule cannot drift apart.
 */
function assignPositions(rows: RaceResultRow[]): RaceResultRow[] {
	const ranked = rows
		.filter((r) => r.accumulatedSeconds != null && specOf(r.status).ranks)
		.sort((a, b) => (a.accumulatedSeconds ?? 0) - (b.accumulatedSeconds ?? 0)); // ?? 0 dead: nulls filtered above

	const positionOf = new Map<number, number>();
	ranked.forEach((r, i) => positionOf.set(r.participantId, i + 1));

	return rows.map((r) => ({ ...r, position: positionOf.get(r.participantId) ?? null }));
}
