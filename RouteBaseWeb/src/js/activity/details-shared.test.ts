// @vitest-environment node
import { describe, it, expect, vi } from 'vitest';
import { registerActionPanel, closeAllActionPanels, closeOtherActionPanels } from './details-shared';

// RP-06: closeAllActionPanels closes every registered panel (called when replay starts so an
// armed photo-placement session can't commit a pin at the flythrough location).

describe('details-shared action panels — RP-06', () => {
	it('closeAllActionPanels invokes every registered closer', () => {
		const photos = vi.fn();
		const segment = vi.fn();
		registerActionPanel('photos', photos);
		registerActionPanel('segment', segment);

		closeAllActionPanels();

		expect(photos).toHaveBeenCalledTimes(1);
		expect(segment).toHaveBeenCalledTimes(1);
	});

	it('closeOtherActionPanels leaves the excepted panel open', () => {
		const photos = vi.fn();
		const segment = vi.fn();
		registerActionPanel('photos', photos);
		registerActionPanel('segment', segment);

		closeOtherActionPanels('photos');

		expect(photos).not.toHaveBeenCalled();
		expect(segment).toHaveBeenCalledTimes(1);
	});
});
