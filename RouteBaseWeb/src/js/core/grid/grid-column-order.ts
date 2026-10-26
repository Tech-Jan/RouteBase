/**
 * Persisted column ordering for the grid.
 *
 * Stores a list of column keys per table and reorders a column array to match. Keys not in the stored
 * list keep their relative schema order, so newly added columns still appear. Ordering is purely
 * presentational — sort/filter params are keyed by column key, so reordering never touches the grid query
 * or the server contract.
 *
 * A DEVICE preference: it is an arrangement of one screen, so inheriting it on another machine would be
 * wrong. core/device-prefs.ts owns the key and the try/catch that used to be repeated at each access here.
 */

import { devicePrefKeys, getJson, removeDevicePref, setJson } from '../device-prefs';

export class GridColumnOrder {
	private order: string[] = [];
	private readonly persistKey?: string | undefined;

	constructor(persistKey?: string) {
		this.persistKey = persistKey;
		this.load();
	}

	/** Reorder `cols` by the stored order; keys not in the order retain their relative schema order. */
	apply<T extends { key: string }>(cols: T[]): T[] {
		if (this.order.length === 0) return cols;
		const rank = new Map(this.order.map((k, i) => [k, i] as const));
		// Stable sort (ES2019+): equal ranks — including two not-in-order keys (both Infinity) — keep input order.
		return [...cols].sort((a, b) => {
			const ra = rank.get(a.key) ?? Infinity;
			const rb = rank.get(b.key) ?? Infinity;
			return ra === rb ? 0 : ra < rb ? -1 : 1;
		});
	}

	/** Replace the stored order with `keys` and persist. */
	set(keys: string[]): void {
		this.order = [...keys];
		this.persist();
	}

	/** Forget the custom order (columns revert to schema order) and clear persistence. */
	reset(): void {
		this.order = [];
		const k = this.storageKey();
		if (k) removeDevicePref(k);
	}

	private storageKey(): string | undefined {
		return this.persistKey ? devicePrefKeys.gridColumnOrder(this.persistKey) : undefined;
	}

	private load(): void {
		const k = this.storageKey();
		if (!k) return;
		// Element-wise filter, not a cast: a stored array is user-editable text, and one non-string entry
		// would otherwise reach the rank Map as a key nothing matches.
		const raw = getJson<unknown>(k, []);
		if (Array.isArray(raw)) this.order = raw.filter((x): x is string => typeof x === 'string');
	}

	private persist(): void {
		const k = this.storageKey();
		if (k) setJson(k, this.order);
	}
}
