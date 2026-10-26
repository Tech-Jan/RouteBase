/**
 * core/device-prefs.ts — DEVICE preferences, and the only module in the app that touches web storage.
 *
 * The tier rule, which is the whole reason this file is separate from core/account-settings.ts:
 *
 *   ACCOUNT if the same user on a new device would be annoyed to LOSE it — taste, or a physical fact
 *           about the person. Those live on the server and arrive as the #appSettings island.
 *   DEVICE  if they would be annoyed to INHERIT it — a position, a size, an open/closed, an ordering on
 *           one screen. Those live here and NEVER reach the server.
 *
 * Before this, device state was 7 ad-hoc key families across 7 modules, two of them with no try/catch at
 * all. That is two bugs, not one:
 *
 *   1. localStorage THROWS — on quota, and in some privacy modes it throws on the very first access. An
 *      unguarded write does not degrade the preference, it takes down whatever feature was mid-call. Every
 *      access below is wrapped, and a failure is always "you get the fallback", never an exception.
 *   2. Ad-hoc keys are unfindable. Nothing could enumerate what this origin stores, so "reset this
 *      device's layout" was unwritable — you cannot clear what you cannot list.
 *
 * THE PREFIX IS THE VERSION. Everything is stored under `rb1.`; a breaking change to any stored shape
 * bumps it to `rb2.` and every old key becomes unreachable in one edit. That is why there is no migration
 * code here and never should be — a device preference is, by definition, cheap to lose.
 *
 * Keys are passed in UNPREFIXED and prefixed here, so no call site can opt out of the version.
 */

/** The version. Bump to `rb2.` to abandon every stored value at once — see the note above. */
const PREFIX = 'rb1.';

/**
 * Every device-pref key family. A family is a named function rather than an inline template so a search
 * for `devicePrefKeys.cardCollapsed` finds every user of that family — the variable part is data, the
 * name is not. This is the registry the old `_COLLAPSE_PREFIX + key` idiom could not provide.
 */
export const devicePrefKeys = {
	/** Chart/panel card collapse state, per card key. */
	cardCollapsed: (card: string): string => `card.collapsed.${card}`,
	/** Sidebar nav group expansion, per group href. */
	navGroupOpen: (group: string): string => `nav.group.${group}.open`,
	/** User-dragged column widths for one grid, as a JSON object. */
	gridColumnWidths: (table: string): string => `grid.${table}.widths`,
	/** User-dragged column ORDER for one grid, as a JSON array of column keys. */
	gridColumnOrder: (table: string): string => `grid.${table}.order`,
	/** Replay chart drawer open/closed, per drawer. */
	drawerOpen: (drawer: string): string => `drawer.${drawer}.open`,
	/** Replay chart drawer dragged height in px, per drawer. */
	drawerHeight: (drawer: string): string => `drawer.${drawer}.height`,
	/** Race replay follow-sidebar width in px. One per app, so not a function. */
	raceSidebarWidth: 'race.sidebar.width',
	/** Playback multiplier last chosen for a replay kind (activity / segment / race). */
	replaySpeed: (replay: string): string => `replay.${replay}.speed`,
	/** Which quantity a chart group puts on X (segment charts: time vs % complete). */
	chartXAxis: (chart: string): string => `chart.${chart}.xaxis`,
	/** Aggregate view for a chart group — TRI-STATE, see the note on getEnum. */
	chartAggregate: (chart: string): string => `chart.${chart}.aggregate`,
	/** Which series a chart group draws, as a JSON array of series keys. */
	chartSeries: (chart: string): string => `chart.${chart}.series`,
	/** How much processing a chart shows (recorded / smoothed / full) — TRI-STATE, see getEnum. */
	chartSeriesMode: (chart: string): string => `chart.${chart}.seriesmode`,
	/** How a map treats rows the table filtered out (all / highlight / hide). */
	mapDisplayMode: (map: string): string => `map.${map}.display`,
	/** Whether a map draws rider names. Sits beside mapDisplayMode — both are "how this map looks to me". */
	mapNamesOn: (map: string): string => `map.${map}.names`,
} as const;

/**
 * Key families this app used BEFORE the registry, listed explicitly so the sweep can remove them.
 *
 * Explicit, and never "delete anything that is not rb1.": localStorage is ORIGIN-scoped, so on a shared
 * origin a blanket sweep would delete another app's data. Each entry is matched with startsWith, so a
 * bare key also covers its own suffixed variants (`raceChartsDrawer` covers `raceChartsDrawerHeight`).
 */
const LEGACY_PREFIXES = [
	'sidebar.segments.open.',      // dashboard-nav group expansion
	'routebase_card_collapsed_',   // collapsible-card
	'gridcolw:',                   // grid-column-sizer
	'gridcolorder:',               // grid-column-order
	'raceChartsDrawer',            // + …Height
	'activityReplayChartsDrawer',  // + …Height
	'segReplayChartsDrawer',       // + …Height
	'raceReplaySidebarWidth',      // race-replay-overlay
];

/** Sentinel recording that the legacy sweep has run, so it costs one read per page instead of a scan. */
const SWEPT_KEY = 'legacy.swept';

function read(key: string): string | null {
	try {
		return localStorage.getItem(PREFIX + key);
	} catch {
		return null;   // storage unavailable (private mode, disabled, quota-locked)
	}
}

function write(key: string, value: string): void {
	try {
		localStorage.setItem(PREFIX + key, value);
	} catch { /* quota or unavailable — a device pref is never worth an exception */ }
}

/** Forget one preference. */
export function removeDevicePref(key: string): void {
	try {
		localStorage.removeItem(PREFIX + key);
	} catch { /* ignore */ }
}

// ── The typed accessors ──────────────────────────────────────────────────────────────────────
// Typed rather than one getString, because every caller would otherwise reimplement the same parse —
// and get the zero case wrong (see getNumber) or the tri-state wrong (see getEnum).

export function getBool(key: string, fallback: boolean): boolean {
	const raw = read(key);
	if (raw === 'true') return true;
	if (raw === 'false') return false;
	return fallback;   // absent OR unparseable → the caller's default, never a coerced false
}

export function setBool(key: string, value: boolean): void {
	write(key, value ? 'true' : 'false');
}

/**
 * A stored `0` is a VALUE, not a missing key — the same trap core/account-settings is written longhand to
 * avoid. `Number(raw) || fallback` would silently discard a deliberate zero height or zero width.
 */
export function getNumber(key: string, fallback: number): number {
	const raw = read(key);
	if (raw === null || raw.trim() === '') return fallback;
	const n = Number(raw);
	return Number.isFinite(n) ? n : fallback;
}

export function setNumber(key: string, value: number): void {
	write(key, String(value));
}

/**
 * A closed set of string values — the client mirror of the server's ChoiceSetting.Coerce.
 *
 * This is the accessor a TRI-STATE needs, and the tri-states are why it exists. `aggregateMode` is
 * `'auto' | 'on' | 'off'`: it auto-engages past an effort threshold, so "the user has not chosen"
 * (auto) and "the user chose off" are DIFFERENT states. Stored as a boolean they collapse — absent
 * would have to mean one of them, and whichever you pick, the other becomes unreachable. That is the
 * same "what does absent mean" defect the settings island had, and it is why a boolean is banned here.
 *
 * A value outside the set degrades to the fallback, exactly like a malformed number.
 */
export function getEnum<T extends string>(key: string, allowed: readonly T[], fallback: T): T {
	const raw = read(key);
	return allowed.includes(raw as T) ? (raw as T) : fallback;
}

export function setEnum<T extends string>(key: string, value: T): void {
	write(key, value);
}

/** Structured prefs (grid widths, column order). Malformed JSON degrades to the fallback, never throws. */
export function getJson<T>(key: string, fallback: T): T {
	const raw = read(key);
	if (raw === null) return fallback;
	try {
		return JSON.parse(raw) as T;
	} catch {
		return fallback;
	}
}

export function setJson(key: string, value: unknown): void {
	try {
		write(key, JSON.stringify(value));
	} catch { /* circular or unserialisable — the caller's shape is a bug, not a reason to throw here */ }
}

// ── Enumeration: the thing ad-hoc keys made impossible ───────────────────────────────────────

/** Every `rb1.` key currently stored, UNPREFIXED. Empty when storage is unavailable. */
export function listDevicePrefKeys(): string[] {
	const keys: string[] = [];
	try {
		for (let i = 0; i < localStorage.length; i++) {
			const full = localStorage.key(i);
			if (full !== null && full.startsWith(PREFIX)) keys.push(full.slice(PREFIX.length));
		}
	} catch { /* storage unavailable → nothing is stored as far as anyone can tell */ }
	return keys;
}

/**
 * Forget every device preference — what the settings page's "Reset this device's layout" calls. Returns
 * how many keys went, so the caller can tell the user something true.
 *
 * Collects first and deletes after: removing while indexing by position renumbers the remaining keys and
 * skips every other one.
 */
export function clearAllDevicePrefs(): number {
	const keys = listDevicePrefKeys();
	for (const key of keys) removeDevicePref(key);
	return keys.length;
}

/**
 * Remove the pre-registry key families, once per device. Returns how many were removed.
 *
 * Called from core/core.ts at boot. There is no migration — decision 4 is a reset, and re-importing an
 * old collapse state is not worth a line of code — so this only takes out the litter.
 */
export function sweepLegacyDevicePrefs(): number {
	if (getBool(SWEPT_KEY, false)) return 0;

	const doomed: string[] = [];
	try {
		for (let i = 0; i < localStorage.length; i++) {
			const full = localStorage.key(i);
			if (full !== null && LEGACY_PREFIXES.some((p) => full.startsWith(p))) doomed.push(full);
		}
		for (const full of doomed) localStorage.removeItem(full);
	} catch {
		return 0;   // storage unavailable — do not record the sweep, so it retries on a device that has it
	}

	setBool(SWEPT_KEY, true);
	return doomed.length;
}
