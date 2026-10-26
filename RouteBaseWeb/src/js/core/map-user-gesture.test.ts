import { describe, it, expect, beforeEach } from 'vitest';
import { dismissPopupOnMapGesture, onUserMapGesture } from './map-user-gesture';
import { createFakeMap, makePointerEvent, type FakeMap } from './fake-map-harness';

// The contract: a photo popup dies on the first REAL interaction with the map surface, and on nothing
// else. The "nothing else" half is what matters under a replay — the camera writes every frame, and a
// move-driven rule would dismiss the popup on the frame after it opened. A popup's own chrome lives
// outside the canvas container and must not count as a gesture either, or a click on it would race the
// dismissal it triggers.

/** A maplibregl.Popup with the two methods this module uses; remove() fires 'close' like the real one. */
class FakePopup {
	removeCount = 0;
	private closeCbs: (() => void)[] = [];
	on(type: string, cb: () => void): this { if (type === 'close') this.closeCbs.push(cb); return this; }
	remove(): void { this.removeCount++; this.closeCbs.splice(0).forEach((cb) => cb()); }
}

let fake: FakeMap;
let popup: FakePopup;

function attach(): void {
	dismissPopupOnMapGesture(fake.map, popup as unknown as maplibregl.Popup);
}

function pointerDownOnMap(): void {
	fake.map.getCanvasContainer().dispatchEvent(makePointerEvent('pointerdown'));
}

beforeEach(() => {
	fake = createFakeMap();
	popup = new FakePopup();
});

describe('dismissPopupOnMapGesture', () => {
	it('removes the popup on a pointerdown on the map surface', () => {
		attach();
		pointerDownOnMap();
		expect(popup.removeCount).toBe(1);
	});

	it('removes the popup on a wheel over the map — the one gesture with no pointerdown', () => {
		attach();
		fake.map.getCanvasContainer().dispatchEvent(new Event('wheel', { bubbles: true }));
		expect(popup.removeCount).toBe(1);
	});

	it('removes the popup on a touch that starts on the canvas itself (events bubble up)', () => {
		attach();
		fake.map.getCanvas().dispatchEvent(makePointerEvent('pointerdown'));
		expect(popup.removeCount).toBe(1);
	});

	it('ignores a pointerdown on the popup\'s own chrome', () => {
		attach();
		// MapLibre appends popups to the map CONTAINER, a sibling of the canvas container.
		const closeButton = document.createElement('button');
		fake.map.getContainer().appendChild(closeButton);
		closeButton.dispatchEvent(makePointerEvent('pointerdown'));
		expect(popup.removeCount).toBe(0);
	});

	it('ignores programmatic camera movement — a replay must not dismiss its own popup', () => {
		attach();
		fake.map.jumpTo({ center: [7, 47] });
		fake.emit('move');
		fake.emit('movestart');
		fake.emit('zoomstart');
		expect(popup.removeCount).toBe(0);
	});

	it('unsubscribes once the popup closes, so a later gesture does not re-remove it', () => {
		attach();
		popup.remove(); // closed by its × / the host's teardown
		pointerDownOnMap();
		expect(popup.removeCount).toBe(1);
	});
});

describe('onUserMapGesture', () => {
	it('stops firing after its unsubscribe', () => {
		let fired = 0;
		const stop = onUserMapGesture(fake.map, () => { fired++; });
		pointerDownOnMap();
		stop();
		pointerDownOnMap();
		expect(fired).toBe(1);
	});

	it('is inert on a map with no canvas container (disposed mid-teardown)', () => {
		const mapless = {} as unknown as maplibregl.Map;
		expect(() => onUserMapGesture(mapless, () => {})()).not.toThrow();
	});
});
