// @vitest-environment node
import { must } from './invariant';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { postReview, postTrashRestore } from './review-api';

describe('review-api error handling (LR-05)', () => {
	afterEach(() => vi.unstubAllGlobals());

	it('returns false when fetch rejects (no unhandled rejection, button re-enables)', async () => {
		vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('net')));
		await expect(postReview('approve', { trackIds: [1] })).resolves.toBe(false);
	});

	it('returns false on a non-ok response', async () => {
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));
		await expect(postReview('trash', { trackIds: [1] })).resolves.toBe(false);
	});

	it('returns false when the trash restore fails', async () => {
		vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));
		await expect(postTrashRestore([1])).resolves.toBe(false);
	});
});

describe('review-api endpoints', () => {
	afterEach(() => vi.unstubAllGlobals());

	// Two restores exist and they are not interchangeable: the review one also clears the owning session's
	// 'abandoned' flag, so an auto ride restored through the generic endpoint would be re-purged.
	it('posts each action to its own endpoint', async () => {
		const fetchMock = vi.fn().mockResolvedValue({ ok: true });
		vi.stubGlobal('fetch', fetchMock);

		await postReview('restore', { trackIds: [7] });
		await postTrashRestore([7]);

		expect(must(fetchMock.mock.calls[0], 'first fetch call')[0]).toBe('/api/review/restore');
		const second = must(fetchMock.mock.calls[1], 'second fetch call');
		expect(second[0]).toBe('/api/trash/restore');
		expect(JSON.parse(second[1].body)).toEqual({ trackIds: [7] });
	});
});
