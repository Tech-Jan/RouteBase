import tseslint from "@typescript-eslint/eslint-plugin";
import tsparser from "@typescript-eslint/parser";
import eslintConfigPrettier from "eslint-config-prettier";
import noUnsanitized from "eslint-plugin-no-unsanitized";

// no-unsanitized (S2-1): interpolations into HTML sinks must go through the canonical
// escaper (core/html escapeHtml, or its dashboard-shared escHtml alias). Anything else
// needs a per-line disable naming the escaped provenance — see core/html.ts contract.
const noUnsanitizedEscapers = {
	escape: { methods: ["escapeHtml", "escHtml"] },
};

export default [
	// TypeScript source files
	{
		files: ["src/js/**/*.ts"],
		languageOptions: {
			parser: tsparser,
			parserOptions: {
				project: "./tsconfig.json",
				sourceType: "module",
			},
		},
		plugins: {
			"@typescript-eslint": tseslint,
			"no-unsanitized": noUnsanitized,
		},
		rules: {
			// --- XSS sink guard (S2-1): innerHTML/outerHTML assignments + insertAdjacentHTML/
			// document.write calls must interpolate only literals or escapeHtml/escHtml calls ---
			"no-unsanitized/property": ["error", noUnsanitizedEscapers],
			"no-unsanitized/method": ["error", noUnsanitizedEscapers],

			// --- TypeScript strict rules ---
			"@typescript-eslint/no-explicit-any": "error",
			// Floating promises are an error (AR-03 S9). maplibre-gl's `once(type, listener)` is
			// exempted: its declared return is the union `this | Promise<any>`, but WITH a listener it
			// returns the map — the "promise" is a types artifact, and 19 subscription sites would each
			// need a misleading `void`. The no-listener promise form is still awaited where used.
			// no-misused-promises (K5): a promise handed to a void slot loses its rejection — every
			// async callback goes through core/async-listener.ts's asyncCallback(), which names the
			// site and routes the rejection to a sink instead of a silent unhandled rejection.
			"@typescript-eslint/no-misused-promises": "error",
			"@typescript-eslint/no-floating-promises": [
				"error",
				{ allowForKnownSafeCalls: [{ from: "package", name: "once", package: "maplibre-gl" }] },
			],
			"@typescript-eslint/no-unused-vars": [
				"error",
				{
					argsIgnorePattern: "^_",
					varsIgnorePattern: "^_",
					caughtErrorsIgnorePattern: "^_",
				},
			],

			// --- Code quality ---
			"max-lines": ["warn", { max: 700, skipBlankLines: true, skipComments: true }],
			"no-console": ["warn", { allow: ["warn", "error"] }],
			"no-debugger": "error",
			eqeqeq: ["error", "always", { null: "ignore" }],
			"no-var": "error",
			"prefer-const": "error",

			// --- No window.* globals of our own (no ambient globals) ---
			// JS↔JS state must travel via ESM imports, not window. The allowlist below is the ONLY
			// permitted window.* surface, and as of the settings redesign it is real browser APIs and
			// CDN libs ONLY — the server→client KEEP set is EMPTY. There is no longer a
			// types/window-globals.d.ts, because there is nothing left to declare: the last entry,
			// activitiesPageConfig, became a read of the #activitiesGridBootstrap island plus the
			// #appSettings seam.
			//
			// Server→client data is a `<script type="application/json">` island read through
			// core/page-bootstrap.ts (readIsland), never a window property. Do not re-add a name here to
			// make one compile. Caveat: a `(window as any).x` cast sidesteps this selector — don't; it
			// defeats the whole point.
			"no-restricted-syntax": [
				"error",
				{
					// localStorage/sessionStorage are NOT here: core/device-prefs.ts is their only permitted
					// toucher, and it uses the bare global. This selector only sees `window.x` member
					// expressions, so it cannot enforce that on its own — core/settings-seams.test.ts
					// walks the source for the bare token. Removing them here closes the other half.
					selector:
						"MemberExpression[object.name='window'][property.name!=/^(location|history|navigator|document|performance|crypto|screen|visualViewport|alert|confirm|prompt|open|close|closed|print|focus|blur|top|parent|self|name|origin|getComputedStyle|getSelection|matchMedia|scroll|scrollTo|scrollBy|scrollX|scrollY|innerWidth|innerHeight|outerWidth|outerHeight|devicePixelRatio|addEventListener|removeEventListener|dispatchEvent|setTimeout|clearTimeout|setInterval|clearInterval|requestAnimationFrame|cancelAnimationFrame|requestIdleCallback|cancelIdleCallback|fetch|signalR|Chart|maplibregl)$/]",
					message:
						"No window.* globals of our own. Share JS↔JS state via an ESM import (e.g. maplibre-state.ts); deliver server→client data as a JSON island read with readIsland (core/page-bootstrap.ts). The allowlist in eslint.config.mjs is browser APIs and CDN libs only — the server→client set is empty and stays that way.",
				},
			],

			// --- Disabled: handled by TypeScript compiler ---
			"no-undef": "off",
			"no-redeclare": "off",
			"@typescript-eslint/no-redeclare": "off",
		},
	},

	// Vite config files (less strict)
	{
		files: ["vite.config.*.ts"],
		languageOptions: {
			parser: tsparser,
			parserOptions: {
				project: "./tsconfig.json",
				sourceType: "module",
			},
		},
		plugins: {
			"@typescript-eslint": tseslint,
		},
		rules: {
			"@typescript-eslint/no-explicit-any": "off",
			"no-console": "off",
		},
	},

	// --- MAP-ENGINE LAYERING (ARCH-F1) — the one-way import rule, mechanically enforced ---
	//
	// The map engine spans two directories and they must depend in ONE direction only, or the
	// core/ <-> maplibre/ cycle comes back. See MAP-CONTRACTS.md -> "Map-engine layering":
	//   core/map-state.ts   bottom layer, a leaf: imports nothing, BOTH directories depend DOWN on it
	//   core/               the lower map layer: NEVER reaches up into maplibre/ for a runtime value
	//   maplibre/           the engine: depends DOWN on core/, never the reverse
	//
	// This was prose in the project conventions until 2026-07-31 and was enforced only by people reading it. It is
	// a real bug class, not a style preference: the Android mirror of it (the data layer reaching UP
	// into the service layer through a process-global static) made RecordingRepository untestable for
	// months. A rule with a crisp definition belongs in a gate, not a document.
	//
	// TYPE-ONLY imports are allowed. The residual up-edge (`import type { CameraControlsInstance }`
	// in core/replay-playback-shell.ts and core/replay-follow-camera.ts) is erased at compile time, so
	// it creates no runtime cycle. Migrate that type down to the bottom layer when the replay slice is
	// next opened, then this exception can go too.
	{
		files: ["src/js/core/**/*.ts"],
		rules: {
			"@typescript-eslint/no-restricted-imports": [
				"error",
				{
					patterns: [
						{
							group: ["**/maplibre/*", "**/maplibre/**"],
							allowTypeImports: true,
							message:
								"core/ must not import runtime values from maplibre/ (MAP-CONTRACTS.md -> Map-engine layering). " +
								"core/ is the LOWER layer: maplibre/ depends down on it, never the reverse. If you need live map " +
								"state, it belongs in the bottom layer core/map-state.ts. `import type` is allowed.",
						},
						// The other half of the same rule (AR-01). The maplibre group above stops core/ reaching
						// sideways into the engine; this stops it reaching UP into a feature — the exact core/ ->
						// pages/ inversion that once had to be fixed, which nothing prevented from recurring.
						//
						// No allowTypeImports: core/ defines its own contracts (core-types.ts, generated/), so it
						// must never need a feature's type either.
						//
						// This list is maintained BY HAND because eslint config is static. Its self-maintaining
						// twin is core/core-layering.test.ts, which derives the same list from the filesystem —
						// so a feature dir missing here is still caught in gate 3. Add new feature dirs anyway:
						// the lint rule is what gives the fast in-editor signal.
						{
							group: [
								"**/activity/**",
								"**/dashboard/**",
								"**/import/**",
								"**/live-recording/**",
								"**/overview/**",
								"**/pages/**",
								"**/photos/**",
								"**/race-sim/**",
								"**/segment-create/**",
								"**/segment-stats/**",
								"**/statistics/**",
								"**/track-edit/**",
								"**/trash/**",
							],
							message:
								"core/ is the bottom layer: it must not import from a feature or pages directory (upward edge). " +
								"If a feature needs to share code with core/, move that code DOWN into core/ or generated/. " +
								"See the map-engine layering note above and AR-01.",
						},
					],
				},
			],
		},
	},

	// Test files: vitest/jsdom fixtures assign static HTML scaffolding to build the DOM
	// under test — not a shipped XSS surface (S2 sink census scope = product code only).
	//
	// The map-engine layering rule is off here for the same reason, and only that reason: it exists to
	// keep a runtime import CYCLE out of the shipped bundles, and no *.test.ts is in a bundle. A test
	// that drives the real maplibre module across the layer boundary (core/replay-follow-camera.test.ts
	// imports maplibre/camera-controls) is testing the seam, not violating it. Product code in core/ is
	// still hard-gated above.
	{
		files: ["src/js/**/*.test.ts"],
		rules: {
			"no-unsanitized/property": "off",
			"no-unsanitized/method": "off",
			"@typescript-eslint/no-restricted-imports": "off",
		},
	},

	// Type declaration files — skip all rules
	{
		files: ["src/js/types/**/*.d.ts"],
		rules: {
			"@typescript-eslint/no-explicit-any": "off",
			"@typescript-eslint/no-unused-vars": "off",
			"no-var": "off",
			"max-lines": "off",
		},
	},

	// Prettier must be last to override formatting rules
	eslintConfigPrettier,
];
