// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import { buildRace } from './race-build';
import { buildResultRows } from './race-results-row';
import type { RaceCheckpointDto, RaceParticipantDto, RaceReplayPayload } from './race-api';

/**
 * The results table drives the map: clicking a row follows that rider, and painting a filtered group
 * recolours his dot. Both address the field BY INDEX — so a row's `index` must be the rider's index in
 * `race.field` / `race.meta`, or every one of those actions lands on the wrong person.
 *
 * It was the index in `payload.participants` instead. `buildRace` COMPACTS the field: a rider it cannot
 * place on the course (no usable anchor) is skipped with a `continue`, and the field is one shorter. From
 * that rider on, every row addressed the person in front of the one it displayed — silently. The last row
 * addressed nobody at all.
 *
 * `BuiltRace.indexByParticipantId` was built for exactly this translation and was read by nothing.
 */

const FINISH_ID = 2;

function checkpoints(): RaceCheckpointDto[] {
	return [
		{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: 'Leg', legColor: null },
		{ checkpointId: FINISH_ID, seq: 1, name: 'Finish', chainageMeters: 1000, node: 100, legName: null, legColor: null },
	];
}

function rider(participantId: number, name: string, finishSeconds: number): RaceParticipantDto {
	return {
		participantId,
		kind: 'simulated',
		name,
		bib: null,
		category: null,
		gender: null,
		club: null,
		color: null,
		emphasised: false, calloutOff: false,
		officialFinishSeconds: finishSeconds,
		finishStatus: null,
		finishStatusRaw: null,
		startOffsetSeconds: null,
		wave: null,
		discipline: null,
		seed: participantId,
		splits: [{ checkpointId: FINISH_ID, elapsedSeconds: finishSeconds }],
		track: null,
	};
}

/** A flat 1 km course: 101 nodes at 10 m. */
function payload(participants: RaceParticipantDto[]): RaceReplayPayload {
	const n = 101;
	return {
		raceId: 1,
		raceName: 'Ski to Sea',
		course: {
			lat: Array.from({ length: n }, () => 47.9),
			lng: Array.from({ length: n }, (_, i) => 8.1 + i * 0.0001),
			elevation: Array.from({ length: n }, () => 500),
			gradePercent: Array.from({ length: n }, () => 0),
			stepMeters: 10,
			totalMeters: 1000,
		},
		checkpoints: checkpoints(),
		participants,
		costCurve: null,
		costCurveMinBin: 0,
	};
}

describe('results-row index ↔ race field index', () => {
	it('addresses the rider it displays, even when the field drops someone', () => {
		// GHOST's only time is 0 s — a DNS placeholder, which `ParseElapsedSeconds` reads as a real 0 and
		// the importer stores. `anchorsFor` drops a split at or before the gun, so he has no second anchor
		// and `buildRace` cannot place him: the field is 2 long, the payload is 3.
		const p = payload([
			rider(10, 'ALPHA', 600),
			rider(11, 'GHOST', 0),
			rider(12, 'OMEGA', 900),
		]);

		const race = buildRace(p);
		const rows = buildResultRows(race.roster, p.checkpoints);

		expect(race.field.size).toBe(2);
		// He is still on the START LIST — the table shows all three. What he does not have is an index.
		expect(rows).toHaveLength(3);
		expect(rows.find((r) => r.name === 'GHOST')?.index).toBeNull();

		// The invariant, stated directly: the rider a row ADDRESSES is the rider it SHOWS.
		for (const row of rows) {
			if (row.index == null) continue;
			expect(race.meta[row.index]).toBeDefined();
			expect(must(race.meta[row.index], 'row.index maps into meta').name).toBe(row.name);
			// And nobody is addressed off the end of the field — that threw "Invalid LngLat" in the rAF loop.
			expect(row.index).toBeLessThan(race.field.size);
		}
	});

	// Same hazard, second array. `statuses` is built in the keep loop precisely so it is compacted
	// with everything else — an index-aligned array built anywhere ELSE would describe the payload's
	// riders, and the sidebar would hand one man's DNF label to the man in front of him.
	it('compacts the status array with the field, so it describes the rider at the same index', () => {
		const MID_ID = 3;
		const abandoned = rider(13, 'QUIT', 700);
		abandoned.splits = [{ checkpointId: MID_ID, elapsedSeconds: 300 }];   // halfway, then nothing

		const p = payload([rider(10, 'ALPHA', 600), rider(11, 'GHOST', 0), abandoned]);
		// A mid-course checkpoint, so the abandoned rider has an anchor past the gun and stays in the
		// field — without one he would be compacted out too and prove nothing about alignment.
		p.checkpoints = [
			...checkpoints().slice(0, 1),
			{ checkpointId: MID_ID, seq: 1, name: 'Half', chainageMeters: 500, node: 50, legName: null, legColor: null },
			{ ...must(checkpoints()[1], 'the finish checkpoint'), seq: 2 },
		];
		const race = buildRace(p);

		expect(race.field.size).toBe(2);
		expect(race.statuses).toHaveLength(race.field.size);

		const rows = buildResultRows(race.roster, p.checkpoints);
		for (const row of rows) {
			if (row.index != null) expect(race.statuses[row.index]).toBe(row.status);
		}
		expect(rows.find((r) => r.name === 'QUIT')?.finished).toBe(false);
		expect(rows.find((r) => r.name === 'ALPHA')?.finished).toBe(true);
	});

	it('keeps rows and field aligned when nobody is dropped', () => {
		const p = payload([rider(10, 'ALPHA', 600), rider(12, 'OMEGA', 900)]);

		const race = buildRace(p);
		const rows = buildResultRows(race.roster, p.checkpoints);

		expect(rows).toHaveLength(2);
		for (const row of rows) {
			expect(must(race.meta[row.index ?? -1], 'nobody is dropped here, so every index is real').name)
				.toBe(row.name);
		}
	});
});
