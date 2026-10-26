// @vitest-environment node
import { describe, expect, it, beforeEach, vi } from 'vitest';

// Singleton leaf module — reset its module state between tests so publisher/subs/_lastIds don't leak.
beforeEach(() => vi.resetModules());

async function load() {
	return import('./overview-pick-bridge');
}

describe('overview-pick-bridge', () => {
	it('publishPickedIds caches for getPickedIds and notifies subscribers', async () => {
		const { publishPickedIds, getOverviewPickBridge } = await load();
		const bridge = getOverviewPickBridge();
		const seen: number[][] = [];
		bridge.onPickedChanged((ids) => seen.push(ids));

		expect(seen).toEqual([[]]); // immediate fire with the current (empty) value
		publishPickedIds([3, 1]);
		expect(bridge.getPickedIds()).toEqual([3, 1]);
		expect(seen).toEqual([[], [3, 1]]);
	});

	it('clearPicks routes to the registered publisher', async () => {
		const { registerPickPublisher, getOverviewPickBridge } = await load();
		const clearPicks = vi.fn();
		registerPickPublisher({ clearPicks });
		getOverviewPickBridge().clearPicks();
		expect(clearPicks).toHaveBeenCalledTimes(1);
	});

	it('is null-safe before the grid registers: getPickedIds is [], clearPicks a no-op', async () => {
		const { getOverviewPickBridge } = await load();
		const bridge = getOverviewPickBridge();
		expect(bridge.getPickedIds()).toEqual([]);
		expect(() => bridge.clearPicks()).not.toThrow();
	});

	it('a subscriber that throws during a publish does not break the others', async () => {
		const { publishPickedIds, getOverviewPickBridge } = await load();
		vi.spyOn(console, 'warn').mockImplementation(() => {});
		const bridge = getOverviewPickBridge();
		let calls = 0;
		bridge.onPickedChanged(() => { calls++; if (calls > 1) throw new Error('boom'); }); // ok on immediate fire, throws on publish
		const good: number[][] = [];
		bridge.onPickedChanged((ids) => good.push(ids));
		publishPickedIds([7]);
		expect(good).toEqual([[], [7]]); // the broadcast guard kept the good subscriber alive
	});
});
