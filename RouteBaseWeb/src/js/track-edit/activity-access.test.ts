// @vitest-environment node
import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
	registerActivityDetailsAccess,
	initSpeedChart, initElevationChart, initHeartRateChart,
	getSpeedChart, getElevationChart,
	closeAllActionPanels, setOnChartClick,
	type ActivityDetailsAccess,
} from './activity-access';

// The seam holds module-level state; clear it before each case so order can't leak a provider.
beforeEach(() => registerActivityDetailsAccess(null));

function fakeAccess(): ActivityDetailsAccess {
	return {
		initSpeedChart: vi.fn(),
		initElevationChart: vi.fn(),
		initHeartRateChart: vi.fn(),
		getSpeedChart: vi.fn(() => null),
		getElevationChart: vi.fn(() => null),
		closeAllActionPanels: vi.fn(),
		setOnChartClick: vi.fn(),
	};
}

describe('activity-access seam', () => {
	it('no-ops and returns null until a provider registers', () => {
		expect(() => {
			initSpeedChart();
			initElevationChart();
			initHeartRateChart();
			closeAllActionPanels();
			setOnChartClick(() => {});
		}).not.toThrow();
		expect(getSpeedChart()).toBeNull();
		expect(getElevationChart()).toBeNull();
	});

	it('delegates every call-through to the registered provider', () => {
		const access = fakeAccess();
		registerActivityDetailsAccess(access);

		initSpeedChart();
		initElevationChart();
		initHeartRateChart();
		closeAllActionPanels();
		const handler = () => {};
		setOnChartClick(handler);

		expect(access.initSpeedChart).toHaveBeenCalledOnce();
		expect(access.initElevationChart).toHaveBeenCalledOnce();
		expect(access.initHeartRateChart).toHaveBeenCalledOnce();
		expect(access.closeAllActionPanels).toHaveBeenCalledOnce();
		expect(access.setOnChartClick).toHaveBeenCalledWith(handler);
	});

	it('returns whatever the provider yields for the chart getters', () => {
		const speed = { id: 'speed' } as unknown as ReturnType<ActivityDetailsAccess['getSpeedChart']>;
		const access = fakeAccess();
		access.getSpeedChart = vi.fn(() => speed);
		registerActivityDetailsAccess(access);

		expect(getSpeedChart()).toBe(speed);
		expect(getElevationChart()).toBeNull();
	});

	it('unregistering with null restores the no-op behaviour', () => {
		const access = fakeAccess();
		registerActivityDetailsAccess(access);
		registerActivityDetailsAccess(null);

		initSpeedChart();
		expect(access.initSpeedChart).not.toHaveBeenCalled();
		expect(getSpeedChart()).toBeNull();
	});
});
