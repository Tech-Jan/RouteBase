/**
 * The single sanctioned reader/writer for data-* attributes (G3). A raw `.dataset.` read is a
 * hidden Razor↔TS contract with inline coercion — `parseFloat(el.dataset.x!)` turns a missing
 * attribute into NaN (or a lie via `!`) with nothing naming the broken key. These helpers make the
 * contract explicit: absence is null (or a thrown error that NAMES the key), numbers are
 * NaN-guarded, and the type-quality ratchet forbids `.dataset.` everywhere else.
 */

/** Optional string read — null when the attribute is absent. Empty string is a present value. */
export function readData(el: HTMLElement, key: string): string | null {
	const v = el.dataset[key];
	return v === undefined ? null : v;
}

/** Required string read — throws naming the key (a missing attr is a markup↔TS contract break). */
export function requireData(el: HTMLElement, key: string): string {
	const v = el.dataset[key];
	if (v === undefined) throw new Error(`data-attr missing: data-${kebab(key)} on <${el.tagName.toLowerCase()}>`);
	return v;
}

/** Required numeric read — throws naming the key when absent or not a number. */
export function readDataNumber(el: HTMLElement, key: string): number {
	const n = Number.parseFloat(requireData(el, key));
	if (Number.isNaN(n)) throw new Error(`data-attr not numeric: data-${kebab(key)} = "${el.dataset[key]}"`);
	return n;
}

/** Optional numeric read — null when absent; throws when present but not a number. */
export function readDataNumberOrNull(el: HTMLElement, key: string): number | null {
	return el.dataset[key] === undefined ? null : readDataNumber(el, key);
}

/** Optional integer read with a default — the "parseInt(x ?? '0') || 0" bootstrap pattern, named. */
export function readDataInt(el: HTMLElement, key: string, fallback: number): number {
	const v = el.dataset[key];
	if (v === undefined) return fallback;
	const n = Number.parseInt(v, 10);
	return Number.isNaN(n) ? fallback : n;
}

/** Write a data-* value (numbers stringified — the attribute is a string either way). */
export function setData(el: HTMLElement, key: string, value: string | number): void {
	el.dataset[key] = String(value);
}

/** Remove a data-* attribute (reads then answer null). */
export function clearData(el: HTMLElement, key: string): void {
	delete el.dataset[key];
}

/** The ATTRIBUTE name for a dataset key (`trackId` → `data-track-id`) — for building selectors. */
export function dataAttrName(key: string): string {
	return `data-${kebab(key)}`;
}

function kebab(key: string): string {
	return key.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`);
}
