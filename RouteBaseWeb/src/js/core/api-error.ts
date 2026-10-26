/**
 * api-error.ts — what the server said when it refused, as a sentence.
 *
 * Every API refusal answers with a named DTO carrying `{code, message}`: `ApiError` everywhere, and the
 * structurally identical `ShareErrorResponse` on the share slice (two names, one shape — that split is
 * the reason a shared reader is worth having). Three call sites had each grown their own reader of it,
 * which is the rule of three, and the failure mode when they drift is silent: the status line shows raw
 * JSON, or "HTTP 400", instead of the sentence the server wrote for the user.
 *
 * `statusText` is deliberately not a fallback — it is "" on HTTP/2, so it reads as an empty message
 * exactly when a real one is missing.
 */

import type { ApiError } from '../generated/contracts';

/**
 * The message out of a refusal body, or `fallback` when there is none.
 *
 * CONSUMES the response body — call it once, on a response you have already decided is a failure.
 *
 * A body that is not JSON at all (a proxy's 502 page, an unhandled 500 behind a reverse proxy) falls
 * back rather than being shown: dumping an HTML error page into a status line is worse than "HTTP 502".
 */
export async function readApiErrorMessage(response: Response, fallback?: string): Promise<string> {
	const body: unknown = await response.json().catch(() => null);
	const message = (body as Partial<ApiError> | null)?.message;
	if (typeof message === 'string' && message.trim() !== '') return message;

	return fallback ?? `HTTP ${response.status}`;
}
