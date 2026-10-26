/**
 * core/account-settings.ts — the page's account-level settings, as one explicitly-initialised seam.
 *
 * Replaces core/profile-settings.ts, whose contract was "usually not set". That was true: the old island
 * was emitted only inside `@if (!string.IsNullOrEmpty(MapTilerApiKey))`, so a page without a MapTiler
 * credential delivered NO settings and every reader fell back to its own default. Sixteen call sites
 * therefore carried `?.` + `??` chains, and two of those literals had drifted from the C# consts.
 *
 * Now the three layouts render #appSettings on every page, so the seam is always filled and
 * getAccountSettings() is NON-NULL and COMPLETE. The fallback lives in exactly one place — the normaliser
 * below — instead of at every leaf.
 *
 * ⚠ Read at CALL TIME, never captured at module scope: `const w = getAccountSettings().charts.borderWidth`
 * at the top of a module runs before initPageSettings() on any module that core.ts does not import.
 * Call it inside the function that needs the value.
 *
 * DEVICE preferences do not live here. Anything about one screen's pixels — panel widths, collapse state,
 * drawer height — is core/device-prefs.ts and never reaches the server. The rule: ACCOUNT if the same user
 * on a new device would be annoyed to LOSE it; DEVICE if they would be annoyed to INHERIT it.
 */

import { PROFILE_SETTINGS_DEFAULTS } from '../generated/settings-defaults';

/**
 * The wire contract, ALIASED from the generated types and never re-declared. A hand-written mirror is what
 * let `settings.chatrs` compile: the old shape carried `[key: string]: unknown`, so a typo was a valid
 * read of `unknown` rather than an error.
 */
export type AccountSettings = import('../generated/contracts').SettingsPayload;

export type MapSettings = AccountSettings['map'];
export type ChartsSettings = AccountSettings['charts'];
export type UiSettings = AccountSettings['ui'];

/** The generated defaults, in the same shape as the payload. Frozen once, shared by every fallback path. */
const DEFAULTS: AccountSettings = freeze({
	map: { ...PROFILE_SETTINGS_DEFAULTS.map },
	charts: { ...PROFILE_SETTINGS_DEFAULTS.charts },
	ui: { ...PROFILE_SETTINGS_DEFAULTS.ui },
});

let current: AccountSettings = DEFAULTS;

function freeze(settings: AccountSettings): AccountSettings {
	Object.freeze(settings.map);
	Object.freeze(settings.charts);
	Object.freeze(settings.ui);
	return Object.freeze(settings);
}

/** Narrow an unknown to a readable record without asserting a shape onto it. */
function section(raw: unknown, key: string): Record<string, unknown> {
	if (typeof raw !== 'object' || raw === null) return {};
	const value = (raw as Record<string, unknown>)[key];
	return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : {};
}

// ?? and never ||: a legitimately-saved 0 is the whole reason these are written out longhand.
// `hillshadeExaggeration: 0` means "no hillshade" and `fogDistance: 0` means "fog disabled" — with `||`
// both would silently become the default, which is how the 0.0-vs-0.2 drift did real damage before.
function num(raw: Record<string, unknown>, key: string, fallback: number): number {
	const value = raw[key];
	return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function bool(raw: Record<string, unknown>, key: string, fallback: boolean): boolean {
	const value = raw[key];
	return typeof value === 'boolean' ? value : fallback;
}

function str(raw: Record<string, unknown>, key: string, fallback: string): string {
	const value = raw[key];
	return typeof value === 'string' && value !== '' ? value : fallback;
}

/**
 * Turn anything at all into a complete AccountSettings. Pure — exported so the fallback behaviour can be
 * tested without a DOM.
 *
 * Every field is checked individually rather than trusting the payload wholesale, because the island is
 * attacker-adjacent in exactly one sense: it is JSON parsed from the document, and a stale cached page can
 * deliver an older shape. Per-field checking makes a partial payload degrade field by field instead of
 * poisoning a whole section.
 */
export function normalizeAccountSettings(raw: unknown): AccountSettings {
	const map = section(raw, 'map');
	const charts = section(raw, 'charts');
	const ui = section(raw, 'ui');

	return freeze({
		map: {
			hillshadeExaggeration: num(map, 'hillshadeExaggeration', DEFAULTS.map.hillshadeExaggeration),
			terrainExaggeration: num(map, 'terrainExaggeration', DEFAULTS.map.terrainExaggeration),
			fogDistance: num(map, 'fogDistance', DEFAULTS.map.fogDistance),
			defaultZoom: num(map, 'defaultZoom', DEFAULTS.map.defaultZoom),
			defaultStyle: str(map, 'defaultStyle', DEFAULTS.map.defaultStyle),
		},
		charts: {
			speedSmoothingSeconds: num(charts, 'speedSmoothingSeconds', DEFAULTS.charts.speedSmoothingSeconds),
			speedBinCount: num(charts, 'speedBinCount', DEFAULTS.charts.speedBinCount),
			defaultSpeedView: str(charts, 'defaultSpeedView', DEFAULTS.charts.defaultSpeedView),
			elevationSmoothingWindow: num(charts, 'elevationSmoothingWindow', DEFAULTS.charts.elevationSmoothingWindow),
			tension: num(charts, 'tension', DEFAULTS.charts.tension),
			borderWidth: num(charts, 'borderWidth', DEFAULTS.charts.borderWidth),
			chartDetail: str(charts, 'chartDetail', DEFAULTS.charts.chartDetail),
		},
		ui: {
			showDebugInfo: bool(ui, 'showDebugInfo', DEFAULTS.ui.showDebugInfo),
			defaultColorMode: str(ui, 'defaultColorMode', DEFAULTS.ui.defaultColorMode),
		},
	});
}

/** Normalise, freeze and store. Call once per page at boot; readers get a complete object either way. */
export function setAccountSettings(raw: unknown): void {
	current = normalizeAccountSettings(raw);
}

/** The page's account settings. Never null, always complete — readers need no fallback. */
export function getAccountSettings(): AccountSettings {
	return current;
}

/** Test/teardown helper — reset so one case's settings cannot leak into the next. */
export function resetAccountSettings(): void {
	current = DEFAULTS;
}

const ISLAND_ID = 'appSettings';

/**
 * Fill the seam from the #appSettings island. Called by core/core.ts, which every layout loads as a module
 * BEFORE the page's own entry — module scripts execute in document order, so this runs first even for
 * segment-statistics.ts's import-time ctx.
 *
 * Warns when the island is missing. This deliberately INVERTS readIsland's quiet-on-absent policy, which is
 * right for an OPTIONAL island and wrong for a mandatory one: silence is what let a MapTiler-key gate
 * suppress everyone's chart preferences for months with no symptom. Do not "fix" the inconsistency — the
 * two islands differ in whether absence is normal.
 */
export function initPageSettings(): void {
	const element = document.getElementById(ISLAND_ID);
	if (element === null) {
		console.warn(`[settings] #${ISLAND_ID} island missing — using defaults`);
		current = DEFAULTS;
		return;
	}

	try {
		setAccountSettings(JSON.parse(element.textContent ?? ''));
	} catch (err) {
		console.warn(`[settings] #${ISLAND_ID} holds malformed JSON — using defaults`, err);
		current = DEFAULTS;
	}
}
