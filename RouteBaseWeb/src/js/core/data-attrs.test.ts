import { describe, it, expect } from 'vitest';
import {
	readData, requireData, readDataNumber, readDataNumberOrNull, readDataInt, setData, clearData,
} from './data-attrs';

function el(attrs: Record<string, string> = {}): HTMLElement {
	const d = document.createElement('div');
	for (const [k, v] of Object.entries(attrs)) d.setAttribute(k, v);
	return d;
}

describe('data-attrs — the single dataset reader (G3)', () => {
	it('readData: absent → null; empty string is a present value', () => {
		expect(readData(el(), 'trackId')).toBeNull();
		expect(readData(el({ 'data-track-id': '' }), 'trackId')).toBe('');
		expect(readData(el({ 'data-track-id': '7' }), 'trackId')).toBe('7');
	});

	it('requireData: throws naming the kebab-cased key and the element', () => {
		expect(() => requireData(el(), 'splitStart')).toThrowError(/data-split-start on <div>/);
		expect(requireData(el({ 'data-split-start': '1.5' }), 'splitStart')).toBe('1.5');
	});

	it('readDataNumber: NaN-guarded — a non-numeric value throws with the key, never NaN', () => {
		expect(readDataNumber(el({ 'data-split-start': '1.5' }), 'splitStart')).toBe(1.5);
		expect(() => readDataNumber(el({ 'data-split-start': 'oops' }), 'splitStart'))
			.toThrowError(/data-split-start = "oops"/);
		expect(() => readDataNumber(el(), 'splitStart')).toThrowError(/missing/);
	});

	it('readDataNumberOrNull: absent → null, present garbage still throws', () => {
		expect(readDataNumberOrNull(el(), 'x')).toBeNull();
		expect(readDataNumberOrNull(el({ 'data-x': '2' }), 'x')).toBe(2);
		expect(() => readDataNumberOrNull(el({ 'data-x': 'y' }), 'x')).toThrowError(/not numeric/);
	});

	it('readDataInt: the bootstrap default pattern — absent or garbage falls back', () => {
		expect(readDataInt(el(), 'pointCount', 0)).toBe(0);
		expect(readDataInt(el({ 'data-point-count': '41' }), 'pointCount', 0)).toBe(41);
		expect(readDataInt(el({ 'data-point-count': 'x' }), 'pointCount', 9)).toBe(9);
	});

	it('setData/clearData round-trip through the same contract', () => {
		const d = el();
		setData(d, 'segmentId', 12);
		expect(readData(d, 'segmentId')).toBe('12');
		clearData(d, 'segmentId');
		expect(readData(d, 'segmentId')).toBeNull();
	});
});
