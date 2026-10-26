// AUTO-GENERATED from RouteBaseWeb/Constants/ProfileSettingsDefaults.cs — do not make direct changes.
// Regenerate: change the const, run `dotnet test` and paste what SettingsDefaultsTsGateTests prints.
//
// These are the SAME numbers the server applies, so a page that was delivered no settings island
// renders exactly as a keyed page would with untouched preferences. Before this existed, each call
// site carried its own literal and two of them had drifted from the C# value.

export const PROFILE_SETTINGS_DEFAULTS = {
	charts: {
		speedSmoothingSeconds: 30,
		speedBinCount: 250,
		defaultSpeedView: 'smoothBin',
		elevationSmoothingWindow: 7,
		tension: 0.2,
		borderWidth: 2,
		chartDetail: 'auto',
	},
	map: {
		hillshadeExaggeration: 0.2,
		terrainExaggeration: 1.0,
		fogDistance: 10,
		defaultZoom: 13,
		defaultStyle: 'satellite-esri',
	},
	ui: {
		defaultColorMode: 'none',
		showDebugInfo: false,
	},
} as const;
