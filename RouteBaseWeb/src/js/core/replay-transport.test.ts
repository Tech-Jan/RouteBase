import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createReplayTransport, type ReplayTransportHooks } from './replay-transport';
import { installFakeRaf, type FakeRaf } from './raf-test-harness';
import { buildSpeedLadder } from './speed-ladder';

// Pins for the shared replay transport — the divergences it exists to close:
//   a. the scrubber write-back GUARD (never fight the user's thumb),
//   b. rAF-coalesced seeks (one apply per frame, at the latest value),
//   d. ONE elapsed formatter (core/format.formatElapsed) with the segment's %-mode override,
//   + the play/pause icon, the speed <select>, the ladder, and destroy().

const IDS = {
	playPauseId: 'tPlay',
	seekId: 'tSeek',
	currentTimeId: 'tTime',
	speedSelectId: 'tSpeed',
	speedSlowerId: 'tSlower',
	speedFasterId: 'tFaster',
	speedLabelId: 'tLabel',
	speedPrefKey: 'testreplay',
};

let raf: FakeRaf;

function seedDom(): void {
	document.body.innerHTML = `
		<button id="tPlay"><i class="bi bi-play-fill"></i></button>
		<input id="tSeek" type="range" min="0" max="100" value="0" />
		<span id="tTime">0:00</span>
		<button id="tSlower"></button>
		<span id="tLabel">1x</span>
		<button id="tFaster"></button>
		<select id="tSpeed"></select>`;
}

function makeHooks(overrides: Partial<ReplayTransportHooks> = {}): ReplayTransportHooks {
	return {
		seekToPercent: vi.fn(),
		togglePlayPause: vi.fn(() => true),
		setSpeed: vi.fn(),
		...overrides,
	};
}

const seek = (): HTMLInputElement => document.getElementById('tSeek') as HTMLInputElement;
const timeEl = (): HTMLElement => document.getElementById('tTime')!;
const playEl = (): HTMLElement => document.getElementById('tPlay')!;

function fireInput(pct: number): void {
	seek().value = String(pct);
	seek().dispatchEvent(new Event('input', { bubbles: true }));
}

beforeEach(() => {
	seedDom();
	raf = installFakeRaf();
	// The remembered speed is a device pref now, so one case's chosen step must not seed the next.
	localStorage.clear();
});

afterEach(() => {
	raf.restore();
	document.body.innerHTML = '';
});

describe('replay-transport', () => {
	it('(a) render writes the scrubber while idle, but NOT while the user is dragging it', () => {
		const transport = createReplayTransport(IDS, makeHooks());

		// Idle: the playhead moves the thumb.
		transport.render(40, 0);
		expect(seek().value).toBe('40');

		// Dragging (explicit pointerdown, not activeElement — iOS Safari would not focus it): the thumb
		// stays put under the finger while the playhead advances underneath.
		seek().dispatchEvent(new Event('pointerdown'));
		transport.render(80, 0);
		expect(seek().value).toBe('40'); // NOT written — the fix

		// Release anywhere ends the drag; write-back resumes.
		window.dispatchEvent(new Event('pointerup'));
		transport.render(80, 0);
		expect(seek().value).toBe('80');
	});

	it('(b) a burst of input events coalesces to ONE seek per frame, at the latest value', () => {
		const hooks = makeHooks();
		createReplayTransport(IDS, hooks);

		for (const pct of [20, 30, 40, 50, 60]) fireInput(pct);
		expect(hooks.seekToPercent).not.toHaveBeenCalled(); // coalesced — nothing yet

		raf.flushFrames();
		expect(hooks.seekToPercent).toHaveBeenCalledTimes(1);
		expect(hooks.seekToPercent).toHaveBeenLastCalledWith(60); // the last value, not the first

		// A frame with nothing pending does not seek again.
		raf.flushFrames();
		expect(hooks.seekToPercent).toHaveBeenCalledTimes(1);
	});

	it('(d) the elapsed clock uses formatElapsed; a labelOverride replaces it verbatim', () => {
		const transport = createReplayTransport(IDS, makeHooks());

		transport.render(50, 3661); // 1:01:01 via formatElapsed
		expect(timeEl().textContent).toBe('1:01:01');

		transport.render(50, 3661, '42%'); // segment % mode
		expect(timeEl().textContent).toBe('42%');
	});

	it('the play button toggles through the hook and flips the icon to match', () => {
		const hooks = makeHooks({ togglePlayPause: vi.fn(() => false) }); // returns paused
		createReplayTransport(IDS, hooks);

		playEl().dispatchEvent(new MouseEvent('click'));

		expect(hooks.togglePlayPause).toHaveBeenCalledTimes(1);
		expect(playEl().innerHTML).toContain('bi-play-fill'); // paused → play icon
		expect(playEl().getAttribute('aria-label')).toBe('Play'); // accessible name tracks the state
	});

	it('setPlayIcon keeps the accessible name in step with the play/pause state', () => {
		const transport = createReplayTransport(IDS, makeHooks());
		transport.setPlayIcon(true);
		expect(playEl().getAttribute('aria-label')).toBe('Pause'); // playing → the click pauses
		transport.setPlayIcon(false);
		expect(playEl().getAttribute('aria-label')).toBe('Play');
	});

	it('buildLadder fills the select, sets the default speed on the engine, and returns it', () => {
		// Nothing stored → the ladder default, which is the contract this always pinned.
		const hooks = makeHooks();
		const transport = createReplayTransport(IDS, hooks);

		const def = transport.buildLadder(8939);

		expect(def).toBe(buildSpeedLadder(8939).defaultSpeed);
		expect((document.getElementById('tSpeed') as HTMLSelectElement).options.length).toBeGreaterThan(0);
		expect(hooks.setSpeed).toHaveBeenCalledWith(def);
	});

	it('the speed <select> change drives setSpeed', () => {
		const hooks = makeHooks();
		const transport = createReplayTransport(IDS, hooks);
		transport.buildLadder(8939);
		(hooks.setSpeed as ReturnType<typeof vi.fn>).mockClear();

		const select = document.getElementById('tSpeed') as HTMLSelectElement;
		select.selectedIndex = select.options.length - 1;
		select.dispatchEvent(new Event('change'));

		expect(hooks.setSpeed).toHaveBeenCalledWith(Number(select.value));
	});

	/**
	 * The remembered speed is an OFFSET from the ladder default, not the multiplier itself — because the
	 * ladder is scaled to the replay's length. These two cases are the reason: the same stored preference
	 * has to mean something on a 2.5-hour race AND on a 2-minute effort, where the ladders share almost
	 * no values. An absolute multiplier would land on whatever step happened to be nearest.
	 */
	it('restores the chosen step as an offset, on a ladder it has never seen', () => {
		const hooks = makeHooks();
		const transport = createReplayTransport(IDS, hooks);

		// Watch a long replay one notch faster than its default.
		transport.buildLadder(8939);
		const longSelect = document.getElementById('tSpeed') as HTMLSelectElement;
		const longLadder = buildSpeedLadder(8939);
		const longDefaultIdx = longLadder.speeds.indexOf(longLadder.defaultSpeed);
		longSelect.selectedIndex = longDefaultIdx + 1;
		longSelect.dispatchEvent(new Event('change'));

		// Now a SHORT replay, whose ladder shares almost nothing with the long one.
		const shortLadder = buildSpeedLadder(90);
		const restored = transport.buildLadder(90);

		const shortDefaultIdx = shortLadder.speeds.indexOf(shortLadder.defaultSpeed);
		expect(restored).toBe(shortLadder.speeds[shortDefaultIdx + 1]);
		expect(restored).not.toBe(longSelect.value);   // not the absolute multiplier
		expect(hooks.setSpeed).toHaveBeenLastCalledWith(restored);
	});

	it('clamps a stored offset that runs off the end of a shorter ladder', () => {
		const hooks = makeHooks();
		const transport = createReplayTransport(IDS, hooks);
		transport.buildLadder(8939);

		// Pin to the fastest step the long ladder offers.
		const select = document.getElementById('tSpeed') as HTMLSelectElement;
		select.selectedIndex = select.options.length - 1;
		select.dispatchEvent(new Event('change'));

		const shortLadder = buildSpeedLadder(90);
		const restored = transport.buildLadder(90);

		// Still a real step — never undefined, never off the end.
		expect(shortLadder.speeds).toContain(restored);
	});

	it('the −/+ stepper persists the chosen step too, without knowing about preferences', () => {
		const hooks = makeHooks();
		const transport = createReplayTransport(IDS, hooks);
		transport.buildLadder(8939);

		// The stepper dispatches a synthetic 'change', which is the one path that persists.
		document.getElementById('tFaster')!.dispatchEvent(new MouseEvent('click'));
		const chosen = (document.getElementById('tSpeed') as HTMLSelectElement).value;

		expect(transport.buildLadder(8939)).toBe(Number(chosen));
	});

	it('destroy() drops the listeners and cancels a pending coalesced seek', () => {
		const hooks = makeHooks();
		const transport = createReplayTransport(IDS, hooks);

		fireInput(55);            // queue a coalesced seek...
		transport.destroy();      // ...then tear down before the frame runs
		raf.flushFrames();
		expect(hooks.seekToPercent).not.toHaveBeenCalled(); // the pending frame was cancelled

		// A click or input after destroy is inert.
		playEl().dispatchEvent(new MouseEvent('click'));
		fireInput(70);
		raf.flushFrames();
		expect(hooks.togglePlayPause).not.toHaveBeenCalled();
		expect(hooks.seekToPercent).not.toHaveBeenCalled();
	});

	it('destroy() also unwires the speed −/+ stepper buttons — no leaked click listener', () => {
		const hooks = makeHooks();
		const transport = createReplayTransport(IDS, hooks);
		transport.buildLadder(8939); // populate the <select> so a step has somewhere to move

		const select = document.getElementById('tSpeed') as HTMLSelectElement;
		select.selectedIndex = 0; // pin to the slow end so a live faster-click would move it to 1

		transport.destroy();

		// The stepper's own click listener must be gone: faster no longer moves the select. (setSpeed
		// alone would not catch the leak — destroy() already removed the transport's change listener —
		// so the selectedIndex is the discriminating assertion for the stepper teardown.)
		document.getElementById('tFaster')!.dispatchEvent(new MouseEvent('click'));
		expect(select.selectedIndex).toBe(0);
	});
});
