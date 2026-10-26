import { describe, it, expect, vi, beforeEach } from 'vitest';
import { must } from '../core/invariant';
import type { RaceReplayPayload } from './race-api';

/**
 * race-replay-view teardown (H2), plus the opening-basemap pin the share fork used to own.
 *
 * `createRaceReplayView().destroy()` is the one replay-transport teardown with real work behind it, and it
 * had NO test: its only importer, share-race-view.test.ts, mocks `destroy` outright, so a regression here
 * was invisible. What it releases is not incidental — the rAF loop reschedules itself while `isPlaying`
 * stays true, so a teardown that fails to stop it leaves the loop driving a dead map AND holding the whole
 * BuiltRace (~7 MB for a 508-team field) alive through its closure. That was a real bug once.
 *
 * The load-bearing part is the ORDER, which is why these tests assert it rather than just the calls:
 * playback.destroy() must run FIRST ("STOP THE CLOCK FIRST" in the source). Nothing else can release the
 * closure while the loop still references it, and tearing the map down under a live loop is what used to
 * throw out of the rAF callback.
 *
 * Only the four collaborators destroy() actually reaches are mocked. Everything built inside start() stays
 * null because the mocked shell never runs its init callbacks — so those `?.destroy()` calls are no-ops
 * here by construction, not by accident.
 */

const shellDestroy = vi.fn();
const playbackDestroy = vi.fn();
const transportDestroy = vi.fn();
const toolbarTeardown = vi.fn();
const switchToBasemap = vi.fn();
const setActiveBasemap = vi.fn();

vi.mock('../core/map-shell', () => ({
	createMapShell: () => ({
		switchToBasemap,
		setActiveBasemap,
		destroy: shellDestroy,
	}),
}));

vi.mock('../core/replay-playback-shell', () => ({
	createReplayPlaybackShell: () => ({
		destroy: playbackDestroy,
		togglePlayPause: vi.fn(() => false),
		setSpeed: vi.fn(),
		isPlaying: vi.fn(() => false),
		stopIdleCamera: vi.fn(),
		syncIdleCamera: vi.fn(),
		applyCamera: vi.fn(),
	}),
}));

vi.mock('../core/replay-transport', () => ({
	createReplayTransport: () => ({
		destroy: transportDestroy,
		setPlayIcon: vi.fn(),
		buildLadder: vi.fn(),
		render: vi.fn(),
	}),
}));

vi.mock('../core/map-toolbar', () => ({ setupMapToolbar: () => toolbarTeardown }));

/** The payload never loads: the mocked shell never fires its init callbacks, so start() is never reached. */
const loadPayload = (): Promise<RaceReplayPayload> => new Promise(() => {});

async function buildView() {
	const { createRaceReplayView } = await import('./race-replay-view');
	return createRaceReplayView({ raceId: 1, editable: false, loadPayload });
}

beforeEach(async () => {
	vi.clearAllMocks();
	document.body.innerHTML = '';
	// The account-settings seam is a module singleton; one case's saved basemap must not reach the next.
	const { resetAccountSettings } = await import('../core/account-settings');
	resetAccountSettings();
});

describe('createRaceReplayView().destroy()', () => {
	it('stops the playback loop and the transport — the ~7 MB BuiltRace closure holder', async () => {
		const view = await buildView();

		view.destroy();

		expect(playbackDestroy).toHaveBeenCalledTimes(1);
		expect(transportDestroy).toHaveBeenCalledTimes(1);
	});

	it('stops the clock BEFORE the transport, so no queued seek frame can outlive the loop', async () => {
		const view = await buildView();

		view.destroy();

		// The transport owns the coalesced-seek frame; the playback shell owns the self-rescheduling loop.
		// Reversing these lets one more frame render into a view that is already tearing down.
		expect(playbackDestroy.mock.invocationCallOrder[0])
			.toBeLessThan(must(transportDestroy.mock.invocationCallOrder[0], 'destroy invoked'));
	});

	it('stops the clock BEFORE the map shell — a live loop must never drive a destroyed map', async () => {
		const view = await buildView();

		view.destroy();

		expect(playbackDestroy.mock.invocationCallOrder[0])
			.toBeLessThan(must(shellDestroy.mock.invocationCallOrder[0], 'destroy invoked'));
	});

	it('releases the toolbar observers and the map shell', async () => {
		const view = await buildView();

		view.destroy();

		expect(toolbarTeardown).toHaveBeenCalledTimes(1);
		expect(shellDestroy).toHaveBeenCalledTimes(1);
	});
});

/**
 * The opening basemap. This pin used to live in share-race-view.test.ts, on a `defaultStyle` option that
 * the owner page fed from a data-attribute and the fork from its seed island. Both copies are gone: the
 * view reads core/account-settings, so BOTH pages resolve from the one #appSettings payload — and the
 * resolution is what the test has to watch, because the fallback is two-step and silent.
 *
 * basemap-registry is deliberately NOT mocked here: the whole point is that the real registry decides.
 */
describe('createRaceReplayView() — the opening basemap', () => {
	it('falls back to the keyless entry when the saved default needs a MapTiler key', async () => {
		// 'streets' explicitly, NOT whatever the shipped default happens to be: the default is keyless now,
		// so relying on it would make this case assert the fallback without ever taking it.
		const { setAccountSettings } = await import('../core/account-settings');
		setAccountSettings({ map: { defaultStyle: 'streets' } });

		await buildView();

		// No key is configured in this environment, so resolveStyleUrl('streets') is null and the registry
		// degrades. A share page can never do better than this: it is served no key at all.
		expect(switchToBasemap).toHaveBeenCalledWith('liberty');
		expect(setActiveBasemap).toHaveBeenCalledWith('liberty');
	});

	it('honours a saved default that resolves without a key', async () => {
		const { setAccountSettings } = await import('../core/account-settings');
		setAccountSettings({ map: { defaultStyle: 'positron' } });

		await buildView();

		expect(switchToBasemap).toHaveBeenCalledWith('positron');
	});
});
