import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { installFakeRaf, type FakeRaf } from "./raf-test-harness";
import { initCollapsibleCards } from "./collapsible-card";
import { devicePrefKeys, setBool } from "./device-prefs";

// Item 5: a chart mounted inside a collapsed (display:none) card measures 0. Expanding must
// resize it AFTER layout settles — not on a magic setTimeout that raced the reveal. This pins
// the hidden→reveal→resize path through the shared afterLayout double-rAF.
describe("collapsible-card reveal resizes its hidden charts", () => {
	let raf: FakeRaf;
	let resize: ReturnType<typeof vi.fn>;

	beforeEach(() => {
		document.body.innerHTML = "";
		localStorage.clear();
		raf = installFakeRaf();
		resize = vi.fn();
		// Chart.getChart(canvas) → the (fake) chart mounted in the body.
		vi.stubGlobal("Chart", { getChart: () => ({ resize }) });
	});
	afterEach(() => {
		raf.restore();
		vi.unstubAllGlobals();
	});

	function mountCollapsedCard(): HTMLElement {
		document.body.innerHTML = `
			<div class="card" data-collapsible-card="k1">
				<div class="card-header" data-collapsible-toggle><h5>Title</h5></div>
				<div class="card-body" data-collapsible-body><canvas></canvas></div>
			</div>`;
		// Seeded through the registry, not a raw key: composition is device-prefs' contract, and a test
		// that hardcodes the stored name goes green-then-silently-wrong the day the prefix is bumped.
		setBool(devicePrefKeys.cardCollapsed("k1"), true); // start collapsed
		initCollapsibleCards();
		return document.querySelector("[data-collapsible-toggle]") as HTMLElement;
	}

	it("resizes the chart only after the second frame on expand", () => {
		const toggle = mountCollapsedCard();
		const body = document.querySelector("[data-collapsible-body]") as HTMLElement;
		expect(body.style.display).toBe("none"); // collapsed → the chart measured 0

		toggle.click();                            // expand
		expect(body.style.display).toBe("");       // revealed
		expect(resize).not.toHaveBeenCalled();     // not before layout

		raf.flushFrames(1);
		expect(resize).not.toHaveBeenCalled();     // one frame is not enough
		raf.flushFrames(1);
		expect(resize).toHaveBeenCalledTimes(1);   // resized once layout settled
	});

	it("does not resize on collapse (nothing became visible)", () => {
		document.body.innerHTML = `
			<div class="card" data-collapsible-card="k2">
				<div class="card-header" data-collapsible-toggle><h5>Title</h5></div>
				<div class="card-body" data-collapsible-body><canvas></canvas></div>
			</div>`;
		// starts expanded (no localStorage entry)
		initCollapsibleCards();
		const toggle = document.querySelector("[data-collapsible-toggle]") as HTMLElement;

		toggle.click();          // collapse
		raf.flushFrames(2);
		expect(resize).not.toHaveBeenCalled();
	});
});
