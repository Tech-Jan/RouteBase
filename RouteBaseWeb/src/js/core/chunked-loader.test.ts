// @vitest-environment node
import { describe, it, expect, vi } from 'vitest';
import { loadInChunks } from './chunked-loader';

// A fetchChunk that echoes back the id subset it was given, after a microtask.
const echo = async (ids: number[]): Promise<number[]> => {
	await Promise.resolve();
	return ids;
};

describe('loadInChunks — chunking & ordering', () => {
	it('sorts ids and chunks at chunkSize boundaries; results in chunk order', async () => {
		const results = await loadInChunks<number, number[]>({
			ids: [5, 1, 3, 2, 4, 6],
			fetchChunk: echo,
			chunkSize: 2,
			concurrency: 1,
		});
		expect(results).toEqual([
			[1, 2],
			[3, 4],
			[5, 6],
		]);
	});

	it('preserveOrder keeps the caller id order', async () => {
		const results = await loadInChunks<number, number[]>({
			ids: [5, 1, 3],
			fetchChunk: echo,
			chunkSize: 1,
			preserveOrder: true,
			concurrency: 1,
		});
		expect(results).toEqual([[5], [1], [3]]);
	});

	it('empty ids resolves to []', async () => {
		const fetchChunk = vi.fn(echo);
		const results = await loadInChunks<number, number[]>({ ids: [], fetchChunk });
		expect(results).toEqual([]);
		expect(fetchChunk).not.toHaveBeenCalled();
	});
});

describe('loadInChunks — progress & per-chunk callbacks', () => {
	it('onProgress reports cumulative counts; onChunk fires once per chunk with its id subset', async () => {
		const chunksSeen: number[][] = [];
		const progress: number[] = [];
		await loadInChunks<number, number[]>({
			ids: [1, 2, 3, 4, 5, 6],
			fetchChunk: echo,
			chunkSize: 2,
			concurrency: 1,
			onChunk: (_r, ids) => chunksSeen.push(ids),
			onProgress: (loaded) => progress.push(loaded),
		});
		expect(chunksSeen).toEqual([
			[1, 2],
			[3, 4],
			[5, 6],
		]);
		expect(progress).toEqual([2, 4, 6]); // cumulative, total = 6
	});
});

describe('loadInChunks — retry & failure', () => {
	it('retries a failed chunk once and succeeds', async () => {
		let calls = 0;
		const fetchChunk = vi.fn(async (ids: number[]) => {
			calls++;
			if (calls === 1) throw new Error('transient');
			return ids;
		});
		const results = await loadInChunks<number, number[]>({
			ids: [1, 2],
			fetchChunk,
			chunkSize: 2,
		});
		expect(results).toEqual([[1, 2]]);
		expect(fetchChunk).toHaveBeenCalledTimes(2);
	});

	it('rejects with the original (first) error after two failures', async () => {
		const fetchChunk = vi.fn(async () => {
			throw new Error(`err-${fetchChunk.mock.calls.length}`);
		});
		await expect(
			loadInChunks<number, number[]>({ ids: [1, 2], fetchChunk, chunkSize: 2 }),
		).rejects.toThrow('err-1'); // first attempt's error is surfaced, not the retry's
	});

	// CB-05: after a fatal chunk failure, surviving pool workers must stop scheduling
	// and suppress callbacks instead of rendering into an already-failed page.
	it('stops scheduling chunks and firing callbacks after a fatal failure', async () => {
		const onChunk = vi.fn();
		const onProgress = vi.fn();
		const fetchChunk = vi.fn(async (ids: number[]): Promise<number[]> => {
			if (ids[0] === 1) throw new Error('boom'); // fails both attempts (retry)
			await new Promise((r) => setTimeout(r, 5)); // successful chunks resolve later
			return ids;
		});

		await expect(
			loadInChunks<number, number[]>({
				ids: Array.from({ length: 10 }, (_, i) => i), // chunks [0]..[9]
				fetchChunk,
				chunkSize: 1,
				concurrency: 2,
				onChunk,
				onProgress,
			}),
		).rejects.toThrow('boom');

		const callsAtRejection = fetchChunk.mock.calls.length;
		const chunksAtRejection = onChunk.mock.calls.length;

		// Let any in-flight straggler settle — it must not schedule more or render.
		await new Promise((r) => setTimeout(r, 20));

		expect(fetchChunk.mock.calls.length).toBe(callsAtRejection);
		expect(onChunk.mock.calls.length).toBe(chunksAtRejection);
		expect(onProgress.mock.calls.length).toBe(0);
		expect(fetchChunk.mock.calls.length).toBeLessThan(10); // never fetched all 10
	});
});

describe('loadInChunks — cancellation', () => {
	it('a pre-aborted signal rejects with AbortError without fetching', async () => {
		const fetchChunk = vi.fn(echo);
		const ctrl = new AbortController();
		ctrl.abort();
		await expect(
			loadInChunks<number, number[]>({ ids: [1, 2, 3], fetchChunk, signal: ctrl.signal }),
		).rejects.toMatchObject({ name: 'AbortError' });
		expect(fetchChunk).not.toHaveBeenCalled();
	});

	it('abort mid-flight: no onChunk/onProgress after abort; rejects AbortError', async () => {
		const ctrl = new AbortController();
		const onChunk = vi.fn();
		const onProgress = vi.fn();
		const fetchChunk = async (ids: number[]): Promise<number[]> => {
			ctrl.abort(); // abort while the chunk is "in flight"
			return ids;
		};
		await expect(
			loadInChunks<number, number[]>({
				ids: [1, 2],
				fetchChunk,
				chunkSize: 2,
				signal: ctrl.signal,
				onChunk,
				onProgress,
			}),
		).rejects.toMatchObject({ name: 'AbortError' });
		expect(onChunk).not.toHaveBeenCalled();
		expect(onProgress).not.toHaveBeenCalled();
	});
});

describe('loadInChunks — concurrency bound', () => {
	it('never exceeds the concurrency limit (default 4)', async () => {
		let inFlight = 0;
		let maxInFlight = 0;
		const fetchChunk = async (ids: number[]): Promise<number[]> => {
			inFlight++;
			maxInFlight = Math.max(maxInFlight, inFlight);
			await new Promise((r) => setTimeout(r, 0));
			inFlight--;
			return ids;
		};
		await loadInChunks<number, number[]>({
			ids: Array.from({ length: 20 }, (_, i) => i),
			fetchChunk,
			chunkSize: 1, // 20 chunks
		});
		expect(maxInFlight).toBeLessThanOrEqual(4);
		expect(maxInFlight).toBeGreaterThan(1); // pool actually parallelises
	});
});
