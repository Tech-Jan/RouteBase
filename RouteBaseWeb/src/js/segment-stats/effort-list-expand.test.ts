// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { centerScrollTop } from './effort-list-expand';

// 10 rows of 50px in a 250px viewport → scrollHeight 500, maxScroll 250.
const ROW = 50;
const VIEWPORT = 250;
const SCROLL = 500;

describe('centerScrollTop', () => {
	it('centres a middle row', () => {
		// row 5: 5*50 - 250/2 + 50/2 = 250 - 125 + 25 = 150
		expect(centerScrollTop(5, ROW, VIEWPORT, SCROLL)).toBe(150);
	});

	it('clamps the leader to the top', () => {
		expect(centerScrollTop(0, ROW, VIEWPORT, SCROLL)).toBe(0);
	});

	it('clamps the last row to the bottom (maxScroll)', () => {
		expect(centerScrollTop(9, ROW, VIEWPORT, SCROLL)).toBe(250);
	});

	it('is 0 when a row height is not yet known', () => {
		expect(centerScrollTop(5, 0, VIEWPORT, SCROLL)).toBe(0);
	});

	it('never scrolls past the content when it barely overflows', () => {
		const top = centerScrollTop(9, ROW, VIEWPORT, SCROLL);
		expect(top).toBeLessThanOrEqual(SCROLL - VIEWPORT);
		expect(top).toBeGreaterThanOrEqual(0);
	});
});
