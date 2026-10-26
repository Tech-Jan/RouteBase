// @vitest-environment node
import { describe, it, expect, afterEach } from "vitest";
import { installFakeRaf, type FakeRaf } from "./raf-test-harness";
import { afterLayout } from "./after-layout";

// The point of afterLayout is that ONE frame is not enough: the reveal commits on frame 1 and
// the element's real size is only readable on frame 2. A single-rAF (or a magic setTimeout) is the
// bug it exists to prevent, so pin the two-frame wait explicitly.
describe("afterLayout", () => {
	let raf: FakeRaf;
	afterEach(() => raf?.restore());

	it("runs the callback only after the SECOND frame", () => {
		raf = installFakeRaf();
		let ran = 0;
		afterLayout(() => { ran++; });

		expect(ran).toBe(0);            // nothing yet
		raf.flushFrames(1);
		expect(ran).toBe(0);            // one frame is not enough (this is the whole point)
		raf.flushFrames(1);
		expect(ran).toBe(1);            // fires after layout has settled
	});
});
