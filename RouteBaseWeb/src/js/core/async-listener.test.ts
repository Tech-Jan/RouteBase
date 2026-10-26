// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import { asyncCallback } from './async-listener';

describe('asyncCallback', () => {
	it('passes this and arguments through and swallows nothing on success', async () => {
		const seen: unknown[] = [];
		const ctx = { tag: 'ctx' };
		const wrapped = asyncCallback('t', async function (this: { tag: string }, a: number, b: string) {
			seen.push(this.tag, a, b);
		});
		wrapped.call(ctx, 7, 'x');
		await Promise.resolve();
		expect(seen).toEqual(['ctx', 7, 'x']);
	});

	it('routes a rejection to the sink with the label instead of rejecting', async () => {
		const sink = vi.fn();
		const boom = new Error('boom');
		const wrapped = asyncCallback('save-click', () => Promise.reject(boom), sink);
		wrapped();
		await Promise.resolve();
		expect(sink).toHaveBeenCalledWith('save-click', boom);
	});

	it('defaults the sink to console.error with the label', async () => {
		const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
		const wrapped = asyncCallback('load', () => Promise.reject(new Error('nope')));
		wrapped();
		await Promise.resolve();
		expect(spy).toHaveBeenCalledWith('async callback failed: load', expect.any(Error));
		spy.mockRestore();
	});
});
