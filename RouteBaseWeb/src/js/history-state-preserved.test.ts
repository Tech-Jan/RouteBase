// @vitest-environment node
import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * `history.replaceState(null, …)` is banned: pass `history.state`.
 *
 * Scroll position across Back lives in `history.state` (core/scroll-restore.ts) — that is what makes it
 * per-history-entry and per-tab, and it is why a fresh visit starts at the top without any
 * navigation-type check. The whole scheme rests on one fragile assumption: that nothing else on the
 * page throws the entry's state away.
 *
 * Every grid mirrors its query into the URL from `onQueryChange`, and the natural way to write that is
 * `replaceState(null, '', url)`. Four of them did. On the dashboard the damage was invisible and
 * total: GridView's constructor refreshes, the refresh ends in `onQueryChange`, so the wipe landed one
 * microtask after initDashboardGrid() returned — before any restore could read the anchor. The scroll
 * position was destroyed on every single load, and nothing failed.
 *
 * That is the class of bug this gate exists for: the next `replaceState(null, …)` written anywhere in
 * the app silently breaks Back on whatever page it lives on, with no test to catch it. The fix is
 * always the same one word, so the gate is absolute — there is no legitimate reason to blank the
 * state of an entry you are only re-writing the URL of.
 *
 * (`pushState(null, …)` is fine and deliberately not matched: a new entry starts with null state
 * anyway, so it destroys nothing.)
 */

const SRC = path.dirname(fileURLToPath(import.meta.url));

/** `replaceState(null` / `replaceState( null` — the wipe, however it is spaced. */
const WIPES_STATE = /replaceState\(\s*null\b/;

function sourceFiles(dir: string = SRC, out: string[] = []): string[] {
	for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
		const p = path.join(dir, e.name);
		if (e.isDirectory()) {
			if (e.name !== 'generated') sourceFiles(p, out);
		} else if (e.name.endsWith('.ts') && !e.name.endsWith('.test.ts') && !e.name.endsWith('.d.ts')) {
			out.push(path.relative(SRC, p).split(path.sep).join('/'));
		}
	}
	return out;
}

describe('history.state is never wiped', () => {
	it('the walk actually reads files', () => {
		expect(sourceFiles().length, 'walk found suspiciously few files — it is broken').toBeGreaterThan(150);
		expect(WIPES_STATE.test("history.replaceState(null, '', url)")).toBe(true);
		expect(WIPES_STATE.test("history.replaceState( null, '', url)")).toBe(true);
		// The two shapes that must NOT match.
		expect(WIPES_STATE.test("history.replaceState(history.state, '', url)")).toBe(false);
		expect(WIPES_STATE.test("history.pushState(null, '', url)")).toBe(false);
	});

	it('no source file passes null to replaceState', () => {
		const offenders = sourceFiles().filter((rel) => WIPES_STATE.test(fs.readFileSync(path.join(SRC, rel), 'utf8')));

		expect(
			offenders,
			`replaceState(null, …) blanks the history entry's state, which is where scroll position ` +
				`across Back is stored (core/scroll-restore.ts). If this runs on a page with scroll ` +
				`restore, Back silently stops working and no other test notices. Pass the state through ` +
				`instead — you are only changing the URL:\n\n` +
				`    history.replaceState(history.state, '', url)\n\n` +
				`Offenders:\n${offenders.join('\n')}`,
		).toEqual([]);
	});
});
