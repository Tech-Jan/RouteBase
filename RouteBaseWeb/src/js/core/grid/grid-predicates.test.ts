// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { matchesCombo, matchesDateRange, matchesRanges, matchesSet, matchesText, wildcardToRegex } from './grid-predicates';

describe('wildcardToRegex / matchesText', () => {
	it('contains-matches without wildcards, case-insensitive', () => {
		expect(matchesText('Morning Run', 'run')).toBe(true);
		expect(matchesText('Morning Run', 'bike')).toBe(false);
	});
	it('anchors when wildcards are present', () => {
		expect(matchesText('Morning Run', '*run')).toBe(true);
		expect(matchesText('Morning Run', 'run*')).toBe(false); // anchored: must start with "run"
		expect(matchesText('Morning Run', 'M?rning*')).toBe(true);
	});
	it('ORs |-separated terms and drops empties', () => {
		expect(matchesText('Bike', '*run|bike')).toBe(true);
		expect(matchesText('Swim', '*run|bike')).toBe(false);
		expect(matchesText('anything', '|')).toBe(true); // no effective terms → no constraint
	});
	it('escapes regex metacharacters', () => {
		expect(wildcardToRegex('a+b(c)').test('xa+b(c)y')).toBe(true);
		expect(matchesText('a.b', 'a.b')).toBe(true);
		expect(matchesText('axb', 'a.b')).toBe(false); // '.' is literal, not a regex dot
	});
	it('null value never matches a non-empty pattern', () => {
		expect(matchesText(null, 'x')).toBe(false);
		expect(matchesText(null, '')).toBe(true);
	});
});

describe('matchesText negation (! / !=)', () => {
	it('a negated term excludes matching values', () => {
		expect(matchesText('Morning Live', '!*live*')).toBe(false);
		expect(matchesText('Morning Run', '!*live*')).toBe(true);
	});
	it('accepts the != spelling', () => {
		expect(matchesText('Morning Live', '!=*live*')).toBe(false);
		expect(matchesText('Morning Run', '!=*live*')).toBe(true);
	});
	it('includes NULL/empty values (they cannot contain the term)', () => {
		expect(matchesText(null, '!*live*')).toBe(true);
	});
	it('combines positive OR-terms with negated exclusions', () => {
		expect(matchesText('Morning Run', 'run|!*live*')).toBe(true); // has run, not live
		expect(matchesText('Live Run', 'run|!*live*')).toBe(false); // has run but excluded by !live
		expect(matchesText('Bike', 'run|!*live*')).toBe(false); // no positive match
	});
	it('a bare ! is a no-op', () => {
		expect(matchesText('anything', '!')).toBe(true);
	});
});

describe('matchesSet / matchesCombo', () => {
	it('empty selection matches everything', () => {
		expect(matchesSet('Run', [])).toBe(true);
		expect(matchesSet(null, undefined)).toBe(true);
	});
	it('membership is exact', () => {
		expect(matchesSet('Run', ['Run', 'Bike'])).toBe(true);
		expect(matchesSet('run', ['Run'])).toBe(false);
		expect(matchesSet(null, ['Run'])).toBe(false);
	});
	it('combo ORs text and selection (server ComboColumn mirror)', () => {
		expect(matchesCombo('Run', '*ru*', ['Bike'])).toBe(true); // text side
		expect(matchesCombo('Bike', '*ru*', ['Bike'])).toBe(true); // selected side
		expect(matchesCombo('Swim', '*ru*', ['Bike'])).toBe(false);
		expect(matchesCombo('anything', '', [])).toBe(true); // empty combo → no constraint
	});
});

describe('matchesRanges', () => {
	it('no ranges → no constraint; null never matches (SQL mirror)', () => {
		expect(matchesRanges(5, [])).toBe(true);
		expect(matchesRanges(null, [{ min: 1, max: 2 }])).toBe(false);
	});
	it('matches when any range contains the value, bounds inclusive', () => {
		const ranges = [
			{ min: 3, max: 6 },
			{ min: 8, max: null },
		];
		expect(matchesRanges(3, ranges)).toBe(true);
		expect(matchesRanges(6, ranges)).toBe(true);
		expect(matchesRanges(7, ranges)).toBe(false);
		expect(matchesRanges(100, ranges)).toBe(true);
	});
});

describe('matchesDateRange (local calendar days — GR-01)', () => {
	it('matches a naive local datetime on its boundary day regardless of timezone', () => {
		// Offset-less ISO (the in-memory grids' StartDateTimeLocal wire shape) parses as LOCAL time;
		// with the old epoch compare a UTC+n browser dropped this row for from=its own displayed day.
		expect(matchesDateRange('2026-07-08T00:30:00', '2026-07-08', '2026-07-08')).toBe(true);
		expect(matchesDateRange('2026-07-08T23:59:59', '2026-07-08', '2026-07-08')).toBe(true);
	});
	it('matches a Date instance by its local calendar day in any timezone', () => {
		expect(matchesDateRange(new Date(2026, 6, 8, 0, 30), '2026-07-08', '2026-07-08')).toBe(true);
		expect(matchesDateRange(new Date(2026, 6, 8, 23, 59), '', '2026-07-08')).toBe(true);
	});
	it('excludes neighbouring days', () => {
		expect(matchesDateRange('2026-07-07T23:59:59', '2026-07-08', '')).toBe(false);
		expect(matchesDateRange('2026-07-09T00:00:00', '', '2026-07-08')).toBe(false);
	});
	it('empty bounds match everything; null/invalid values never match', () => {
		expect(matchesDateRange('2026-07-08T12:00:00', '', '')).toBe(true);
		expect(matchesDateRange(null, '2026-07-08', '')).toBe(false);
		expect(matchesDateRange('not a date', '2026-07-08', '')).toBe(false);
	});
	it('an unparseable bound imposes no constraint', () => {
		expect(matchesDateRange('2026-07-08T12:00:00', 'garbage', '')).toBe(true);
	});
	it('supports open-ended ranges', () => {
		expect(matchesDateRange('2026-07-10T00:00:00', '2026-07-08', '')).toBe(true);
		expect(matchesDateRange('2026-07-01T00:00:00', '', '2026-07-08')).toBe(true);
	});
});
