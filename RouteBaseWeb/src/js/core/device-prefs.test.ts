import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
	clearAllDevicePrefs, devicePrefKeys, getBool, getEnum, getJson, getNumber, listDevicePrefKeys,
	removeDevicePref, setBool, setEnum, setJson, setNumber, sweepLegacyDevicePrefs,
} from './device-prefs';

/**
 * core/device-prefs.ts — the only module allowed to touch web storage.
 *
 * Two properties are what this file buys, and both are invisible when they break:
 *
 *   1. KEY COMPOSITION IS THE STORAGE CONTRACT. Everything lands under `rb1.`, because the prefix IS the
 *      version — a break bumps it and every old key becomes unreachable in one edit. A caller that
 *      slipped an unprefixed key in would still work, and would then be invisible to both the reset
 *      button and the next version bump.
 *   2. A THROW NEVER PROPAGATES. localStorage throws on quota, and in some privacy modes on the very
 *      first access. An unguarded write does not degrade a preference — it takes down the feature that
 *      was mid-call. That is the bug two of the seven old call sites actually had.
 */

function clearStorage(): void {
	localStorage.clear();
}

beforeEach(clearStorage);
afterEach(() => {
	vi.restoreAllMocks();
	clearStorage();
});

describe('device-prefs — key composition is the storage contract', () => {
	it('writes every value under the rb1. version prefix', () => {
		setBool('card.collapsed.speed', true);

		expect(localStorage.getItem('rb1.card.collapsed.speed')).toBe('true');
		// Unprefixed must NOT exist: a caller reading the bare key would survive a version bump that is
		// supposed to abandon it.
		expect(localStorage.getItem('card.collapsed.speed')).toBeNull();
	});

	it('lists stored keys unprefixed, and only its own', () => {
		setBool('a', true);
		setNumber('b', 3);
		localStorage.setItem('someOtherApp.thing', 'x');   // a co-tenant on this origin

		expect(listDevicePrefKeys().sort()).toEqual(['a', 'b']);
	});

	it('the registry names every family it stores', () => {
		expect(devicePrefKeys.cardCollapsed('speedChart')).toBe('card.collapsed.speedChart');
		expect(devicePrefKeys.navGroupOpen('/Segments')).toBe('nav.group./Segments.open');
		expect(devicePrefKeys.gridColumnWidths('activities')).toBe('grid.activities.widths');
		expect(devicePrefKeys.gridColumnOrder('activities')).toBe('grid.activities.order');
		expect(devicePrefKeys.drawerOpen('raceCharts')).toBe('drawer.raceCharts.open');
		expect(devicePrefKeys.drawerHeight('raceCharts')).toBe('drawer.raceCharts.height');
		expect(devicePrefKeys.raceSidebarWidth).toBe('race.sidebar.width');
	});
});

describe('device-prefs — typed accessors', () => {
	it('round-trips each type', () => {
		setBool('b', false);
		setNumber('n', 42.5);
		setJson('j', { widths: { name: 120 } });

		expect(getBool('b', true)).toBe(false);
		expect(getNumber('n', 0)).toBe(42.5);
		expect(getJson('j', {})).toEqual({ widths: { name: 120 } });
	});

	it('returns the caller fallback for an absent key, in both directions', () => {
		// Both directions matter: the drawer defaults OPEN and the cards default EXPANDED, so a getBool
		// that always fell to false would silently collapse every drawer in the app.
		expect(getBool('missing', true)).toBe(true);
		expect(getBool('missing', false)).toBe(false);
		expect(getNumber('missing', 7)).toBe(7);
		expect(getJson('missing', ['x'])).toEqual(['x']);
	});

	it('keeps a stored zero, which is a value and not a missing key', () => {
		setNumber('n', 0);

		// `Number(raw) || fallback` is the mistake this is written longhand to avoid — a deliberate zero
		// height or zero width would come back as the default forever.
		expect(getNumber('n', 99)).toBe(0);
	});

	it('getEnum keeps every member of its closed set and rejects anything else', () => {
		const MODES = ['auto', 'on', 'off'] as const;

		for (const m of MODES) {
			setEnum('m', m);
			expect(getEnum('m', MODES, 'auto')).toBe(m);
		}

		localStorage.setItem('rb1.m', 'sometimes');
		expect(getEnum('m', MODES, 'auto')).toBe('auto');
	});

	it('getEnum distinguishes an absent tri-state from a stored one', () => {
		const MODES = ['auto', 'on', 'off'] as const;

		// THE property a boolean cannot have: three distinguishable states, so "never chosen" and
		// "chosen off" stay different answers. Absent is the fallback; 'off' is not absent.
		expect(getEnum('m', MODES, 'auto')).toBe('auto');
		setEnum('m', 'off');
		expect(getEnum('m', MODES, 'auto')).toBe('off');
	});

	it('falls back rather than throwing on a garbage value', () => {
		localStorage.setItem('rb1.n', 'not-a-number');
		localStorage.setItem('rb1.j', '{ this is not json');
		localStorage.setItem('rb1.b', 'yes');

		expect(getNumber('n', 5)).toBe(5);
		expect(getJson('j', { ok: true })).toEqual({ ok: true });
		expect(getBool('b', true)).toBe(true);    // unparseable → fallback, not a coerced false
	});
});

describe('device-prefs — a storage throw never reaches the caller', () => {
	it('survives a getItem that throws (privacy mode)', () => {
		vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
			throw new DOMException('denied', 'SecurityError');
		});

		expect(getBool('b', true)).toBe(true);
		expect(getNumber('n', 3)).toBe(3);
		expect(getJson('j', null)).toBeNull();
	});

	it('survives a setItem that throws (quota exceeded)', () => {
		vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
			throw new DOMException('quota', 'QuotaExceededError');
		});

		expect(() => setBool('b', true)).not.toThrow();
		expect(() => setNumber('n', 1)).not.toThrow();
		expect(() => setJson('j', { a: 1 })).not.toThrow();
	});

	it('survives a removeItem that throws', () => {
		setBool('b', true);
		vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
			throw new DOMException('denied', 'SecurityError');
		});

		expect(() => removeDevicePref('b')).not.toThrow();
		expect(() => clearAllDevicePrefs()).not.toThrow();
	});

	it('does not throw on an unserialisable value', () => {
		const circular: Record<string, unknown> = {};
		circular['self'] = circular;

		expect(() => setJson('j', circular)).not.toThrow();
	});
});

describe('device-prefs — clearing and the legacy sweep', () => {
	it('clearAllDevicePrefs removes every rb1. key and reports the count', () => {
		setBool('a', true);
		setNumber('b', 1);
		setJson('c', []);
		localStorage.setItem('someOtherApp.thing', 'keep me');

		expect(clearAllDevicePrefs()).toBe(3);
		expect(listDevicePrefKeys()).toEqual([]);
		// Origin-scoped storage: clearing OUR prefs must never be localStorage.clear().
		expect(localStorage.getItem('someOtherApp.thing')).toBe('keep me');
	});

	it('clearAllDevicePrefs removes ALL of them, not every other one', () => {
		// Deleting while indexing by position renumbers the remaining keys — the classic off-by-half. Five
		// keys is enough that a collect-then-delete bug leaves survivors.
		for (const k of ['a', 'b', 'c', 'd', 'e']) setBool(k, true);

		expect(clearAllDevicePrefs()).toBe(5);
		expect(listDevicePrefKeys()).toEqual([]);
	});

	it('sweeps the pre-registry key families and leaves everything else alone', () => {
		localStorage.setItem('routebase_card_collapsed_speedChart', 'true');
		localStorage.setItem('sidebar.segments.open./Segments', 'true');
		localStorage.setItem('gridcolw:activities', '{}');
		localStorage.setItem('gridcolorder:activities', '[]');
		localStorage.setItem('raceChartsDrawer', 'false');
		localStorage.setItem('raceChartsDrawerHeight', '200');   // a bare prefix covers its suffixes
		localStorage.setItem('raceReplaySidebarWidth', '300');
		localStorage.setItem('somethingElseEntirely', 'keep me');

		expect(sweepLegacyDevicePrefs()).toBe(7);
		expect(localStorage.getItem('routebase_card_collapsed_speedChart')).toBeNull();
		expect(localStorage.getItem('raceChartsDrawerHeight')).toBeNull();
		// The explicit prefix list is the point: localStorage is ORIGIN-scoped, so "delete anything not
		// rb1." would take a co-tenant's data with it.
		expect(localStorage.getItem('somethingElseEntirely')).toBe('keep me');
	});

	it('sweeps once per device, then costs one read', () => {
		localStorage.setItem('gridcolw:activities', '{}');

		expect(sweepLegacyDevicePrefs()).toBe(1);

		// A key that looks legacy but appears after the sweep is NOT re-collected — the sentinel means the
		// scan does not run again. Nothing writes those families any more, so this is the correct trade.
		localStorage.setItem('gridcolw:segments', '{}');
		expect(sweepLegacyDevicePrefs()).toBe(0);
		expect(localStorage.getItem('gridcolw:segments')).toBe('{}');
	});

	it('does not record the sweep when storage is unavailable, so a working device still gets it', () => {
		vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
			throw new DOMException('denied', 'SecurityError');
		});
		localStorage.setItem('gridcolw:activities', '{}');

		expect(sweepLegacyDevicePrefs()).toBe(0);
		expect(getBool('legacy.swept', false)).toBe(false);
	});
});
