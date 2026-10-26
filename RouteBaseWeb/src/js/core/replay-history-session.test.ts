import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createReplayHistorySession } from './replay-history-session';

// P-NEW-11..14 (CR1-05): pins for the shared replay history session — idempotent
// enter() (the RP-01/RS2-05 duplicate-entry backstop), guarded leave(), the
// popstate clear-flag-then-exit dance, and the Escape exit gate.

interface FakeHistory {
	state: Record<string, boolean> | null;
	pushState: ReturnType<typeof vi.fn>;
	back: ReturnType<typeof vi.fn>;
}

let fakeHistory: FakeHistory;

beforeEach(() => {
	fakeHistory = {
		state: null,
		pushState: vi.fn((s: Record<string, boolean>) => { fakeHistory.state = s; }),
		back: vi.fn(() => { fakeHistory.state = null; }),
	};
	vi.stubGlobal('history', fakeHistory);
});

afterEach(() => {
	vi.unstubAllGlobals();
	vi.restoreAllMocks();
});

function makeSession(key: string, active: { value: boolean }) {
	const exitedWithFlag: (boolean | undefined)[] = [];
	const exit = vi.fn(() => {
		// Capture the state flag as seen at exit time (popstate must clear it first).
		exitedWithFlag.push(fakeHistory.state ? fakeHistory.state[key] : undefined);
	});
	const session = createReplayHistorySession({
		stateKey: key,
		isActive: () => active.value,
		exit,
	});
	return { session, exit, exitedWithFlag };
}

describe('replay-history-session — P-NEW-11..14', () => {
	it('enter() twice pushes exactly one history entry (idempotent)', () => {
		const active = { value: true };
		const { session } = makeSession('testKeyA', active);

		session.enter();
		session.enter();

		expect(fakeHistory.pushState).toHaveBeenCalledTimes(1);
		expect(fakeHistory.pushState).toHaveBeenCalledWith({ testKeyA: true }, '');
	});

	it('leave() calls history.back only while the state is still ours', () => {
		const active = { value: true };
		const { session } = makeSession('testKeyB', active);

		session.enter();
		session.leave(); // state is ours → pop the dummy entry
		expect(fakeHistory.back).toHaveBeenCalledTimes(1);

		session.enter();
		fakeHistory.state = { testKeyB: false }; // back button already moved us (flag cleared)
		session.leave();
		expect(fakeHistory.back).toHaveBeenCalledTimes(1); // no second back

		session.enter();
		fakeHistory.state = null; // no state at all
		session.leave();
		expect(fakeHistory.back).toHaveBeenCalledTimes(1);
	});

	it('popstate while active clears the state flag then exits; while inactive is a no-op', () => {
		const active = { value: true };
		const { session, exit, exitedWithFlag } = makeSession('testKeyC', active);

		session.enter();
		window.dispatchEvent(new PopStateEvent('popstate'));
		expect(exit).toHaveBeenCalledTimes(1);
		expect(exitedWithFlag).toEqual([false]); // flag cleared BEFORE exit ran (guarded back skips)

		active.value = false;
		window.dispatchEvent(new PopStateEvent('popstate'));
		expect(exit).toHaveBeenCalledTimes(1); // inactive → no-op
	});

	it('destroy() removes the popstate + keydown handlers, so a recreate cannot stack them', () => {
		const active = { value: true };
		const { session, exit } = makeSession('testKeyE', active);

		session.destroy();

		// Both globals are gone — neither a Back nor an Escape reaches the exit any more.
		window.dispatchEvent(new PopStateEvent('popstate'));
		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
		expect(exit).not.toHaveBeenCalled();
	});

	it('Escape exits only while active', () => {
		const active = { value: false };
		const { exit } = makeSession('testKeyD', active);

		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
		expect(exit).not.toHaveBeenCalled();

		active.value = true;
		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'a' }));
		expect(exit).not.toHaveBeenCalled(); // other keys ignored

		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
		expect(exit).toHaveBeenCalledTimes(1);
	});
});
