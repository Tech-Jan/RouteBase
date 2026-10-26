/**
 * core/chunked-loader.ts — generic chunked bulk loader with progress + cancellation.
 *
 * Splits a large id list into fixed-size chunks and fetches them through a bounded
 * concurrency pool, reporting progress as each chunk resolves. Used by any page that
 * needs to bulk-load by id while keeping the UI responsive and showing "X / Y" progress.
 *
 * The helper is deliberately transport-agnostic: it knows nothing about GPS, endpoints,
 * maps, or response shapes. The caller supplies `fetchChunk` (which owns the URL, request
 * body, and parsing) and `onProgress` / `onChunk` (which own the page's status UI and
 * progressive rendering). This keeps the batching concern in one place and lets activities
 * and segments — which hit different endpoints with different response shapes — reuse it.
 *
 * Why client-side chunking (rather than streaming a single request): the bulk GPS query is a
 * monolithic SQLite window-function compute. Splitting the id list decomposes that compute into
 * smaller queries that return independently → genuine progress + faster first paint, while the
 * tuned, cached data-access path stays untouched.
 *
 * Cancellation: pass an AbortSignal. Once aborted, no further chunks are scheduled and no
 * further onChunk/onProgress callbacks fire; the returned promise rejects with an AbortError.
 * Callers should catch that (or check `signal.aborted` after awaiting) and bail silently.
 *
 * Partial failure: each chunk is retried once on error; if it still fails the returned promise
 * rejects with that error (the caller decides how to surface it). After a fatal failure no
 * further chunks are scheduled and no further onChunk/onProgress callbacks fire — surviving pool
 * workers stop rather than render into a page that already handled the rejection. Chunks that
 * already resolved have had their onChunk fire, so a late failure does not un-render earlier progress.
 */

import { must } from './invariant';

export interface ChunkedLoadOptions<TId, TChunkResult> {
	/** Full id list to load. Chunked deterministically (sorted) so backend cache keys stay stable. */
	ids: TId[];
	/** Fetch one chunk. Owns the URL, request body, and response parsing. */
	fetchChunk: (idsSubset: TId[]) => Promise<TChunkResult>;
	/** Called as each chunk resolves, with the cumulative count of ids loaded so far. */
	onProgress?: ((loaded: number, total: number) => void) | undefined;
	/** Called once per resolved chunk — the hook for progressive rendering. */
	onChunk?: ((result: TChunkResult, idsSubset: TId[]) => void) | undefined;
	/** Ids per chunk. Default 150. */
	chunkSize?: number | undefined;
	/** Max chunks in flight at once. Default 4. */
	concurrency?: number | undefined;
	/**
	 * Keep the caller's id order instead of sorting (default sorts for backend-cache stability).
	 * Use when ordering is meaningful — e.g. loading visible items first for earliest paint.
	 */
	preserveOrder?: boolean | undefined;
	/** Abort the whole load. Stops scheduling and rejects with an AbortError. */
	signal?: AbortSignal | undefined;
}

/** Stable comparator for numbers or strings — deterministic chunking keeps backend caches warm. */
function _defaultCompare<TId>(a: TId, b: TId): number {
	return a < b ? -1 : a > b ? 1 : 0;
}

function _abortError(): DOMException {
	return new DOMException('Chunked load aborted', 'AbortError');
}

/** Fetch a chunk, retrying once on failure (unless aborted in between). */
async function _fetchWithRetry<TId, TChunkResult>(
	fetchChunk: (idsSubset: TId[]) => Promise<TChunkResult>,
	chunk: TId[],
	signal: AbortSignal | undefined,
): Promise<TChunkResult> {
	try {
		return await fetchChunk(chunk);
	} catch (firstErr) {
		if (signal?.aborted) throw _abortError();
		try {
			return await fetchChunk(chunk);
		} catch (_secondErr) {
			throw firstErr; // surface the original error
		}
	}
}

/**
 * Load `ids` in chunks through a bounded concurrency pool.
 *
 * @returns chunk results in chunk order (sorted-id order). Callers merge as appropriate
 *          (concat for arrays, Object.assign for dictionaries).
 */
export async function loadInChunks<TId, TChunkResult>(
	opts: ChunkedLoadOptions<TId, TChunkResult>,
): Promise<TChunkResult[]> {
	const {
		ids,
		fetchChunk,
		onProgress,
		onChunk,
		chunkSize = 150,
		concurrency = 4,
		preserveOrder = false,
		signal,
	} = opts;

	const total = ids.length;
	if (total === 0) return [];
	if (signal?.aborted) throw _abortError();

	// Deterministic chunking: sort a copy so repeated loads of the same set produce the
	// same chunk boundaries → the backend's per-id-set memoization keeps hitting. Callers that
	// need a meaningful order (e.g. visible-first) opt out via preserveOrder.
	const sorted = preserveOrder ? [...ids] : [...ids].sort(_defaultCompare);
	const chunks: TId[][] = [];
	for (let i = 0; i < sorted.length; i += chunkSize) {
		chunks.push(sorted.slice(i, i + chunkSize));
	}

	const results: TChunkResult[] = new Array(chunks.length);
	let loaded = 0;
	let nextIndex = 0;
	let fatal: unknown = null; // set when any chunk fails fatally; stops the whole pool

	async function worker(): Promise<void> {
		while (true) {
			if (signal?.aborted) throw _abortError();
			if (fatal !== null) return; // a sibling chunk already failed — stop scheduling
			const index = nextIndex++;
			if (index >= chunks.length) return;

			const chunk = must(chunks[index], 'index < length (guarded above)');
			let result: TChunkResult;
			try {
				result = await _fetchWithRetry(fetchChunk, chunk, signal);
			} catch (err) {
				fatal = err; // flag before rethrow so siblings stop and stragglers suppress
				throw err;
			}

			// A late-arriving result after an abort or a sibling's fatal failure must not
			// render or report progress into a page that already handled the rejection.
			if (signal?.aborted) throw _abortError();
			if (fatal !== null) return;

			results[index] = result;
			loaded += chunk.length;
			if (onChunk) onChunk(result, chunk);
			if (onProgress) onProgress(loaded, total);
		}
	}

	const poolSize = Math.max(1, Math.min(concurrency, chunks.length));
	await Promise.all(Array.from({ length: poolSize }, () => worker()));

	return results;
}
