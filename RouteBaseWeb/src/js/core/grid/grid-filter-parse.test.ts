// @vitest-environment node
/**
 * TS mirror of RouteBaseWeb.Tests/Services/GridFilterParsingTests.cs — the parser header mandates
 * "same grammar + same test cases". Keep both suites in sync when the grammar changes.
 */
import { describe, it, expect } from 'vitest';
import { parseRanges, stripNegation, type NumRange } from './grid-filter-parse';

function r(min: number | null, max: number | null): NumRange {
	return { min, max };
}

describe('parseRanges (C# GridFilterParsingTests mirror)', () => {
	const cases: Array<[string, NumRange[]]> = [
		['3-6', [r(3, 6)]],
		['3-6,8-12', [r(3, 6), r(8, 12)]],
		['3-6|8-12', [r(3, 6), r(8, 12)]],
		[' 3 - 6 , 8-12 ', [r(3, 6), r(8, 12)]],
		['>5', [r(5, null)]],
		['>=5', [r(5, null)]],
		['5-', [r(5, null)]],
		['<10', [r(null, 10)]],
		['<=10', [r(null, 10)]],
		['-10', [r(null, 10)]],
		['5', [r(5, 5)]],
		['-10-5', [r(-10, 5)]],
		['1.5-2.5', [r(1.5, 2.5)]],
		// bad token + reversed dropped, good kept
		['abc, 3-6, 9-1', [r(3, 6)]],
	];
	it.each(cases)('parses %j', (input, expected) => {
		expect(parseRanges(input)).toEqual(expected);
	});

	const dropped: Array<string | null | undefined> = ['6-3', 'x', '', null, undefined, ', , '];
	it.each(dropped)('drops invalid/empty input %j', (input) => {
		expect(parseRanges(input)).toEqual([]);
	});

	// SC-02 twin: non-finite bounds must never survive (C# guards via double.IsFinite; TS via the regex).
	const nonFinite = ['NaN', 'Infinity', '-Infinity', '>NaN', '5-Infinity'];
	it.each(nonFinite)('rejects non-finite %j', (input) => {
		expect(parseRanges(input)).toEqual([]);
	});

	// TS-side edge pins beyond the C# list (verified same behavior in C# by hand).
	it('parses a fully negative closed range', () => {
		expect(parseRanges('-20--10')).toEqual([r(-20, -10)]);
	});
	it('parses exponent notation', () => {
		expect(parseRanges('1e2-2e2')).toEqual([r(100, 200)]);
	});
	it('parses the legacy operator spellings for negative single bounds', () => {
		expect(parseRanges('<=-10')).toEqual([r(null, -10)]);
		expect(parseRanges('>=-10')).toEqual([r(-10, null)]);
	});
});

describe('stripNegation (C# GridFilterParsingTests mirror)', () => {
	const cases: Array<[string, { negated: boolean; pattern: string }]> = [
		['*live*', { negated: false, pattern: '*live*' }],
		['!*live*', { negated: true, pattern: '*live*' }],
		['!=*live*', { negated: true, pattern: '*live*' }],
		['! *live* ', { negated: true, pattern: '*live*' }], // trimmed
		['!', { negated: true, pattern: '' }], // bare ! → empty (no-op for callers)
	];
	it.each(cases)('parses %j', (input, expected) => {
		expect(stripNegation(input)).toEqual(expected);
	});
});
