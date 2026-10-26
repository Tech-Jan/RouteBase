/**
 * core/invariant.ts — the one assert-helper for module-internal invariants (Phase L).
 *
 * Policy (one sentence): if you cannot say WHY the value must exist, write a real guard instead.
 * must() is for lockstep parallel arrays, indices dominated by an earlier bounds check, post-has()
 * map gets, and test fixtures — places where absence was already a crash/corruption and the throw
 * only names it. Wire data, DOM lookups and user input get control-flow guards (G3 guard-rebind),
 * never must().
 */
export function must<T>(value: T, what: string): NonNullable<T> {
	if (value == null) throw new Error(`invariant: ${what} is ${value === null ? 'null' : 'absent'}`);
	return value;
}
