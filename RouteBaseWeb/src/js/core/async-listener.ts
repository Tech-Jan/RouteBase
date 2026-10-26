/**
 * core/async-listener.ts — the sanctioned bridge from async functions to void callback slots
 * (K5, no-misused-promises). A promise handed to addEventListener / a void-returning option
 * loses its rejection channel: the error becomes an unhandled rejection with no site context.
 * asyncCallback() names the site and routes the rejection to a sink instead.
 *
 * Use it at the BOUNDARY (the point a void slot is filled), not inside logic — the wrapped
 * function itself stays an honest promise for tests and composition.
 */

export type AsyncErrorSink = (label: string, err: unknown) => void;

const consoleSink: AsyncErrorSink = (label, err) => {
	console.error(`async callback failed: ${label}`, err);
};

/**
 * Wrap an async function for a void callback slot. `this` and arguments pass through unchanged
 * (covers `async function (this: …)` listener idioms); a rejection goes to `sink` (default:
 * console.error with the label) instead of an unhandled-rejection event.
 */
export function asyncCallback<This, Args extends unknown[]>(
	label: string,
	fn: (this: This, ...args: Args) => Promise<unknown>,
	sink: AsyncErrorSink = consoleSink,
): (this: This, ...args: Args) => void {
	return function (this: This, ...args: Args): void {
		fn.apply(this, args).catch((err: unknown) => {
			sink(label, err);
		});
	};
}
