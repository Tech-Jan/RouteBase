// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { buildRace } from './race-build';
import type { RaceParticipantDto, RaceReplayPayload } from './race-api';

const NODES = 201;          // 2000 m at 10 m
const STEP = 10;
const TOTAL = (NODES - 1) * STEP;

function payload(participants: RaceParticipantDto[]): RaceReplayPayload {
	return {
		raceId: 1,
		raceName: 'Black Forest',
		course: {
			lat: Array.from({ length: NODES }, () => 47.9),
			lng: Array.from({ length: NODES }, (_, i) => 8.1 + i * 0.0001),
			elevation: Array.from({ length: NODES }, () => 500),
			// First half climbs at 8%, second half descends at 8%.
			gradePercent: Array.from({ length: NODES }, (_, i) => (i < NODES / 2 ? 8 : -8)),
			stepMeters: STEP,
			totalMeters: TOTAL,
		},
		checkpoints: [
			{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 0, node: 0, legName: null, legColor: null },
			{ checkpointId: 2, seq: 1, name: 'Half', chainageMeters: TOTAL / 2, node: 100, legName: null, legColor: null },
			{ checkpointId: 3, seq: 2, name: 'Finish', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null },
		],
		participants,
		costCurve: null,
		costCurveMinBin: 0,
	};
}

function simulated(id: number, finishSeconds: number, half?: number): RaceParticipantDto {
	const splits = half === undefined
		? [{ checkpointId: 3, elapsedSeconds: finishSeconds }]
		: [
			{ checkpointId: 2, elapsedSeconds: half },
			{ checkpointId: 3, elapsedSeconds: finishSeconds },
		];
	return {
		participantId: id, kind: 'simulated', name: `Rider ${id}`, bib: `${id}`,
		category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false, officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: id, splits, track: null,
	};
}

function real(id: number): RaceParticipantDto {
	return {
		participantId: id, kind: 'real', name: 'Me', bib: null, category: null, gender: null, club: null, color: null, emphasised: false, calloutOff: false, officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: 0,
		splits: [],
		track: {
			lat: [47.95, 47.95, 47.95],       // well off the course line
			lng: [8.1, 8.11, 8.12],
			elapsedSeconds: [0, 500, 1000],
			chainageMeters: [0, TOTAL / 2, TOTAL],
		},
	};
}

describe('buildRace', () => {
	it('places every rider in one field, real and simulated alike', () => {
		const built = buildRace(payload([simulated(1, 1000), simulated(2, 1200), real(3)]));

		expect(built.field.size).toBe(3);
		expect(built.meta.map((m) => m.isReal)).toEqual([false, false, true]);
		expect(built.indexByParticipantId.get(3)).toBe(2);
	});

	it('a simulated rider finishes on his recorded time, to the second', () => {
		const built = buildRace(payload([simulated(1, 1234)]), { noise: 0 });

		built.field.advanceTo(1234);

		expect(built.field.chainages[0]).toBeCloseTo(TOTAL, 0);
		expect(built.field.finished[0]).toBe(1);
	});

	it('a simulated rider is slower up than down — he is not on constant pace', () => {
		// Both halves are the same distance. A constant-pace rider is at halfway at halfway; a real one
		// has spent most of his race on the climb.
		const built = buildRace(payload([simulated(1, 1000)]), { noise: 0 });

		built.field.advanceTo(500);

		expect(built.field.chainages[0]).toBeLessThan(TOTAL * 0.4);
	});

	it('hits an intermediate split exactly when the rider has one', () => {
		const built = buildRace(payload([simulated(1, 1000, 700)]), { noise: 0 });

		built.field.advanceTo(700);

		expect(built.field.chainages[0]).toBeCloseTo(TOTAL / 2, 0);
	});

	it('a real rider rides his OWN line, not the course', () => {
		const built = buildRace(payload([simulated(1, 1000), real(2)]), { noise: 0 });

		built.field.advanceTo(500);

		expect(built.field.lats[0]).toBeCloseTo(47.9, 6);    // simulated: on the course
		expect(built.field.lats[1]).toBeCloseTo(47.95, 6);   // real: off it, and stays off it
	});

	it('drops a rider with no time at all rather than parking him on the start line', () => {
		const ghost: RaceParticipantDto = {
			participantId: 9, kind: 'simulated', name: 'Ghost', bib: null, category: null,
			gender: null, club: null, color: null, emphasised: false, calloutOff: false, officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: 9, splits: [], track: null,
		};

		const built = buildRace(payload([simulated(1, 1000), ghost]));

		expect(built.field.size).toBe(1);
		expect(built.indexByParticipantId.has(9)).toBe(false);
	});

	it('survives out-of-order or duplicate splits instead of throwing mid-race', () => {
		// Bad data from a timing export must not take the whole replay down.
		const broken: RaceParticipantDto = {
			participantId: 5, kind: 'simulated', name: 'Broken', bib: null, category: null,
			gender: null, club: null, color: null, emphasised: false, calloutOff: false, officialFinishSeconds: null, finishStatus: null, finishStatusRaw: null, startOffsetSeconds: null, wave: null, discipline: null, seed: 5,
			splits: [
				{ checkpointId: 3, elapsedSeconds: 900 },   // finish...
				{ checkpointId: 2, elapsedSeconds: 950 },   // ...before the halfway split?
			],
			track: null,
		};

		const built = buildRace(payload([broken]));

		expect(built.field.size).toBe(1);
		built.field.advanceTo(900);
		expect(built.field.finished[0]).toBe(1);
	});

	it('noise is per-rider and deterministic — the same seed replays identically', () => {
		const a = buildRace(payload([simulated(1, 1000), simulated(2, 1000)]), { noise: 0.3 });
		const b = buildRace(payload([simulated(1, 1000), simulated(2, 1000)]), { noise: 0.3 });

		a.field.advanceTo(500);
		b.field.advanceTo(500);

		expect(a.field.chainages[0]).toBeCloseTo(b.field.chainages[0] ?? NaN, 6);
		// ...but two riders on identical times do NOT move as one organism.
		expect(a.field.chainages[0]).not.toBeCloseTo(a.field.chainages[1] ?? NaN, 2);
	});

	it('ranks the field by progress', () => {
		const built = buildRace(payload([simulated(1, 2000), simulated(2, 1000), simulated(3, 1500)]), {
			noise: 0,
		});

		built.field.advanceTo(500);

		expect(built.field.order()).toEqual([1, 2, 0]);
	});

	/**
	 * The Ski to Sea teleport: a corrupt split put a LATE checkpoint at t=0. Anchors used to be sorted by
	 * TIME, so that 0 outranked the rider's real splits, and every one of them was then dropped for being
	 * "out of order" behind it — leaving him standing AT that checkpoint when the gun went, crawling to the
	 * line for the rest of the race. Ordering by course position instead makes the bogus split the loser.
	 */
	it('does not teleport a rider to a checkpoint his split claims he reached at t=0', () => {
		const corrupt: RaceParticipantDto = {
			...simulated(1, 1000),
			splits: [
				{ checkpointId: 2, elapsedSeconds: 0 },      // the half-way checkpoint, "reached" at the gun
				{ checkpointId: 3, elapsedSeconds: 1000 },
			],
		};

		const built = buildRace(payload([corrupt]), { noise: 0 });
		built.field.advanceTo(0);

		expect(built.field.chainages[0]).toBe(0);          // on the line, not 1 km up the road
	});

	/**
	 * A rider whose results simply stop (87 Ski to Sea teams publish no finish time) must be held WHERE HE
	 * STOPPED. The pacing model holds his arrivals flat past his last anchor, so the sampler used to read the
	 * whole tail as "reached" and park him on the finish line — showing a rider crossing a line he never got to.
	 */
	it('holds a rider with no finish at his last split, not at the finish line', () => {
		const dnf: RaceParticipantDto = {
			...simulated(1, 1000),
			splits: [{ checkpointId: 2, elapsedSeconds: 500 }],   // halfway, and nothing after it
		};

		const built = buildRace(payload([dnf]), { noise: 0 });
		built.field.advanceTo(5000);   // long past his last split

		expect(built.field.chainages[0]).toBeCloseTo(TOTAL / 2, 0);
		expect(built.field.finished[0]).toBe(1);
	});

	/**
	 * The STATUS answers a different question from `field.finished`, which means "past his last
	 * time" — and a DNF's last time is where he QUIT. The map fades on the second; the sidebar label and
	 * the table's Status facet read the first.
	 */
	it('tells a rider who reached the line from one who stopped short', () => {
		const dnf: RaceParticipantDto = {
			...simulated(2, 1000),
			splits: [{ checkpointId: 2, elapsedSeconds: 500 }],
		};

		const built = buildRace(payload([simulated(1, 1000), dnf]), { noise: 0 });
		built.field.advanceTo(5000);

		expect([...built.statuses]).toEqual(['finished', 'dnf']);
		// Both are STOPPED by now — which is exactly why the two facts cannot be one boolean.
		expect(built.field.finished[0]).toBe(1);
		expect(built.field.finished[1]).toBe(1);
	});

	// The other side of race-results-row's `reachedFinish(p, null) === true`, reached through statusOf.
	// Both callers read that ONE helper; they used to each decide it, and disagree.
	it('counts everyone as finished when the race has no finish checkpoint — no line, nothing to miss', () => {
		// A REAL rider: buildLegColumns needs two checkpoints to make a leg, so one leaves
		// finishCheckpointId null — and with one checkpoint there is no interval to pace a simulated rider
		// through. The gun is the lowest checkpoint now, not node 0, so a single checkpoint IS the gun and
		// a thin rider gets one anchor and is dropped. A real rider rides his own GPS and needs none.
		const p = payload([real(1)]);
		p.checkpoints = [
			{ checkpointId: 3, seq: 0, name: 'Somewhere', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null },
		];

		const built = buildRace(p, { noise: 0 });

		expect(built.field.size).toBe(1);
		expect([...built.statuses]).toEqual(['finished']);
	});

	/**
	 * A DNS is an entrant the field never had, not a rider it lost. He is on the start list — 113 of the
	 * 1387 Schauinsland entrants are exactly this — so he must survive into the ROSTER while owning no
	 * index into `field`/`meta`/`participants`. A number there would address the rider standing in that
	 * slot, which is how a follow/paint lands on someone else.
	 */
	it('keeps a DNS on the roster and off the course', () => {
		const dns: RaceParticipantDto = {
			...simulated(2, 1000),
			finishStatus: 'dns',
			finishStatusRaw: 'DNS/DNF',
		};

		const built = buildRace(payload([simulated(1, 1000), dns]), { noise: 0 });

		expect(built.field.size).toBe(1);
		expect(built.roster).toHaveLength(2);
		expect(built.roster.map((r) => r.fieldIndex)).toEqual([0, null]);
		expect(built.roster.map((r) => r.status)).toEqual(['finished', 'dns']);
		expect(built.indexByParticipantId.has(2)).toBe(false);
	});

	it('gives every roster entry the field index it actually got, across a rider the field dropped', () => {
		// The ghost has no time at all, so buildRace drops him for a different reason than the DNS above —
		// and the roster must still line up: everyone after him keeps the index he did NOT take.
		const ghost: RaceParticipantDto = { ...simulated(2, 1000), splits: [] };

		const built = buildRace(payload([simulated(1, 1000), ghost, simulated(3, 1200)]), { noise: 0 });

		expect(built.roster.map((r) => r.fieldIndex)).toEqual([0, null, 1]);
		expect(built.participants.map((p) => p.participantId)).toEqual([1, 3]);
	});

	it('lets a published status override what the splits say', () => {
		// The Black Forest case: a finish-mat time AND a published DNF. The file wins.
		const badged: RaceParticipantDto = { ...simulated(2, 1000), finishStatus: 'dnf' };

		expect([...buildRace(payload([badged]), { noise: 0 }).statuses]).toEqual(['dnf']);
	});

	// ── staggered starts ───────────────────────────────────────────────────────────────────────────

	/**
	 * Net is the default and the previous behaviour, and it is the CORRECT visualization for a time trial:
	 * it shows who was fast. Gun shows what a spectator saw. A race with no offsets reads identically
	 * either way, which is why the toggle only appears when the field actually has them.
	 */
	it('leaves a staggered field alone under the NET basis — everyone still leaves at t=0', () => {
		const late: RaceParticipantDto = { ...simulated(2, 1000), startOffsetSeconds: 600 };

		const built = buildRace(payload([simulated(1, 1000), late]), { noise: 0 });
		built.field.advanceTo(1000);

		// Both have finished: their own clocks both ran 0 → 1000.
		expect([...built.field.finished]).toEqual([1, 1]);
	});

	it('holds a late starter on the line until HIS gun under the gun basis', () => {
		const late: RaceParticipantDto = { ...simulated(2, 1000), startOffsetSeconds: 600 };

		const built = buildRace(payload([simulated(1, 1000), late]), { noise: 0, timeBasis: 'gun' });

		built.field.advanceTo(300);
		expect(built.field.chainages[1]).toBe(0);          // still standing at his block
		expect(built.field.chainages[0]).toBeGreaterThan(0);

		built.field.advanceTo(1000);
		expect(built.field.finished[0]).toBe(1);           // the early rider is home
		expect(built.field.finished[1]).toBe(0);           // the late one is 400 s into his ride
		expect(built.field.chainages[1]).toBeGreaterThan(0);
	});

	it('extends the play clock to cover the last wave — it is the field\'s own max', () => {
		const late: RaceParticipantDto = { ...simulated(2, 1000), startOffsetSeconds: 600 };

		const net = buildRace(payload([simulated(1, 1000), late]), { noise: 0 });
		const gun = buildRace(payload([simulated(1, 1000), late]), { noise: 0, timeBasis: 'gun' });

		expect(gun.field.endSeconds).toBeCloseTo(net.field.endSeconds + 600, 0);
	});

	it('shifts a REAL rider too — his GPS is a fact, WHEN he set off is the schedule\'s', () => {
		const late = real(1);
		late.startOffsetSeconds = 600;

		const gun = buildRace(payload([late]), { noise: 0, timeBasis: 'gun' });

		gun.field.advanceTo(300);
		expect(gun.field.chainages[0]).toBe(0);
	});

	it('ignores an impossible offset rather than putting him on the course before the race', () => {
		const bad: RaceParticipantDto = { ...simulated(2, 1000), startOffsetSeconds: -60 };

		const built = buildRace(payload([bad]), { noise: 0, timeBasis: 'gun' });
		built.field.advanceTo(1000);

		expect(built.field.finished[0]).toBe(1);
	});

	it('reads a real rider from his DERIVED splits — he gets no row for a checkpoint he never reached', () => {
		const withFinish = real(1);
		withFinish.splits = [{ checkpointId: 3, elapsedSeconds: 1000 }];
		const abandoned = real(2);
		abandoned.splits = [{ checkpointId: 2, elapsedSeconds: 400 }];

		expect([...buildRace(payload([withFinish, abandoned]), { noise: 0 }).statuses]).toEqual(['finished', 'dnf']);
	});

	it('keeps the good splits of a rider whose later ones went bad', () => {
		// Half-way at 400 s is real; the finish checkpoint claiming t=0 is arithmetic. The first must
		// survive the second.
		const corrupt: RaceParticipantDto = {
			...simulated(1, 1000),
			splits: [
				{ checkpointId: 2, elapsedSeconds: 400 },
				{ checkpointId: 3, elapsedSeconds: 0 },
			],
		};

		const built = buildRace(payload([corrupt]), { noise: 0 });
		built.field.advanceTo(400);

		expect(built.field.chainages[0]).toBeCloseTo(TOTAL / 2, 0);   // he honoured his real split
	});
});

/**
 * The grid is the whole recorded RIDE and the race is a window on it, so the Start checkpoint may sit well
 * into the course — a neutral roll-out to the line. Every simulated rider is anchored there, not at node 0.
 *
 * Seeded at node 0 the field would spawn at the ride's first GPS point and race the roll-out; worse, the
 * pacing model leaves every node below the first anchor zero-filled, and the sampler's start clamp reads a
 * zero as "he arrived at t=0" — so at the gun the whole field stood at the wrong end of the neutral section.
 */
describe('buildRace — a race that starts after the roll-out', () => {
	function windowed(participants: RaceParticipantDto[]): RaceReplayPayload {
		const p = payload(participants);
		return {
			...p,
			checkpoints: [
				{ checkpointId: 1, seq: 0, name: 'Start', chainageMeters: 400, node: 40, legName: null, legColor: null },
				{ checkpointId: 2, seq: 1, name: 'Half', chainageMeters: TOTAL / 2, node: 100, legName: null, legColor: null },
				{ checkpointId: 3, seq: 2, name: 'Finish', chainageMeters: TOTAL, node: NODES - 1, legName: null, legColor: null },
			],
		};
	}

	it('holds the field on the start LINE at the gun, not at the ride\'s first point', () => {
		const built = buildRace(windowed([simulated(1, 1000)]), { noise: 0 });

		built.field.advanceTo(0);
		expect(built.field.chainages[0]).toBeCloseTo(400, 0);
	});

	it('still walks him to the finish on his own time', () => {
		const built = buildRace(windowed([simulated(1, 1000)]), { noise: 0 });

		built.field.advanceTo(1000);
		expect(built.field.chainages[0]).toBeCloseTo(TOTAL, 0);
		expect(built.field.finished[0]).toBe(1);
	});

	it('does not double-anchor a rider whose results file split the start line too', () => {
		// A split AT the start is the gun, already seeded — a second anchor on the same node is a
		// zero-length interval to the model, which throws rather than pacing it.
		const withStartSplit = simulated(1, 1000);
		withStartSplit.splits = [{ checkpointId: 1, elapsedSeconds: 0 }, ...withStartSplit.splits];

		expect(() => buildRace(windowed([withStartSplit]), { noise: 0 })).not.toThrow();
	});
});
