// @vitest-environment node
import { describe, expect, it } from 'vitest';
import type { EditScript } from './edit-ops';
import { TRACK_EDIT_LIMITS } from './track-edit-errors';
import {
	parseOverviewDate, orderForSession, effectiveLoadedIds, slot0ActivityId, pickableIds,
	pickerBarState, flowAState, editorPickBarState, buildEditorUrl, buildActivitiesPickerUrl,
	readPickForParam, readEditorPickParam, PICK_FOR_PARAM,
} from './entry-picker-core';

// Small helpers so tests read declaratively.
const load = (activityId: number): EditScript['ops'][number] => ({ op: 'loadActivity', activityId });
const script = (...ops: EditScript['ops']): EditScript => ({ schemaVersion: 1, ops });
/** startEpochOf backed by a plain map — proves ordering imports no GPS cache (T-EP14). */
const epochOf = (m: Record<number, number | null>) => (id: number): number | null => m[id] ?? null;

describe('entry-picker-core', () => {
	// T-EP1
	it('parseOverviewDate parses valid dd.MM.yyyy HH:mm and rejects garbage', () => {
		expect(parseOverviewDate('05.03.2026 14:07')).toBe(Date.UTC(2026, 2, 5, 14, 7));
		expect(parseOverviewDate('')).toBeNull();
		expect(parseOverviewDate(undefined)).toBeNull();
		expect(parseOverviewDate(null)).toBeNull();
		expect(parseOverviewDate('not a date')).toBeNull();
		expect(parseOverviewDate('99.99.9999 99:99')).toBeNull();
		expect(parseOverviewDate('2026-03-05 14:07')).toBeNull(); // wrong separator/order
	});

	// T-EP2
	it('orderForSession sorts chronologically, earliest first (slot 0)', () => {
		const starts = epochOf({ 7: 300, 3: 100, 5: 200 });
		expect(orderForSession([7, 3, 5], starts)).toEqual([3, 5, 7]);
	});

	// T-EP3
	it('orderForSession puts unknown-start ids last, id-ascending', () => {
		const starts = epochOf({ 5: 200, 3: 100, 9: null, 2: null });
		expect(orderForSession([9, 5, 2, 3], starts)).toEqual([3, 5, 2, 9]);
	});

	// T-EP4
	it('orderForSession breaks ties by id-ascending', () => {
		const starts = epochOf({ 8: 100, 2: 100, 5: 100 });
		expect(orderForSession([8, 2, 5], starts)).toEqual([2, 5, 8]);
	});

	// T-EP5
	it('effectiveLoadedIds respects the cursor and ignores non-load ops', () => {
		const s = script(load(3), { op: 'splitAt', slot: 0, index: 4 }, load(7), load(9));
		expect(effectiveLoadedIds(s, 3)).toEqual([3, 7]); // op[2]=load(7) is index<3; load(9) at 3 excluded
		expect(effectiveLoadedIds(s, 4)).toEqual([3, 7, 9]);
		expect(effectiveLoadedIds(s, 1)).toEqual([3]);
		expect(effectiveLoadedIds(s, 0)).toEqual([]);
	});

	// T-EP6
	it('slot0ActivityId is effective-first, falls back to full script, null when no load', () => {
		const s = script(load(3), load(7));
		expect(slot0ActivityId(s, 2)).toBe(3);          // effective first load
		expect(slot0ActivityId(s, 0)).toBe(3);          // E-EP15: cursor 0 → full-script fallback
		const noLoad = script({ op: 'splitAt', slot: 0, index: 1 });
		expect(slot0ActivityId(noLoad, 1)).toBeNull();
	});

	// T-EP7
	it('pickableIds subtracts in-session ids, preserving input order', () => {
		expect(pickableIds([5, 3, 9, 1], [3, 1])).toEqual([5, 9]);
	});

	// T-EP8
	it('pickableIds returns [] for null and empty highlight (never select-all)', () => {
		expect(pickableIds(null, [1, 2])).toEqual([]);
		expect(pickableIds([], [1, 2])).toEqual([]);
	});

	// T-EP9
	it('pickerBarState enablement matrix', () => {
		// 0 selected (empty highlight) → disabled, filter/paint hint
		const empty = pickerBarState({ highlight: [], inSession: [], slotCount: 0, cursor: 0 });
		expect(empty.enabled).toBe(false);
		expect(empty.reason).toBe('filter or paint on the map to select');

		// all selected already in session → disabled with that reason
		const allIn = pickerBarState({ highlight: [4, 5], inSession: [4, 5], slotCount: 2, cursor: 2 });
		expect(allIn.enabled).toBe(false);
		expect(allIn.reason).toBe('all selected are already in the session');
		expect(allIn.message).toBe('0 selected · 2 already in session');

		// fits → enabled
		const fits = pickerBarState({ highlight: [7, 8], inSession: [], slotCount: 1, cursor: 1 });
		expect(fits.enabled).toBe(true);
		expect(fits.reason).toBeNull();
		expect(fits.addLabel).toBe('Add 2');

		// more addable than free slots → disabled with slot count (2 free: cap − 2 slots occupied)
		const nearCap = TRACK_EDIT_LIMITS.maxSlots - 2;
		const overSlots = pickerBarState({ highlight: [1, 2, 3], inSession: [], slotCount: nearCap, cursor: nearCap });
		expect(overSlots.enabled).toBe(false);
		expect(overSlots.reason).toBe('only 2 slots free');

		// over maxOps headroom (cursor + N > 500) → disabled
		const overOps = pickerBarState({ highlight: [1, 2], inSession: [], slotCount: 0, cursor: 499 });
		expect(overOps.enabled).toBe(false);
		expect(overOps.reason).toBe('session edit limit reached');
	});

	// T-EP10
	it('flowAState is hidden when empty, enabled up to the cap, disabled over it', () => {
		expect(flowAState(null).hidden).toBe(true);
		expect(flowAState([]).hidden).toBe(true);

		const three = flowAState([3, 1, 2]);
		expect(three).toMatchObject({ hidden: false, enabled: true, label: 'Open 3 in editor', title: null });

		const cap = TRACK_EDIT_LIMITS.maxSlots;
		const atCap = flowAState(Array.from({ length: cap }, (_v, i) => i + 1));
		expect(atCap.enabled).toBe(true);

		const overCap = flowAState(Array.from({ length: cap + 1 }, (_v, i) => i + 1));
		expect(overCap.hidden).toBe(false);
		expect(overCap.enabled).toBe(false);
		expect(overCap.label).toBe(`Open ${cap + 1} in editor`);
		expect(overCap.title).toBe(`max ${cap} tracks`);
	});

	// T-EP10b — front-door bar (nav ?editorPick=1). Intent holds, contract changed: unified rule — selection
	// is the effective highlight (marks ?? paint ?? filter); canClear is driven by whether marks exist.
	it('editorPickBarState counts the highlight, hint at 0, enabled to the cap; canClear = hasMarks', () => {
		const zero = editorPickBarState(null, false);
		expect(zero).toMatchObject({ selectedCount: 0, enabled: false, openLabel: 'Open in editor', canClear: false });
		expect(zero.reason).toBe('check rows or paint the map to select tracks');
		expect(editorPickBarState([], false).enabled).toBe(false);

		// Highlighted by paint (2 tracks), no explicit marks → selectable but nothing to Clear.
		const paintOnly = editorPickBarState([7, 8], false);
		expect(paintOnly).toMatchObject({ selectedCount: 2, enabled: true, openLabel: 'Open 2 in editor', reason: null, canClear: false });

		// Same count but with marks present → Clear becomes available.
		expect(editorPickBarState([7], true).canClear).toBe(true);
		expect(editorPickBarState([7], true).message).toBe('1 track selected');

		const cap = TRACK_EDIT_LIMITS.maxSlots;
		expect(editorPickBarState(Array.from({ length: cap }, (_v, i) => i + 1), false).enabled).toBe(true);
		const over = editorPickBarState(Array.from({ length: cap + 1 }, (_v, i) => i + 1), true);
		expect(over.enabled).toBe(false);
		expect(over.reason).toBe(`max ${cap} tracks`);
		expect(over.canClear).toBe(true); // still clearable when over cap
	});

	// T-EP10c
	it('readEditorPickParam is true only for editorPick=1', () => {
		expect(readEditorPickParam('?editorPick=1')).toBe(true);
		expect(readEditorPickParam('?editorPick=1&foo=2')).toBe(true);
		expect(readEditorPickParam('?editorPick=0')).toBe(false);
		expect(readEditorPickParam('?editorPick=')).toBe(false);
		expect(readEditorPickParam('?foo=1')).toBe(false);
		expect(readEditorPickParam('')).toBe(false);
	});

	// T-EP11
	it('buildEditorUrl has the exact editor URL shape', () => {
		expect(buildEditorUrl(42, 'sess-1')).toBe('/Activities/Details/42?editSession=sess-1');
	});

	// T-EP12
	it('buildActivitiesPickerUrl and readPickForParam round-trip; absent/empty → null', () => {
		const url = buildActivitiesPickerUrl('abc 123');
		expect(url).toBe('/Activities?pickFor=abc%20123');
		const search = url.slice(url.indexOf('?'));
		expect(readPickForParam(search)).toBe('abc 123');
		expect(readPickForParam('?foo=1')).toBeNull();
		expect(readPickForParam(`?${PICK_FOR_PARAM}=`)).toBeNull();
		expect(readPickForParam('')).toBeNull();
	});

	// T-EP13
	it('pickerBarState counts split-off slots via slotCount (free = maxSlots − slotCount)', () => {
		// 3 slots occupied (2 loads + 1 split-off) → free = cap − 3
		const free = TRACK_EDIT_LIMITS.maxSlots - 3;
		const ids = (n: number): number[] => Array.from({ length: n }, (_v, i) => i + 1);

		const over = pickerBarState({ highlight: ids(free + 1), inSession: [], slotCount: 3, cursor: 3 });
		expect(over.enabled).toBe(false);
		expect(over.reason).toBe(`only ${free} slots free`);
		// exactly `free` addable → fits
		const fits = pickerBarState({ highlight: ids(free), inSession: [], slotCount: 3, cursor: 3 });
		expect(fits.enabled).toBe(true);
	});

	// T-EP14
	it('orderForSession uses only the injected lookup (mock-free, no cache import)', () => {
		const seen: number[] = [];
		const starts = (id: number): number | null => { seen.push(id); return id === 2 ? null : id * 10; };
		expect(orderForSession([3, 1, 2], starts)).toEqual([1, 3, 2]);
		expect(seen.sort((a, b) => a - b)).toEqual([1, 2, 3]); // every id resolved through the injected fn
	});
});
