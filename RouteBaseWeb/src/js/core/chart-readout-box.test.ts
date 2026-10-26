import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { ChartReadoutBox } from "./chart-readout-box";

// chart-readout-box is the live HTML-escaping sink for three pages' chart hover boxes, plus a
// timer-based sticky show/hide state machine and positioning clamp. CF-07 lands its first tests;
// CF-06 pins escaping against the shared core/html escapeHtml (no local hand-rolled esc).

function makeHost(clientWidth = 400): HTMLElement {
	const host = document.createElement("div");
	document.body.appendChild(host);
	Object.defineProperty(host, "clientWidth", { value: clientWidth, configurable: true });
	return host;
}

beforeEach(() => {
	document.body.innerHTML = "";
});

afterEach(() => {
	vi.useRealTimers();
});

describe("ChartReadoutBox — escaping (CF-06/CF-07)", () => {
	it("escapes HTML in title, row label, and the data-row-key attribute", () => {
		const host = makeHost();
		const box = new ChartReadoutBox(host);
		box.show(
			"<b>t</b>",
			[{ color: "red", label: "<img onerror=x>", cells: [{ num: "1", unit: "km" }], rowKey: '"><script>' }],
			10,
			10,
		);
		const el = host.querySelector(".chart-readout") as HTMLElement;
		// No live nodes were injected — the markup was escaped, not parsed.
		expect(el.querySelector("script")).toBeNull();
		expect(el.querySelector("img")).toBeNull();
		expect(el.querySelector(".chart-readout-title")!.textContent).toBe("<b>t</b>");
		// The attribute value round-trips as a literal (no breakout of the double-quoted attr).
		const row = el.querySelector(".chart-readout-row") as HTMLElement;
		expect(row.dataset.rowKey).toBe('"><script>');
		expect(el.innerHTML).not.toContain('"><script>');
	});
});

describe("ChartReadoutBox — sticky hide state machine (CF-07)", () => {
	it("scheduleHide fires onHide once; cancelHide aborts a pending hide", () => {
		vi.useFakeTimers();
		const host = makeHost();
		const box = new ChartReadoutBox(host);
		const onHide = vi.fn();
		box.setInteractive({ onHide });
		box.show("t", [{ color: "red", label: "a", cells: [{ num: "1", unit: "" }] }], 10, 10);

		box.scheduleHide();
		vi.advanceTimersByTime(200);
		expect(onHide).toHaveBeenCalledTimes(1);

		// A cancelled hide never fires.
		box.show("t", [{ color: "red", label: "a", cells: [{ num: "1", unit: "" }] }], 10, 10);
		box.scheduleHide();
		box.cancelHide();
		vi.advanceTimersByTime(200);
		expect(onHide).toHaveBeenCalledTimes(1); // still just the first
	});
});

describe("ChartReadoutBox — positioning clamp (CF-07)", () => {
	// jsdom lays nothing out: every offsetWidth is 0, which made the old version of this suite
	// tautological — with boxW = 0 the clamp is arithmetically the identity, so the assertions held
	// whether or not the clamp existed. Giving the readout a real measured size is what puts teeth in it.
	const BOX_W = 120;
	const BOX_H = 40;

	function withMeasuredBox(run: () => void): void {
		const proto = HTMLElement.prototype as unknown as Record<string, unknown>;
		const widthDesc = Object.getOwnPropertyDescriptor(proto, "offsetWidth");
		const heightDesc = Object.getOwnPropertyDescriptor(proto, "offsetHeight");
		Object.defineProperty(proto, "offsetWidth", {
			configurable: true,
			get(this: HTMLElement) { return this.classList.contains("chart-readout") ? BOX_W : 0; },
		});
		Object.defineProperty(proto, "offsetHeight", {
			configurable: true,
			get(this: HTMLElement) { return this.classList.contains("chart-readout") ? BOX_H : 0; },
		});
		try { run(); } finally {
			if (widthDesc) Object.defineProperty(proto, "offsetWidth", widthDesc); else delete proto.offsetWidth;
			if (heightDesc) Object.defineProperty(proto, "offsetHeight", heightDesc); else delete proto.offsetHeight;
		}
	}

	function leftAfterShow(hostWidth: number, caretX: number): number {
		let left = NaN;
		withMeasuredBox(() => {
			const host = makeHost(hostWidth);
			const box = new ChartReadoutBox(host);
			box.show("t", [{ color: "red", label: "abcdef", cells: [{ num: "123", unit: "km" }] }], caretX, 40);
			left = parseFloat((host.querySelector(".chart-readout") as HTMLElement).style.left);
		});
		return left;
	}

	it("centres the box on the caret when there is room either side", () => {
		expect(leftAfterShow(400, 200)).toBeCloseTo(200 - BOX_W / 2, 1);
	});

	it("clamps left so the box never overflows the host's right edge", () => {
		// Un-clamped this would be 390 - 60 = 330, and 330 + 120 = 450 > 400: half the readout would
		// hang outside the chart.
		const left = leftAfterShow(400, 390);

		expect(left).toBeCloseTo(400 - BOX_W, 1);
		expect(left + BOX_W).toBeLessThanOrEqual(400);
	});

	it("clamps at zero rather than going negative near the left edge", () => {
		expect(leftAfterShow(400, 10)).toBe(0); // centring would want -50
	});

	it("pins to zero when the box is wider than its host", () => {
		expect(leftAfterShow(100, 95)).toBe(0); // maxLeft goes negative; the guard floors it
	});

	it("sits the box above the plot by the gap, using its measured height", () => {
		withMeasuredBox(() => {
			const host = makeHost(400);
			const box = new ChartReadoutBox(host);
			box.show("t", [{ color: "red", label: "abcdef", cells: [{ num: "1", unit: "km" }] }], 200, 100);
			const top = parseFloat((host.querySelector(".chart-readout") as HTMLElement).style.top);
			// plotTop - GAP - boxH: below the plot top by construction, and dependent on the real height.
			expect(top).toBeLessThan(100 - BOX_H);
		});
	});
});
