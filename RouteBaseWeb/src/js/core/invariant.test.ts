// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { must } from './invariant';

describe('must', () => {
	// Pins the guard-flip trap: falsy-but-present values MUST pass through unchanged —
	// `if (!x)` style absence checks skip index 0 / '' / false, `must` never may.
	it('passes falsy non-nullish values through unchanged', () => {
		expect(must(0, 'zero')).toBe(0);
		expect(must('', 'empty string')).toBe('');
		expect(must(false, 'false')).toBe(false);
		expect(must(NaN, 'NaN')).toBeNaN();
	});

	it('returns the value identity for objects', () => {
		const o = { a: 1 };
		expect(must(o, 'obj')).toBe(o);
	});

	it('throws a named message for undefined and null, distinguishing the two', () => {
		expect(() => must(undefined, 'efforts[i] lockstep')).toThrow('invariant: efforts[i] lockstep is absent');
		expect(() => must(null, 'row')).toThrow('invariant: row is null');
	});
});
