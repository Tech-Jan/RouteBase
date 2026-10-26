// @vitest-environment node
import { must } from './invariant';
import { describe, it, expect } from 'vitest';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// Parse-level guard for wwwroot/css/replay-controls.css. A browser is not available in this environment,
// so these read the stylesheet as text and pin the invariants the standalone-pill removal
// established. The assertions are deliberately few and specific — regex over CSS is crude, so
// each is commented with WHY it exists, and a legitimate future change reads as a deliberate edit here
// rather than a mystery failure.

/** A repo path, resolved whether vitest started from RouteBaseWeb or the solution root. NOT
 *  import.meta.url — vite rewrites it to a non-file URL under transform (share-race-view.test.ts idiom). */
function sourcePath(relativeToWeb: string): string {
	for (const base of ['.', 'RouteBaseWeb', '..']) {
		const candidate = resolve(process.cwd(), base, relativeToWeb);
		if (existsSync(candidate)) return candidate;
	}
	throw new Error(`source not found from ${process.cwd()}: ${relativeToWeb}`);
}

const css = readFileSync(sourcePath('wwwroot/css/replay-controls.css'), 'utf-8');

/** The FIRST `.replay-controls { … }` rule's DECLARATIONS (comments stripped, so a comment that merely
 *  mentions a retired property does not trip an assertion about the property being declared). */
function baseRule(): string {
	const m = css.match(/\.replay-controls\s*\{([^}]*)\}/);
	expect(m, 'the base .replay-controls rule must exist').not.toBeNull();
	return must(m![1], 'capture group 1').replace(/\/\*[\s\S]*?\*\//g, '');
}

describe('replay-controls.css — the dock is the only layout', () => {
	it('the transport bar is a full-width docked bar, not a floating pill', () => {
		// The standalone-pill layout (position:absolute; left:50%; width:min(680px,…); transform:translateX)
		// is retired — every transport renders inside the dock, which owns the width and does the centring.
		const rule = baseRule();
		expect(rule).toContain('width: 100%');
		expect(rule).not.toContain('position: absolute');
		expect(rule).not.toContain('translateX');
	});

	it('no rcSlideUp keyframe survives — the bar cannot jump off-centre when animations are disabled', () => {
		// rcSlideUp ended on translateX(-50%) with animation-fill-mode: both, and an animated value outranks
		// a declaration, so with animations off (reduced-motion, a user stylesheet) the bar sat half its
		// width left of centre. The dock's entrance (replayDockSlideUp) is a pure translateY.
		expect(css).not.toMatch(/@keyframes\s+rcSlideUp/);
		expect(css).toMatch(/@keyframes\s+replayDockSlideUp/);
		expect(baseRule()).toContain('animation: replayDockSlideUp');
	});

	it('the safe-area inset lives on the dock, and docked bars reset their own padding (NOT a double-count)', () => {
		// The dock is bottom-most whether the strip is open or closed, so the inset belongs to it. A docked
		// bar therefore drops the standalone bar's safe-area padding-bottom for a flat value. This was
		// mis-reported once as a double-inset bug; it is deliberate. Pin both halves so a "fix" trips here.

		// Exactly ONE selector in the docked chain (.replay-dock…) carries the inset — the dock itself.
		const dockChainWithInset = [...css.matchAll(/(\.replay-dock[^{]*)\{([^}]*)\}/g)]
			.filter((m) => must(m[2], 'body capture').includes('env(safe-area-inset-bottom'));
		expect(dockChainWithInset).toHaveLength(1);
		expect(must(must(dockChainWithInset[0], 'single inset carrier')[1], 'selector capture').trim()).toBe('.replay-dock');

		// ...and the docked bar itself resets padding-bottom to a flat value, carrying no inset of its own.
		const dockedBarReset = css.match(/\.replay-dock \.replay-controls\s*\{([^}]*)\}/);
		expect(dockedBarReset, 'the docked-bar padding reset must exist').not.toBeNull();
		const resetBody = must(dockedBarReset![1], 'rule body capture');
		expect(resetBody).toMatch(/padding-bottom:\s*10px/);
		expect(resetBody).not.toContain('safe-area-inset');
	});
});
