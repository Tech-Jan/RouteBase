// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { chunkBySize } from './import-chunking';

const size = (n: number) => n;

describe('chunkBySize', () => {
	it('caps a chunk by item count', () => {
		expect(chunkBySize([1, 1, 1, 1, 1], size, 2, 1000)).toEqual([[1, 1], [1, 1], [1]]);
	});

	it('caps a chunk by total bytes', () => {
		expect(chunkBySize([6, 5, 4, 1], size, 100, 10)).toEqual([[6], [5, 4, 1]]);
	});

	it('gives an oversized item its own chunk without dropping it', () => {
		expect(chunkBySize([2, 99, 3], size, 100, 10)).toEqual([[2], [99], [3]]);
	});

	it('returns no chunks for an empty queue', () => {
		expect(chunkBySize([], size, 40, 100)).toEqual([]);
	});
});
