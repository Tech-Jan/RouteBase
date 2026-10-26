// endpoint-types.ts — compiler-bind each fetch site's body type to its OpenAPI endpoint (D6, phase 3).
//
// D6 keeps the hand-written `request<T>`/`asJson<T>` wrappers and the dual signed-in/share routing (no
// openapi-fetch, no runtime dependency in a `deps: []` project). This module supplies the one missing
// piece: instead of asserting a response type by hand — `request<SaveResponse>(url)`, an unchecked claim —
// a call site names its ENDPOINT and the type FOLLOWS from the generated `paths`:
//
//     request<ResponseOf<'/api/edit-sessions/{id}/save', 'post'>>(url)
//
// The runtime URL is still built exactly as before; only the type argument changes. `P` must be a key of
// the generated `paths` and `M` a method defined on it, so a wrong path template or verb is a COMPILE error
// (proven once deliberately, by compiling a wrong verb). What stays unchecked is
// that the runtime string matches the template — enforcing that needs a runtime builder, a deliberate D6
// non-goal.
//
// Hand-written (NOT emitted by gen-contracts.mjs) so it is outside the drift gate: it depends only on the
// SHAPE of `paths`, which openapi-typescript keeps stable, not on any one endpoint.

import type { paths } from '../generated/openapi-types';

/** The HTTP verbs an operation object can live under in `paths[P]`. */
type HttpMethod = 'get' | 'put' | 'post' | 'delete' | 'options' | 'head' | 'patch' | 'trace';

/** The verbs actually defined (carry an operation, not `never`) on path `P` — the friendly `M` constraint. */
export type MethodOf<P extends keyof paths> = {
	[M in HttpMethod]: paths[P][M] extends { responses: unknown } ? M : never;
}[HttpMethod];

/** The `application/json` body an operation returns for `Status`, or `never` if that status has no JSON body. */
type JsonResponseOf<Op, Status extends number> = Op extends { responses: infer R }
	? Status extends keyof R
		? R[Status] extends { content: { 'application/json': infer T } }
			? T
			: never
		: never
	: never;

/**
 * The response body type of endpoint `P` · method `M` (status 200 unless overridden). Bind a fetch site to
 * it: `request<ResponseOf<'/api/edit-commits/{commitId}', 'get'>>(url)`. A wrong path or verb is a compile
 * error; a status with no JSON body resolves to `never` (a 204 site keeps its own `void`/`undefined`).
 */
export type ResponseOf<
	P extends keyof paths,
	M extends MethodOf<P>,
	Status extends number = 200,
> = JsonResponseOf<paths[P][M], Status>;

/** The `application/json` request body an operation accepts, or `never` if it takes none. */
type JsonRequestOf<Op> = Op extends { requestBody?: { content: { 'application/json': infer T } } } ? T : never;

/**
 * The request body type of endpoint `P` · method `M`. Symmetric with `ResponseOf` for POST/PUT sites that
 * build a typed body: `const body: RequestOf<'/api/edit-sessions', 'post'> = { activityIds }`.
 */
export type RequestOf<P extends keyof paths, M extends MethodOf<P>> = JsonRequestOf<paths[P][M]>;
