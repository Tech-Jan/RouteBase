/**
 * Payload → a race that can be played. Pure: no map, no DOM, no fetch — so the wiring that decides how
 * a rider MOVES is testable without a browser.
 *
 * This is where the two halves meet. A simulated rider's checkpoint times go through the pacing model
 * to become arrival times over the shared course grid (a thin source). A real rider's own GPS becomes a
 * rich source directly — his times are already facts, so there is nothing to model.
 */

import type { RaceParticipantDto, RaceReplayPayload } from './race-api';
import { must } from '../core/invariant';
import {
	DEFAULT_FATIGUE_K,
	fitCostCurve,
	simulateArrivals,
	type Anchor,
	type CostCurve,
} from './pacing-model';
import { RaceField, createRichSource, createThinSource, type CourseGrid, type ParticipantSource } from './race-field';
import type { RiderMeta } from './race-markers';
// Aliased: the local `reachedFinish` array below would otherwise shadow the predicate.
import { finishCheckpointId, reachedFinish as didReachFinish, type RaceRosterEntry } from './race-results-row';
import { specOf, statusOf, type RaceFinishStatus } from './race-finish-status';
import { computeDnfStandings, type DnfStanding } from './race-dnf-standings';

export interface BuiltRace {
	readonly field: RaceField;
	readonly meta: RiderMeta[];
	readonly grid: CourseGrid;
	/** Index into the field, by participantId — the leaderboard and hover need this. */
	readonly indexByParticipantId: Map<number, number>;
	readonly participants: RaceParticipantDto[];
	/**
	 * Each rider's published-or-derived status. Index-aligned with `field`/`meta`/`participants`.
	 *
	 * DELIBERATELY not `field.finished`, which means "past his last time" — and a DNF's last time is
	 * where he stopped, not the line. Built in the keep loop below because that is where the field is
	 * COMPACTED, so the alignment is true by construction rather than by remembering.
	 */
	readonly statuses: readonly RaceFinishStatus[];
	/**
	 * The position each non-finisher held when he STOPPED, plus which number he was to stop (DNF 1, DNF 2).
	 * Keyed by field index; riders who rank are absent. Static for the build — see race-dnf-standings.
	 */
	readonly dnfStandings: ReadonlyMap<number, DnfStanding>;
	/**
	 * EVERY entrant, including the ones the field does not contain — a DNS is on the start list and on
	 * no part of the course. This is what the results table reads; `participants` is the field.
	 */
	readonly roster: RaceRosterEntry[];
}

/**
 * Which clock the replay runs on.
 *
 *   net — everyone leaves at t=0 on his OWN clock. The default, the previous behaviour, and the correct
 *         visualization for a time trial: it shows who was FAST.
 *   gun — everyone is placed on the road at his real start offset. The only way a wave start or a
 *         Ragnar-style staggered relay is legible: it shows what a spectator saw.
 *
 * It changes the FIELD only. The results table stays on net times, because that is what the
 * classification is decided on and what the file published — a rider does not win by starting early.
 */
export type RaceTimeBasis = 'net' | 'gun';

export interface BuildRaceOptions {
	/** Per-rider jitter, so 424 riders on the same terrain do not move as one organism. */
	readonly noise?: number;
	/** Colour for a participant; index-aligned with `payload.participants`. */
	readonly colorFor?: (p: RaceParticipantDto, index: number) => string;
	/** Default 'net' — the behaviour every race had before staggered starts existed. */
	readonly timeBasis?: RaceTimeBasis;
}

/** His start offset in seconds, or 0. Null and a mass start are the same thing. */
export function startOffsetOf(p: RaceParticipantDto, basis: RaceTimeBasis): number {
	if (basis !== 'gun') return 0;
	const offset = p.startOffsetSeconds;
	// A non-finite or negative offset would put him on the course before the race and NaN his whole
	// arrival array; the server already guards it, and this is the last gate before the model.
	return offset != null && Number.isFinite(offset) && offset > 0 ? offset : 0;
}

/** The colours a rider wears when nobody has painted him. Exported: the results table's swatches must
 *  agree with the map, and two copies of a constant is how they stop agreeing. */
export const REAL_COLOR = '#ffd43b';
export const SIMULATED_COLOR = '#4dabf7';

/**
 * A rider's colour: whatever the race painted him, else the default for his kind.
 *
 * The persisted colour is honoured HERE, in the default, rather than by each caller passing a `colorFor`
 * that remembers to check it — a default that has to be opted into is a default that gets forgotten.
 */
export function raceColorOf(p: RaceParticipantDto): string {
	return p.color ?? (p.kind === 'real' ? REAL_COLOR : SIMULATED_COLOR);
}

/**
 * A fallback grade→cost curve for a race no real rider has ridden yet — roughly the shape fitted from
 * the reference ride (seconds per metre). Only used
 * when the server sends no curve; the moment a real effort exists, that is fitted instead and the field
 * inherits ITS terrain response.
 */
function defaultCostCurve(): CostCurve {
	const table: Array<[number, number]> = [
		[-20, 0.0761], [-15, 0.0875], [-10, 0.0875], [-5, 0.1075],
		[0, 0.1608], [5, 0.3179], [10, 0.5372], [15, 0.6494],
	];
	const samples = table.flatMap(([gradePercent, secondsPerMetre]) =>
		Array.from({ length: 5 }, () => ({ gradePercent, secondsPerMetre })),
	);
	return fitCostCurve(samples);
}

function curveFrom(payload: RaceReplayPayload): CostCurve {
	if (!payload.costCurve || payload.costCurve.length === 0) return defaultCostCurve();
	return {
		binPercent: 1,
		minBin: payload.costCurveMinBin,
		cost: Float64Array.from(payload.costCurve),
	};
}

/**
 * Turn a participant's splits into the model's anchors.
 *
 * The start is implicit: everyone leaves the line at 0. The LINE, not the ride's first GPS point — the
 * course grid is the whole recorded activity and the race is a window on it, so `startNode` is where the
 * Start checkpoint sits. Seeded at node 0 regardless, every rider would materialise at the start of the
 * roll-out and ride the neutral section as if it were racing.
 *
 * A rider whose splits stop early (a DNF) simply gets fewer anchors — the model then holds him at his last
 * checkpoint rather than walking him to a finish he never reached.
 *
 * Ordered by NODE, not by time, and that is the whole point. Sorting by time lets ONE corrupt split
 * outrank the good ones: a Ski to Sea import wrote `kayak = 0s` for 11 teams (the accumulator's backward
 * pass underflowing a compressed official total), time-sort put that 0 first, and every real split behind
 * it was then dropped for being "out of order" — so the rider materialised AT the last checkpoint and
 * crawled to the line. Walking the course in order instead, the bogus split is the one that loses.
 *
 * A split at or before the gun is never a fact — it is arithmetic — so it is dropped outright.
 */
function anchorsFor(
	p: RaceParticipantDto, nodeOf: ReadonlyMap<number, number>, startOffset: number, startNode: number,
): Anchor[] {
	// Under the gun basis he sits on the line until HIS start; the sampler already holds a rider at the
	// first node for any t below his first arrival, so the offset needs no special case anywhere else.
	const anchors: Anchor[] = [{ node: startNode, elapsedSeconds: startOffset }];

	// A split at or before HIS gun is arithmetic, never a fact — see the Ski to Sea `kayak = 0s` above.
	// The filter is on the rider's own clock (before the shift), because that is the clock the results
	// file wrote: `> 0` means "after he started", not "after the first wave started".
	//
	// `node > startNode` drops the Start's own split for the same reason it used to drop node 0: it is the
	// gun, already seeded above, and a duplicate node is a zero-length interval to the model.
	const byNode = p.splits
		.map((s) => ({ node: nodeOf.get(s.checkpointId), elapsedSeconds: s.elapsedSeconds }))
		.filter((s): s is Anchor => s.node !== undefined && s.node > startNode && s.elapsedSeconds > 0)
		.map((s) => ({ node: s.node, elapsedSeconds: s.elapsedSeconds + startOffset }))
		.sort((a, b) => a.node - b.node);
	if (byNode.length === 0) return anchors;

	// The furthest split is where the rider's race ENDED — his finish, or wherever he abandoned. Its time is
	// the authority: it is the number the results file published, and everything before it must fit inside
	// it. So an intermediate split claiming a time at or past the finish loses to the finish, rather than
	// the finish losing to it (a timing export that contradicts itself must not cost a rider his line).
	const final = must(byNode[byNode.length - 1], 'byNode non-empty (caller filters empty splits)');

	for (const split of byNode) {
		const last = must(anchors[anchors.length - 1], 'anchors seeded non-empty');
		// The model demands strictly-increasing nodes and non-decreasing times.
		if (split.node <= last.node || split.elapsedSeconds < last.elapsedSeconds) continue;
		if (split !== final && split.elapsedSeconds >= final.elapsedSeconds) continue;
		anchors.push(split);
	}
	return anchors;
}

export function buildRace(payload: RaceReplayPayload, options: BuildRaceOptions = {}): BuiltRace {
	const { course } = payload;
	const grid: CourseGrid = {
		distance: course.lat.map((_, i) => i * course.stepMeters),
		lat: course.lat,
		lng: course.lng,
		elevation: course.elevation,
		gradePercent: course.gradePercent,
		stepMeters: course.stepMeters,
	};

	const curve = curveFrom(payload);
	// Built ONCE. It used to be rebuilt inside anchorsFor — i.e. 424 identical Maps per build.
	const nodeOf = new Map(payload.checkpoints.map((c) => [c.checkpointId, c.node]));
	// Where the race begins on the ride. The lowest checkpoint node, not 0: everything before it is
	// roll-out, and no simulated rider may be placed there.
	const startNode = payload.checkpoints.length > 0 ? Math.min(...payload.checkpoints.map((c) => c.node)) : 0;
	const finishId = finishCheckpointId(payload.checkpoints);
	const noise = options.noise ?? 0.05;
	const colorFor = options.colorFor ?? raceColorOf;
	const basis = options.timeBasis ?? 'net';

	const sources: ParticipantSource[] = [];
	const meta: RiderMeta[] = [];
	const indexByParticipantId = new Map<number, number>();
	const kept: RaceParticipantDto[] = [];
	const statuses: RaceFinishStatus[] = [];
	// Mutable while it is being built — `fieldIndex` is only knowable after the rider survives the keep
	// loop, and the alternative (a second pass keyed by participantId) is the bug this shape prevents.
	const roster: { participant: RaceParticipantDto; fieldIndex: number | null; status: RaceFinishStatus }[] = [];

	// Ability: a rider's finish time against the field's median — read from his OWN clock, never the
	// gun's. Under the gun basis a late wave's riders would otherwise all look slow and be handed a
	// steeper hill response for having started later.
	//
	// A 1:17 winner and a 3:30 last finisher do not lose the same fraction of their race to a 10% climb,
	// and on this course the spread is 2.7x.
	const finishTimes = payload.participants
		.map((p) => p.splits[p.splits.length - 1]?.elapsedSeconds ?? 0)
		.filter((t) => t > 0)
		.sort((a, b) => a - b);
	const medianFinish = finishTimes.length > 0 ? must(finishTimes[finishTimes.length >> 1], 'mid index of non-empty list') : 0;

	for (const p of payload.participants) {
		const status = statusOf(p, didReachFinish(p, finishId));
		// EVERY entrant gets a roster row, and it is pushed BEFORE the two `continue`s below: the results
		// table shows the start list, which is wider than the field. `fieldIndex` is patched in only if he
		// makes it out the far end, so "not in the field" is expressed as a null rather than as an absence.
		const rosterEntry = { participant: p, fieldIndex: null, status } as (typeof roster)[number];
		roster.push(rosterEntry);

		// A DNS never took the course. He has no dot to draw, so he is not a rider the field is missing —
		// he is an entrant it never had. Stated through the status table rather than relying on "he
		// happens to have no splits", which is true today and is not the reason.
		if (!specOf(status).onCourse) continue;

		const startOffset = startOffsetOf(p, basis);
		let source: ParticipantSource;

		if (p.kind === 'real' && p.track) {
			// His times are facts. There is nothing to model — only WHEN they start, which under the gun
			// basis is his own start offset rather than the first wave's.
			if (p.track.lat.length < 2) continue;
			source = createRichSource(
				p.track.lat, p.track.lng,
				Float32Array.from(p.track.elapsedSeconds, (t) => t + startOffset),
				Float32Array.from(p.track.chainageMeters),
			);
		} else {
			const anchors = anchorsFor(p, nodeOf, startOffset, startNode);
			if (anchors.length < 2) continue;   // no time at all — he cannot be placed on the course
			const last = must(anchors[anchors.length - 1], 'anchors seeded non-empty');
			source = createThinSource(
				grid,
				simulateArrivals(grid, curve, anchors, {
					fatigueK: DEFAULT_FATIGUE_K,   // ignored by the model when he has real splits
					// `- startOffset` puts him back on his OWN clock, which is the clock `medianFinish` is
					// measured on. Without it every rider in a late wave reads as slow and is handed a
					// steeper hill response for nothing but the hour he set off at.
					abilityAlpha: abilityAlphaFor(last.elapsedSeconds - startOffset, medianFinish),
					noise,
					seed: p.seed,
				}),
				// His race ends at his last split, not at the end of the course — and begins at the start
				// LINE, not at the ride's first GPS point.
				last.node,
				startNode,
			);
		}

		indexByParticipantId.set(p.participantId, sources.length);
		sources.push(source);
		meta.push({
			name: p.name ?? p.bib ?? `#${p.participantId}`,
			color: colorFor(p, sources.length - 1),
			isReal: p.kind === 'real',
			// Straight off the payload, like the colour — both are persisted presentation, and both have
			// to survive a style switch, which rebuilds the marker layer from this meta.
			emphasised: p.emphasised === true,
		});
		kept.push(p);
		statuses.push(status);
		rosterEntry.fieldIndex = sources.length - 1;
	}

	const field = new RaceField(sources);
	// Sampled once, here, while nothing is rendering yet — computeDnfStandings advances the field to each
	// abandon time and the first render's advanceTo overwrites whatever it leaves behind.
	const dnfStandings = computeDnfStandings(field, statuses);

	return {
		field, meta, grid, indexByParticipantId,
		participants: kept, statuses, roster, dnfStandings,
	};
}

/**
 * Hill sensitivity from finish time. Slower riders lose proportionally more on a climb, so alpha rises
 * with finish time — gently, and clamped: this is NOT yet validated against real data (one reference
 * rider is not a dataset), so it is deliberately a small effect rather than a confident one.
 */
function abilityAlphaFor(finishSeconds: number, medianFinish: number): number {
	if (finishSeconds <= 0 || medianFinish <= 0) return 1;
	const ratio = finishSeconds / medianFinish;
	return Math.min(1.25, Math.max(0.8, 1 + 0.35 * (ratio - 1)));
}

