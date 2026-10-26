import { describe, it, expect, beforeEach } from "vitest";
import { setChartEmptyState } from "./chart-empty-state";

// The contract: an empty chart is VISIBLY empty (a message), never a blank canvas — the failure
// mode the owner reported on the progress chart. These pin the message + canvas-hide behaviour.

function hostWithCanvas() {
	const host = document.createElement("div");
	const canvas = document.createElement("canvas");
	host.appendChild(canvas);
	document.body.appendChild(host);
	return { host, canvas };
}

describe("setChartEmptyState", () => {
	beforeEach(() => { document.body.innerHTML = ""; });

	it("shows a visible message and hides the canvas when empty", () => {
		const { host, canvas } = hostWithCanvas();
		setChartEmptyState(host, "no data", true);
		const el = host.querySelector<HTMLElement>(".chart-empty-state")!;
		expect(el).not.toBeNull();
		expect(el.textContent).toBe("no data");
		expect(el.style.display).toBe("flex");
		expect(canvas.style.visibility).toBe("hidden");
	});

	it("hides the message and restores the canvas when not empty", () => {
		const { host, canvas } = hostWithCanvas();
		setChartEmptyState(host, "no data", true);
		setChartEmptyState(host, "", false);
		expect(host.querySelector<HTMLElement>(".chart-empty-state")!.style.display).toBe("none");
		expect(canvas.style.visibility).toBe("");
	});

	it("is idempotent — repeated empties reuse one overlay and update the message", () => {
		const { host } = hostWithCanvas();
		setChartEmptyState(host, "first", true);
		setChartEmptyState(host, "second", true);
		expect(host.querySelectorAll(".chart-empty-state").length).toBe(1);
		expect(host.querySelector<HTMLElement>(".chart-empty-state")!.textContent).toBe("second");
	});

	it("no-ops on a null host", () => {
		expect(() => setChartEmptyState(null, "x", true)).not.toThrow();
	});
});
