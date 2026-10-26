// @vitest-environment node
import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Gates (f) and (g) — the two settings seams, each of which is only worth anything if it is the ONLY door.
 *
 * (f) Only core/account-settings.ts may read PROFILE_SETTINGS_DEFAULTS.
 *     A default is what you apply when you were handed nothing. Once the island is delivered by the layout
 *     and the payload is complete, exactly one module is ever handed nothing — the normaliser. Every other
 *     reader receiving a default is a reader that still believes the value might be missing, and that
 *     belief is what produced ~16 hand-copied literal fallbacks, two of which had drifted from the C#
 *     const. The fallback moves from ~16 places to one; this is what stops it moving back.
 *
 * (g) Only core/device-prefs.ts may touch localStorage/sessionStorage.
 *     Device preferences today are 7 ad-hoc key families across 7 modules, two of them with no try/catch —
 *     and localStorage throws on quota and in some privacy modes, so an unguarded write takes the feature
 *     down with it. One module owns the key registry (`rb1.` prefix, where the VERSION IS THE PREFIX, so a
 *     break bumps to `rb2.` and there is never migration code) and owns the try/catch.
 *
 *     Note eslint cannot do this job: its allowlist at eslint.config.mjs governs `window.localStorage`
 *     member expressions, and every call site in this repo writes the bare global — which that selector
 *     never sees. This walk does.
 *
 * Both walks skip *.test.ts, the same exemption eslint grants and core-layering.test.ts takes: a test may
 * legitimately import the defaults to assert the seam, or seed localStorage to arrange a case.
 *
 * No comment stripping, deliberately — a mention inside a comment is a false POSITIVE, which can only make
 * these gates stricter, never let a real call site through.
 */

// import.meta.url → .../src/js/core/settings-seams.test.ts; two `..` pop the filename and core/.
const jsRoot = path.resolve(fileURLToPath(import.meta.url), '..', '..');

/** The single permitted reader of the generated defaults. */
const DEFAULTS_OWNER = 'core/account-settings.ts';
/** The single permitted toucher of web storage. */
const STORAGE_OWNER = 'core/device-prefs.ts';
/** The generated module that DECLARES the defaults — the export site, not a consumer. */
const DEFAULTS_SOURCE = 'generated/settings-defaults.ts';

const DEFAULTS_TOKEN = /\bPROFILE_SETTINGS_DEFAULTS\b/;
const STORAGE_TOKEN = /\b(localStorage|sessionStorage)\b/;

/** Non-test, non-declaration .ts under src/js, as paths relative to src/js with '/' separators. */
function collectSourceFiles(dir: string = jsRoot): string[] {
	const out: string[] = [];
	for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
		const abs = path.join(dir, ent.name);
		if (ent.isDirectory()) {
			out.push(...collectSourceFiles(abs));
		} else if (ent.name.endsWith('.ts') && !ent.name.endsWith('.test.ts') && !ent.name.endsWith('.d.ts')) {
			out.push(path.relative(jsRoot, abs).split(path.sep).join('/'));
		}
	}
	return out;
}

let cachedFiles: string[] | null = null;
function sourceFiles(): string[] {
	return (cachedFiles ??= collectSourceFiles().sort());
}

function filesMatching(token: RegExp): string[] {
	return sourceFiles().filter((rel) => token.test(fs.readFileSync(path.join(jsRoot, rel), 'utf8')));
}

describe('settings seams', () => {
	it('the source walk actually reads files', () => {
		const files = sourceFiles();
		// ~250 non-test modules under src/js today. A broken walk (wrong root, empty read) would make every
		// assertion below pass hollowly.
		expect(files.length, 'source walk found suspiciously few files — the walk is broken').toBeGreaterThan(150);

		// Proves the read+match path works, against the one file that must always contain the token.
		expect(filesMatching(DEFAULTS_TOKEN), 'the defaults token no longer matches its own export site').toContain(
			DEFAULTS_SOURCE,
		);
		expect(STORAGE_TOKEN.test('window.localStorage.getItem("x")')).toBe(true);
		expect(STORAGE_TOKEN.test('sessionStorage.setItem("x", "1")')).toBe(true);
	});

	it('only core/account-settings.ts reads PROFILE_SETTINGS_DEFAULTS', () => {
		const offenders = filesMatching(DEFAULTS_TOKEN).filter(
			(rel) => rel !== DEFAULTS_OWNER && rel !== DEFAULTS_SOURCE,
		);

		expect(
			offenders,
			`A default is what you apply when you were handed nothing, and after the layout delivers a ` +
				`complete payload exactly one module is ever handed nothing. A reader that still applies its own ` +
				`default is a reader that still believes the value can be missing — which is how ~16 literal ` +
				`fallbacks accreted and two of them drifted from the C# const. Take the value from ` +
				`${DEFAULTS_OWNER}'s getAccountSettings(), which is non-null and always complete:\n${offenders.join('\n')}`,
		).toEqual([]);
	});

	it('only core/device-prefs.ts touches localStorage/sessionStorage', () => {
		const offenders = filesMatching(STORAGE_TOKEN).filter((rel) => rel !== STORAGE_OWNER);

		expect(
			offenders,
			`Web storage has exactly one owner: ${STORAGE_OWNER} holds the typed key registry (the \`rb1.\` ` +
				`prefix IS the version, so a break bumps the prefix and there is never migration code) and holds ` +
				`the try/catch — localStorage throws on quota and in some privacy modes, and an unguarded write ` +
				`takes the feature down with it. Ad-hoc key families are also unfindable: nothing can enumerate ` +
				`what this origin stores, so "reset this device's layout" cannot be written:\n${offenders.join('\n')}`,
		).toEqual([]);
	});
});
