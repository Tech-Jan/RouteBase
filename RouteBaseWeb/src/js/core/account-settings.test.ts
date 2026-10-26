import { describe, it, expect, afterEach, vi } from 'vitest';
import {
	getAccountSettings,
	initPageSettings,
	normalizeAccountSettings,
	resetAccountSettings,
	setAccountSettings,
} from './account-settings';
import { PROFILE_SETTINGS_DEFAULTS } from '../generated/settings-defaults';

/**
 * The seam that replaced ~16 leaf-level fallbacks with one normaliser. What is worth pinning is precisely
 * the behaviour those fallbacks used to be responsible for, plus the two things that made the old shape
 * dangerous: a saved 0 being eaten, and "absent" being a state readers disagreed about.
 */
afterEach(() => {
	resetAccountSettings();
	document.body.innerHTML = '';
	vi.restoreAllMocks();
});

describe('account settings — normalisation', () => {
	it('fills a completely empty payload with the generated defaults', () => {
		// The LiveView shape: the old island emitted {map:null,charts:null,ui:null} and every reader had to
		// decide what that meant. Now it means the defaults, once, here.
		const settings = normalizeAccountSettings({ map: null, charts: null, ui: null });

		expect(settings.map).toEqual(PROFILE_SETTINGS_DEFAULTS.map);
		expect(settings.charts).toEqual(PROFILE_SETTINGS_DEFAULTS.charts);
		expect(settings.ui).toEqual(PROFILE_SETTINGS_DEFAULTS.ui);
	});

	it.each([undefined, null, 'nonsense', 42, []])('survives a junk payload: %s', (raw) => {
		const settings = normalizeAccountSettings(raw);

		expect(settings.charts.speedSmoothingSeconds).toBe(PROFILE_SETTINGS_DEFAULTS.charts.speedSmoothingSeconds);
		expect(settings.map.defaultStyle).toBe(PROFILE_SETTINGS_DEFAULTS.map.defaultStyle);
	});

	// The reason every field is written out longhand with ?? instead of ||. hillshadeExaggeration: 0 means
	// "no hillshade" and fogDistance: 0 means "fog disabled" — with || both would silently become the
	// default, which is a visible map change from a legitimately saved value.
	it('keeps a saved 0 instead of folding it to the default', () => {
		const settings = normalizeAccountSettings({
			map: { hillshadeExaggeration: 0, fogDistance: 0 },
			charts: { speedSmoothingSeconds: 0, borderWidth: 0, tension: 0 },
		});

		expect(settings.map.hillshadeExaggeration).toBe(0);
		expect(settings.map.fogDistance).toBe(0);
		expect(settings.charts.speedSmoothingSeconds).toBe(0);
		expect(settings.charts.borderWidth).toBe(0);
		expect(settings.charts.tension).toBe(0);
	});

	it('keeps a saved false instead of folding it to the default', () => {
		const settings = normalizeAccountSettings({ ui: { showDebugInfo: false } });

		expect(settings.ui.showDebugInfo).toBe(false);
	});

	it('degrades field by field, not section by section', () => {
		// A stale cached page can deliver an older shape. One bad field must not poison its siblings.
		const settings = normalizeAccountSettings({ charts: { tension: 'not a number', borderWidth: 5 } });

		expect(settings.charts.borderWidth).toBe(5);
		expect(settings.charts.tension).toBe(PROFILE_SETTINGS_DEFAULTS.charts.tension);
	});

	it('rejects a non-finite number rather than passing NaN to a chart', () => {
		const settings = normalizeAccountSettings({ charts: { tension: Number.NaN, borderWidth: Number.POSITIVE_INFINITY } });

		expect(settings.charts.tension).toBe(PROFILE_SETTINGS_DEFAULTS.charts.tension);
		expect(settings.charts.borderWidth).toBe(PROFILE_SETTINGS_DEFAULTS.charts.borderWidth);
	});

	it('freezes the result and its sections', () => {
		// Readers share one object; a module that mutated it would change settings for every other reader.
		const settings = normalizeAccountSettings({});

		expect(Object.isFrozen(settings)).toBe(true);
		expect(Object.isFrozen(settings.map)).toBe(true);
		expect(Object.isFrozen(settings.charts)).toBe(true);
		expect(Object.isFrozen(settings.ui)).toBe(true);
	});
});

describe('account settings — the seam', () => {
	it('is complete before anything is set', () => {
		// The property every reader depends on: no caller has to null-check, so no caller invents a default.
		expect(getAccountSettings().charts.borderWidth).toBe(PROFILE_SETTINGS_DEFAULTS.charts.borderWidth);
	});

	it('stores what was set', () => {
		setAccountSettings({ charts: { borderWidth: 4 } });

		expect(getAccountSettings().charts.borderWidth).toBe(4);
	});
});

describe('account settings — island boot', () => {
	it('reads the #appSettings island', () => {
		document.body.innerHTML =
			'<script type="application/json" id="appSettings">{"charts":{"tension":0.75}}</script>';

		initPageSettings();

		expect(getAccountSettings().charts.tension).toBe(0.75);
	});

	// Deliberately LOUDER than readIsland's quiet-on-absent policy. That policy is right for an optional
	// island and wrong for a mandatory one: silence is exactly how a MapTiler-key gate suppressed everyone's
	// chart preferences without a single symptom.
	it('warns exactly once when the island is missing, and still yields defaults', () => {
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

		initPageSettings();

		expect(warn).toHaveBeenCalledTimes(1);
		expect(String(warn.mock.calls[0]?.[0])).toContain('appSettings');
		expect(getAccountSettings()).toEqual(normalizeAccountSettings({}));
	});

	it('warns and falls back when the island holds malformed JSON', () => {
		const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
		document.body.innerHTML =
			'<script type="application/json" id="appSettings">{ not json</script>';

		initPageSettings();

		expect(warn).toHaveBeenCalledTimes(1);
		expect(getAccountSettings().charts.tension).toBe(PROFILE_SETTINGS_DEFAULTS.charts.tension);
	});
});
