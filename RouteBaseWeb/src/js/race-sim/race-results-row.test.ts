// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import {
	buildLegColumns,
	buildResultRows,
	CLOCK_TOLERANCE_SECONDS,
	finishCheckpointId,
	reachedFinish,
	type RaceRosterEntry,
} from './race-results-row';
import { statusOf } from './race-finish-status';
import type { RaceCheckpointDto, RaceParticipantDto, RaceReplayPayload } from './race-api';

/** Start → XC Ski → Downhill → Finish: three legs, named and coloured on the checkpoint they follow. */
function checkpoints(): RaceCheckpointDto[] {
	return [
		{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: 'XC Ski', legColor: '#4dabf7' },
		{ checkpointId: 2, seq: 1, name: 'XC Ski finish', chainageMeters: 5108, node: 510, legName: 'Downhill', legColor: '#e8590c' },
		{ checkpointId: 3, seq: 2, name: 'Downhill finish', chainageMeters: 8504, node: 850, legName: 'Run', legColor: '#40c057' },
		{ checkpointId: 4, seq: 3, name: 'Finish', chainageMeters: 21517, node: 2151, legName: null, legColor: null },
	];
}

function participant(over: Partial<RaceParticipantDto> = {}): RaceParticipantDto {
	return {
		participantId: 1,
		kind: 'simulated',
		name: 'BIRCH EQUIPMENT',
		bib: '65',
		category: 'Competitive Open',
		gender: null,
		club: null,
		color: null,
		emphasised: false, calloutOff: false,
		officialFinishSeconds: null,
		finishStatus: null,
		finishStatusRaw: null,
		startOffsetSeconds: null,
		wave: null,
		discipline: null,
		seed: 1,
		splits: [],
		track: null,
		...over,
	};
}

function payload(participants: RaceParticipantDto[]): RaceReplayPayload {
	return {
		raceId: 1,
		raceName: 'Ski to Sea',
		course: { lat: [], lng: [], elevation: [], gradePercent: [], stepMeters: 10, totalMeters: 21517 },
		checkpoints: checkpoints(),
		participants,
		costCurve: null,
		costCurveMinBin: 0,
	};
}

/**
 * These fixtures build a whole payload; buildResultRows takes the ROSTER (see its doc — a payload index
 * is not a field index once buildRace drops anyone). These tests cover row CONTENT, not the field seam,
 * so wrapping each participant as an in-field roster entry here is exactly equivalent to what they
 * asserted before.
 */
const roster = (
	participants: readonly RaceParticipantDto[], cps: readonly RaceCheckpointDto[] = checkpoints(),
): RaceRosterEntry[] => {
	const finishId = finishCheckpointId(cps);
	return participants.map((p, fieldIndex) => ({
		participant: p, fieldIndex, status: statusOf(p, reachedFinish(p, finishId)),
	}));
};
const rowsOf = (p: RaceReplayPayload) => buildResultRows(roster(p.participants, p.checkpoints), p.checkpoints);

describe('buildLegColumns', () => {
	it('names each leg from the checkpoint it FOLLOWS, not the one it ends at', () => {
		const legs = buildLegColumns(checkpoints());

		// The leg ENDING at "XC Ski finish" is the XC Ski leg — its name is carried by the Start row.
		expect(legs.map((l) => l.label)).toEqual(['XC Ski', 'Downhill', 'Run']);
		expect(legs.map((l) => l.checkpointId)).toEqual([2, 3, 4]);
		expect(legs.map((l) => l.color)).toEqual(['#4dabf7', '#e8590c', '#40c057']);
	});

	it('falls back to the checkpoint name when the leg is unnamed', () => {
		const legs = buildLegColumns([
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
			{ checkpointId: 2, seq: 1, name: 'Finish', chainageMeters: 100, node: 10, legName: null, legColor: null },
		]);

		expect(legs).toEqual([{ checkpointId: 2, label: 'Finish', color: null }]);
	});

	it('a race with no intermediate checkpoints is ONE leg — the degenerate case, not a special path', () => {
		const legs = buildLegColumns([
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: 'The whole race', legColor: null },
			{ checkpointId: 2, seq: 1, name: 'Finish', chainageMeters: 40000, node: 4000, legName: null, legColor: null },
		]);

		expect(legs).toHaveLength(1);
		expect(must(legs[0], 'one leg').label).toBe('The whole race');
	});
});

describe('finishCheckpointId', () => {
	it('is the LAST checkpoint by seq — the end of the final leg', () => {
		expect(finishCheckpointId(checkpoints())).toBe(4);
	});

	it('follows seq, not array order — a reordered payload must not move the finish line', () => {
		expect(finishCheckpointId([...checkpoints()].reverse())).toBe(4);
	});

	it('is null when there is no leg at all — with no line defined, nobody can have missed it', () => {
		const single = checkpoints().slice(0, 1);

		expect(finishCheckpointId(single)).toBeNull();
		expect(finishCheckpointId([])).toBeNull();
	});
});

describe('reachedFinish', () => {
	it('is true only for a rider carrying a split AT the finish checkpoint', () => {
		const finisher = participant({ splits: [{ checkpointId: 4, elapsedSeconds: 20654 }] });
		const abandoned = participant({ splits: [{ checkpointId: 2, elapsedSeconds: 1269 }] });

		expect(reachedFinish(finisher, 4)).toBe(true);
		expect(reachedFinish(abandoned, 4)).toBe(false);
	});

	/**
	 * This used to assert FALSE, and buildRace's own copy of the rule asserted TRUE for the same race —
	 * two tests, each pinning one half of a contradiction. The intent is gone rather than violated: the
	 * contract is "one definition", which never held while there were two. True is the answer that keeps
	 * it: with no line defined nobody can be said to have missed it, and a field where everyone reads
	 * `finished` makes the Status facet uniform, which ships the column hidden instead of badging a whole
	 * race DNF.
	 */
	it('is true for everyone when the race has no finish checkpoint — no line, nothing to miss', () => {
		expect(reachedFinish(participant({ splits: [{ checkpointId: 2, elapsedSeconds: 10 }] }), null)).toBe(true);
		expect(reachedFinish(participant({ splits: [] }), null)).toBe(true);
	});

	it('gives buildResultRows the same answer buildRace gets — that is the whole point of the helper', () => {
		const single: RaceCheckpointDto[] = [
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
		];
		const p = participant({ splits: [{ checkpointId: 1, elapsedSeconds: 0 }] });

		const [row] = buildResultRows(roster([p], single), single);

		expect(must(row, 'one row').finished).toBe(reachedFinish(p, finishCheckpointId(single)));
		expect(must(row, 'one row').finished).toBe(true);
	});
});

describe('buildResultRows', () => {
	// The extraction of reachedFinish() must not have moved the line: `elapsed` is built from p.splits,
	// so "has a time at the finish" and "has a split row for it" have to stay the same fact. Scoped to a
	// race that HAS a finish checkpoint — without one the two diverge on purpose (finished, no time).
	it('keeps row.finished equivalent to having an accumulated time, once a finish line exists', () => {
		const rows = rowsOf(payload([
			participant({ participantId: 1, splits: [
				{ checkpointId: 2, elapsedSeconds: 1269 },
				{ checkpointId: 4, elapsedSeconds: 20654 },
			] }),
			participant({ participantId: 2, splits: [{ checkpointId: 2, elapsedSeconds: 1400 }] }),
			// A hole mid-race, but he still crossed the line: a finisher, not a DNF.
			participant({ participantId: 3, splits: [
				{ checkpointId: 2, elapsedSeconds: 1300 },
				{ checkpointId: 4, elapsedSeconds: 21000 },
			] }),
		]));

		expect(rows).toHaveLength(3);
		for (const r of rows) expect(r.finished).toBe(r.accumulatedSeconds != null);
		expect(rows.map((r) => r.finished)).toEqual([true, false, true]);
	});

	/**
	 * A TIME is no longer enough to hold a POSITION. A disqualified rider crossed the line and has a full
	 * set of splits — and has no place, because that is what disqualified means. Same for hors classement
	 * and for a finish outside the cutoff. Ranking him would publish a classification the event did not.
	 */
	it('gives no position to a rider whose status does not rank, however complete his splits', () => {
		const finish = [{ checkpointId: 4, elapsedSeconds: 20000 }];
		const rows = buildResultRows([
			{ participant: participant({ participantId: 1, splits: finish }), fieldIndex: 0, status: 'finished' },
			{ participant: participant({ participantId: 2, splits: finish }), fieldIndex: 1, status: 'dsq' },
			{ participant: participant({ participantId: 3, splits: finish }), fieldIndex: 2, status: 'notCompeting' },
			{ participant: participant({ participantId: 4, splits: finish }), fieldIndex: 3, status: 'otl' },
			{ participant: participant({ participantId: 5, splits: finish }), fieldIndex: 4, status: 'provisional' },
		], checkpoints());

		const positionOf = new Map(rows.map((r) => [r.participantId, r.position]));
		expect(positionOf.get(2)).toBeNull();
		expect(positionOf.get(3)).toBeNull();
		expect(positionOf.get(4)).toBeNull();
		// ...and the two that DO count are placed 1 and 2, not 1 and 5: an unranked rider is skipped, not
		// given a place and then hidden. He was never in the classification.
		expect([positionOf.get(1), positionOf.get(5)].sort()).toEqual([1, 2]);
	});

	it('carries the roster\'s status and raw token onto the row', () => {
		const [row] = buildResultRows([{
			participant: participant({ finishStatusRaw: 'DNS/DNF' }),
			fieldIndex: null,
			status: 'unknown',
		}], checkpoints());

		expect(must(row, 'one row').status).toBe('unknown');
		expect(must(row, 'one row').statusRaw).toBe('DNS/DNF');
		// He is not in the field, and the row says so rather than pointing at whoever is at index 0.
		expect(must(row, 'one row').index).toBeNull();
	});

	it('derives each leg duration from the gap between consecutive splits', () => {
		const rows = rowsOf(payload([
			participant({
				splits: [
					{ checkpointId: 2, elapsedSeconds: 1269 },   // XC Ski: 0 → 1269
					{ checkpointId: 3, elapsedSeconds: 2524 },   // Downhill: 1269 → 2524
					{ checkpointId: 4, elapsedSeconds: 4757 },   // Run: 2524 → 4757
				],
			}),
		]));

		expect(must(rows[0], 'row 0').legSeconds).toEqual([1269, 1255, 2233]);
		expect(must(rows[0], 'row 0').accumulatedSeconds).toBe(4757);   // the last split IS the accumulated finish
		expect(must(rows[0], 'row 0').finished).toBe(true);
	});

	it('the first leg starts at the line — everyone leaves at zero', () => {
		const rows = rowsOf(payload([
			participant({ splits: [{ checkpointId: 2, elapsedSeconds: 900 }] }),
		]));

		expect(must(rows[0], 'row 0').legSeconds[0]).toBe(900);
	});

	it('leaves a leg null when a split is missing, rather than inventing it', () => {
		const rows = rowsOf(payload([
			participant({
				splits: [
					{ checkpointId: 2, elapsedSeconds: 1000 },
					// no split at checkpoint 3 — the Downhill leg AND the Run leg both straddle the hole
					{ checkpointId: 4, elapsedSeconds: 5000 },
				],
			}),
		]));

		expect(must(rows[0], 'row 0').legSeconds).toEqual([1000, null, null]);
		expect(must(rows[0], 'row 0').accumulatedSeconds).toBe(5000);   // he still finished
	});

	// ── the two clocks ────────────────────────────────────────────────────────────────────────────

	it('flags a rider whose legs sum PAST his published finish (a forced start overlapped two legs)', () => {
		const rows = rowsOf(payload([
			participant({
				splits: [{ checkpointId: 4, elapsedSeconds: 39_000 }],
				officialFinishSeconds: 30_960,   // published 8.6 h; the legs say 10.8 h
			}),
		]));

		expect(must(rows[0], 'row 0').deltaSeconds).toBe(8040);
		expect(must(rows[0], 'row 0').agreement).toBe('over');
		expect(must(rows[0], 'row 0').accumulatedSeconds).toBe(39_000);   // replayed on the LEGS, not the published total
	});

	it('flags a rider whose legs sum SHORT of it (untimed dead time between legs)', () => {
		const rows = rowsOf(payload([
			participant({ splits: [{ checkpointId: 4, elapsedSeconds: 5000 }], officialFinishSeconds: 6000 }),
		]));

		expect(must(rows[0], 'row 0').agreement).toBe('under');
	});

	it('says nothing when the two clocks agree within tolerance', () => {
		const rows = rowsOf(payload([
			participant({
				splits: [{ checkpointId: 4, elapsedSeconds: 5000 }],
				officialFinishSeconds: 5000 - (CLOCK_TOLERANCE_SECONDS - 1),
			}),
		]));

		expect(must(rows[0], 'row 0').agreement).toBe('match');
	});

	it('reports NO delta when there is no published total — a DNF is not a two-hour overrun', () => {
		const rows = rowsOf(payload([
			participant({ splits: [{ checkpointId: 4, elapsedSeconds: 5000 }], officialFinishSeconds: null }),
		]));

		expect(must(rows[0], 'row 0').deltaSeconds).toBeNull();
		expect(must(rows[0], 'row 0').agreement).toBeNull();
	});

	// ── positions ─────────────────────────────────────────────────────────────────────────────────

	it('ranks finishers by their ACCUMULATED time — the clock they are replayed on', () => {
		const rows = rowsOf(payload([
			// Published totals would rank Slowcoach first. The legs say otherwise, and the legs win.
			participant({ participantId: 1, name: 'Slowcoach', splits: [{ checkpointId: 4, elapsedSeconds: 9000 }], officialFinishSeconds: 1000 }),
			participant({ participantId: 2, name: 'Winner', splits: [{ checkpointId: 4, elapsedSeconds: 4000 }], officialFinishSeconds: 4000 }),
		]));

		expect(rows.find((r) => r.name === 'Winner')!.position).toBe(1);
		expect(rows.find((r) => r.name === 'Slowcoach')!.position).toBe(2);
	});

	it('a rider with no finish is UNPLACED, not last', () => {
		const rows = rowsOf(payload([
			participant({ participantId: 1, name: 'Finisher', splits: [{ checkpointId: 4, elapsedSeconds: 4000 }] }),
			participant({ participantId: 2, name: 'DNF', splits: [{ checkpointId: 2, elapsedSeconds: 900 }] }),
		]));

		expect(rows.find((r) => r.name === 'Finisher')!.position).toBe(1);

		// Sorting him "slowest" would be a claim the data does not make: he did not go slowly, he stopped.
		const dnf = rows.find((r) => r.name === 'DNF')!;
		expect(dnf.position).toBeNull();
		expect(dnf.finished).toBe(false);
		expect(dnf.accumulatedSeconds).toBeNull();
	});

	it('carries the field index, so the table can drive the map without a second lookup', () => {
		const rows = rowsOf(payload([
			participant({ participantId: 10 }),
			participant({ participantId: 20, kind: 'real', color: '#ffd43b' }),
		]));

		expect(rows.map((r) => r.index)).toEqual([0, 1]);
		expect(must(rows[1], 'row 1').isReal).toBe(true);
		expect(must(rows[1], 'row 1').color).toBe('#ffd43b');
	});
});
