import { defineConfig } from 'vitest/config';

// Test runner config — separate from the Vite build (vite.config.esm.ts).
// Tests are colocated with sources as `*.test.ts` and never bundled: the build
// uses an explicit entry map, so these files are invisible to `npm run build`.
// jsdom is the DEFAULT, not the mandate. Building one costs ~1.4 s per test file and dominated the
// suite (349 s of 416 s CPU) when every file paid it. A file that never touches the DOM opts out with
//     // @vitest-environment node
// as its first line — 0 ms environment, same assertions. 111 of 248 files do; the suite went 50 s → 33 s.
// Default-jsdom is deliberate: a new test file is correct without thinking about it, and opting out is
// a one-line change that fails loudly ("document is not defined") if the file did need a DOM.
export default defineConfig({
	test: {
		environment: 'jsdom',
		include: ['src/js/**/*.test.ts'],
		// Shims for browser APIs jsdom lacks (e.g. HTMLDialogElement.showModal) — environment gaps
		// only; never behaviour the product code should be providing itself.
		setupFiles: ['./vitest.setup.ts'],
	},
});
