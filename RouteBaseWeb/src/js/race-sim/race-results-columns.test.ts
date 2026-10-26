// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from '../core/invariant';
import { buildResultSchema, defaultColorFor, optionsOf } from './race-results-columns';
import type { RaceLegColumn, RaceResultRow } from './race-results-row';

function row(over: Partial<RaceResultRow> = {}): RaceResultRow {
	return {
		participantId: 1,
		index: 0,
		name: 'BIRCH EQUIPMENT',
		bib: '65',
		category: 'Competitive Open',
		discipline: null,
		gender: null,
		club: null,
		isReal: false,
		color: null,
		emphasised: false, calloutOff: false,
		position: 1,
		finished: true,
		status: 'finished',
		statusRaw: null,
		accumulatedSeconds: 20654,
		officialSeconds: 20654,
		deltaSeconds: 0,
		agreement: 'match',
		legSeconds: [1269, 1255],
		...over,
	};
}

const legs: RaceLegColumn[] = [
	{ checkpointId: 2, label: 'XC Ski', color: '#4dabf7' },
	{ checkpointId: 3, label: 'Downhill', color: '#e8590c' },
];

const keys = (rows: RaceResultRow[], l = legs) => buildResultSchema(rows, l).columns.map((c) => c.key);
const column = (key: string, rows: RaceResultRow[] = [row()], l = legs) =>
	buildResultSchema(rows, l).columns.find((c) => c.key === key)!;
/** The optional columns that start SHOWN for this data. */
const shownKeys = (rows: RaceResultRow[], l = legs) => [...buildResultSchema(rows, l).defaultVisibleKeys].sort();

describe('buildResultSchema', () => {
	it('emits ONE column per leg, keyed by the checkpoint the leg ends at', () => {
		expect(keys([row()])).toContain('leg-2');
		expect(keys([row()])).toContain('leg-3');
		expect(column('leg-2').header).toBe('XC Ski');
		expect(column('leg-3').header).toBe('Downhill');
	});

	it('a race with one leg gets one leg column — the degenerate case needs no special path', () => {
		const single: RaceLegColumn[] = [{ checkpointId: 9, label: 'The whole race', color: null }];
		expect(keys([row()], single).filter((k) => k.startsWith('leg-'))).toEqual(['leg-9']);
	});

	// ── relevance vs preference ───────────────────────────────────────────────────────────────────
	// The schema is the same for every race; only which optional columns START SHOWN is data-driven.
	// (This replaces "the column does not exist when the data is empty": conditional EXISTENCE made the
	// key set vary per race, which made the globally-keyed persisted column order mean different things.)

	it('emits the same column keys whatever the data — a stable schema across races', () => {
		const rich = [row({ gender: 'F', club: 'Nooksack Nordic' }), row({ gender: 'M', club: 'Whatcom' })];
		const poor = [row({ gender: null, club: null, category: null }), row({ gender: null, club: null, category: null })];

		expect(keys(rich)).toEqual(keys(poor));
		expect(keys(poor)).toContain('gender');
		expect(keys(poor)).toContain('club');
	});

	it('starts a column shown only when its data can PARTITION the field', () => {
		// Ski to Sea publishes no gender column at all. An always-present, always-empty "Gender" combo
		// would look broken rather than inapplicable — so it ships hidden, which also ships no filter.
		expect(shownKeys([row({ gender: null, club: null }), row({ gender: null, club: null })]))
			.not.toContain('gender');

		expect(shownKeys([row({ gender: 'F' }), row({ gender: 'M' })])).toContain('gender');
		expect(shownKeys([row({ club: 'Nooksack Nordic' }), row({ club: 'Whatcom' })])).toContain('club');
	});

	it('hides a column whose every row reads the SAME — one option matching everyone narrows nothing', () => {
		// A men-only race. The facet would offer exactly "M", and picking it returns the whole field.
		expect(shownKeys([row({ gender: 'M' }), row({ gender: 'M' })])).not.toContain('gender');
	});

	it('counts null as a value, so "some have a club, some do not" is a real partition', () => {
		expect(shownKeys([row({ club: 'Whatcom' }), row({ club: null })])).toContain('club');
	});

	it('never starts the audit columns shown, however rich the data — that is preference, not relevance', () => {
		const rows = [row({ officialSeconds: 100, deltaSeconds: 5 }), row({ officialSeconds: 200, deltaSeconds: -5 })];

		expect(shownKeys(rows)).not.toContain('official');
		expect(shownKeys(rows)).not.toContain('delta');
	});

	// ── the filter unit ───────────────────────────────────────────────────────────────────────────

	it('exposes times to the filter in MINUTES — the unit a person can actually type', () => {
		// `20-40` is a sentence; `1200-2400` is not. The formatter still renders h:mm:ss.
		expect(column('leg-2').getValue!(row({ legSeconds: [1800, 0] }))).toBe(30);
		expect(column('time').getValue!(row({ accumulatedSeconds: 3600 }))).toBe(60);
	});

	it('keeps a missing time NULL rather than sorting it as zero', () => {
		// A DNF is not "the fastest team". Null sorts last; 0 would sort first.
		expect(column('time').getValue!(row({ accumulatedSeconds: null }))).toBeNull();
		expect(column('leg-2').getValue!(row({ legSeconds: [null, 100] }))).toBeNull();
	});

	// ── the two clocks ────────────────────────────────────────────────────────────────────────────

	it('classes an over-run delta so the cell can be coloured', () => {
		const html = column('delta').formatter!(row({ deltaSeconds: 8040, agreement: 'over' }));

		expect(html).toContain('race-delta-over');
		expect(html).toContain('+2:14:00');   // the legs ran 2h14 past the published finish
	});

	it('classes an under-run delta (untimed dead time between legs)', () => {
		const html = column('delta').formatter!(row({ deltaSeconds: -600, agreement: 'under' }));

		expect(html).toContain('race-delta-under');
		expect(html).toContain('−10:00');
	});

	it('says nothing when there is no published total to compare against', () => {
		const html = column('delta').formatter!(row({ deltaSeconds: null, agreement: null }));

		expect(html).not.toContain('race-delta-');
		expect(html).toContain('--');
	});

	// ── escaping ──────────────────────────────────────────────────────────────────────────────────

	it('escapes the team name — it came out of a CSV the user picked', () => {
		const html = column('name').formatter!(row({ name: '<img src=x onerror=alert(1)>' }));

		expect(html).not.toContain('<img');
		expect(html).toContain('&lt;img');
	});
});

describe('defaultColorFor', () => {
	it('a painted rider keeps his colour', () => {
		expect(defaultColorFor(row({ color: '#e64980' }))).toBe('#e64980');
	});

	it('an unpainted rider falls back by kind — real riders stand out from the field', () => {
		expect(defaultColorFor(row({ color: null, isReal: true }))).toBe('#ffd43b');
		expect(defaultColorFor(row({ color: null, isReal: false }))).toBe('#4dabf7');
	});
});

describe('the swatch cell', () => {
	const swatch = (r: RaceResultRow): string => must(column('color').formatter, 'the swatch is formatted')(r);

	it('marks a rider whose call-out is hidden, so the table cannot contradict the map', () => {
		// He is missing from the map labels and the speed chart; the owner panel is the only other place
		// that says so, and an anonymous share visitor never sees that panel at all.
		expect(swatch(row({ calloutOff: true }))).toContain('race-swatch-hushed');
		expect(swatch(row({ calloutOff: false }))).not.toContain('race-swatch-hushed');
	});

	it('still shows his colour — hidden is not painted-over', () => {
		expect(swatch(row({ color: '#f06595', calloutOff: true }))).toContain('background:#f06595');
	});
});

describe('optionsOf', () => {
	it('collects distinct non-null values, sorted', () => {
		const rows = [
			row({ category: 'Recreational Mixed' }),
			row({ category: 'Competitive Open' }),
			row({ category: 'Recreational Mixed' }),
			row({ category: null }),
		];

		expect(optionsOf(rows, (r) => r.category)).toEqual([
			{ value: 'Competitive Open', label: 'Competitive Open' },
			{ value: 'Recreational Mixed', label: 'Recreational Mixed' },
		]);
	});
});

/**
 * `defaultVisibleKeys` is the set grid-view SHOWS (`!c.optionalKey || visibleKeys.has(c.optionalKey)`),
 * not the set it hides. The race grid had `official` and `delta` in it under a comment reading "columns
 * that start hidden" — the polarity was inverted, so the page shipped with Club hidden and the two audit
 * columns on. That is why the assertions below name the whole optional set before narrowing.
 *
 * It matters more than cosmetics because grid-view builds the FILTER ROW from the visible columns, so a
 * column that ships hidden ships with no filter — and this table's filter is the race's only colour UI.
 */
describe('defaultVisibleKeys', () => {
	it('declares an optional key for every facet column, so the assertions below cannot go stale', () => {
		const rows = [row({ club: 'Bellingham' }), row({ club: 'Whatcom' })];
		const optional = buildResultSchema(rows, []).columns.filter((c) => c.optionalKey).map((c) => c.optionalKey);

		expect(optional.sort())
			.toEqual(['bib', 'category', 'club', 'delta', 'discipline', 'gender', 'official', 'status']);
	});

	it('shows the columns the filter-then-paint workflow needs, and hides the audit ones', () => {
		// Two clubs and two bibs partition; category/gender/status are uniform here, so they stay hidden.
		const rows = [row({ bib: '1', club: 'Bellingham' }), row({ bib: '2', club: 'Whatcom' })];

		expect(shownKeys(rows, [])).toEqual(['bib', 'club']);
	});
});

/**
 * The column reads `row.status`, not `row.finished` — that is the Phase 1 change, and it is the whole
 * point of the vocabulary: `finished` is a fact about a rider's SPLITS, the status is a fact about his
 * RESULT, and the two disagree on real data in both directions.
 */
describe('the Status column', () => {
	const mixed = [row({ status: 'finished' }), row({ status: 'dnf' })];

	it('starts shown when the field is mixed — that is what makes DNF filterable, and paintable', () => {
		expect(shownKeys(mixed)).toContain('status');
	});

	it('stays hidden when everyone finished — the facet would match the whole field', () => {
		expect(shownKeys([row({ status: 'finished' }), row({ status: 'finished' })])).not.toContain('status');
	});

	it('stays hidden when NOBODY finished — the shape a race with no finish checkpoint produces', () => {
		expect(shownKeys([row({ status: 'dnf' }), row({ status: 'dnf' })])).not.toContain('status');
	});

	it('emits the wire value the filter offers, both ways', () => {
		const get = column('status', mixed).getValue!;

		expect(get(row({ status: 'finished' }))).toBe('finished');
		expect(get(row({ status: 'dnf' }))).toBe('dnf');
	});

	it('offers only the statuses THIS race contains — a filter for a value nobody has is a dead control', () => {
		const options = column('status', mixed).filter!;
		const values = (options.options ?? []).map((o) => (typeof o === 'string' ? o : o.value));

		expect(values).toEqual(['finished', 'dnf']);
	});

	it('badges the DNF and stays blank on a finisher — 41 badges read as data, 864 as noise', () => {
		const format = column('status', mixed).formatter!;

		expect(format(row({ status: 'dnf' }))).toContain('is-dnf');
		expect(format(row({ status: 'dnf' }))).toContain('DNF');
		expect(format(row({ status: 'finished' }))).toBe('');
	});

	it('partitions on the STATUS, not on the splits — a published DNF with a finish time still counts', () => {
		// The Black Forest riders who carry a `cp_ziel` time AND a published DNF. Keying the facet on
		// `finished` made them indistinguishable from the finishers, which is the bug seen from the UI.
		const badged = [
			row({ finished: true, status: 'finished' }),
			row({ finished: true, status: 'dnf' }),
		];

		expect(shownKeys(badged)).toContain('status');
		expect(column('status', badged).getValue!(must(badged[1], 'the badged rider'))).toBe('dnf');
	});
});
