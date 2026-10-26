import { describe, expect, it } from 'vitest';
import { readApiErrorMessage } from './api-error';

/**
 * The shared reader for `{code, message}` refusal bodies.
 *
 * Three call sites had grown their own — activity delete, the share-link mint dialog and the race
 * riders panel — and each got a different answer for the same server response. What the user sees when
 * this drifts is raw JSON in a status line, or "HTTP 400" where the server wrote a real sentence.
 */
describe('readApiErrorMessage', () => {
	it('reads the message out of an ApiError body', async () => {
		const response = new Response(
			JSON.stringify({ code: 'bad_request', message: "A rider's name must be 255 characters or fewer." }),
			{ status: 400 });

		expect(await readApiErrorMessage(response))
			.toBe("A rider's name must be 255 characters or fewer.");
	});

	it('reads ShareErrorResponse too — two contract names, one shape', async () => {
		const response = new Response(
			JSON.stringify({ message: 'You do not own that segment.', code: 'badRequest' }), { status: 403 });

		expect(await readApiErrorMessage(response)).toBe('You do not own that segment.');
	});

	it('falls back on a body that is not JSON at all', async () => {
		// A proxy's 502 page. Showing it would put HTML in a status line — worse than saying "HTTP 502".
		const response = new Response('<html><body>Bad Gateway</body></html>', { status: 502 });

		expect(await readApiErrorMessage(response)).toBe('HTTP 502');
	});

	it('falls back on an empty body', async () => {
		expect(await readApiErrorMessage(new Response('', { status: 403 }))).toBe('HTTP 403');
	});

	it('falls back when the body is JSON but carries no message', async () => {
		const response = new Response(JSON.stringify({ code: 'bad_request' }), { status: 400 });

		expect(await readApiErrorMessage(response)).toBe('HTTP 400');
	});

	it('treats a blank message as absent — a whitespace status line says nothing', async () => {
		const response = new Response(JSON.stringify({ code: 'bad_request', message: '   ' }), { status: 400 });

		expect(await readApiErrorMessage(response)).toBe('HTTP 400');
	});

	it('prefers the caller\'s fallback over the status code', async () => {
		const response = new Response('', { status: 500 });

		expect(await readApiErrorMessage(response, 'Could not create a share link.'))
			.toBe('Could not create a share link.');
	});

	it('still prefers the server\'s message over the caller\'s fallback', async () => {
		const response = new Response(
			JSON.stringify({ code: 'rate_limited', message: 'Too many links today.' }), { status: 429 });

		expect(await readApiErrorMessage(response, 'Could not create a share link.'))
			.toBe('Too many links today.');
	});
});
