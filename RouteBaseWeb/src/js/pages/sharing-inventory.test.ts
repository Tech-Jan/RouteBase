// Pins the /Sharing inventory's pure rendering: what a row says, where it links, and that a
// user-authored resource name cannot become markup.

import { describe, it, expect } from "vitest";
import { must } from '../core/invariant';
import type { ShareLinkInventoryItemDto } from "../generated/contracts";
import {
	formatExpiry,
	formatOpens,
	groupByKind,
	kindLabel,
	renderInventory,
	resourceHref,
} from "./sharing-inventory";

const NOW = new Date("2026-07-24T12:00:00Z");

function link(over: Partial<ShareLinkInventoryItemDto> = {}): ShareLinkInventoryItemDto {
	return {
		id: 1,
		kind: "activity",
		resourceId: 42,
		resourceName: "Morning ride",
		createdAt: "2026-07-20T09:00:00Z",
		expiresAt: "2026-07-27T09:00:00Z",
		openCount: 0,
		lastOpenedAt: null,
		...over,
	} as ShareLinkInventoryItemDto;
}

describe("resourceHref", () => {
	it("sends each kind to the page that owns it", () => {
		expect(resourceHref("activity", 42)).toBe("/Activities/Details/42");
		expect(resourceHref("segment", 7)).toBe("/Segments/Statistics?id=7");
		expect(resourceHref("race", 9)).toBe("/Races/Replay?raceId=9");
	});

	it("sends a live recording to its activity shell — live is keyed by track_id", () => {
		expect(resourceHref("live", 42)).toBe("/Activities/Details/42");
	});
});

describe("kindLabel", () => {
	it("keeps an unknown kind visible under its wire name", () => {
		// A kind this build does not recognise is exactly the link an owner most needs to see.
		expect(kindLabel("something-new")).toBe("something-new");
	});
});

describe("groupByKind", () => {
	it("groups in display order and drops empty kinds", () => {
		const groups = groupByKind([link({ kind: "race" }), link({ kind: "activity" })]);

		expect(groups.map((g) => g.kind)).toEqual(["activity", "race"]);
	});

	it("keeps unknown kinds rather than discarding their links", () => {
		const groups = groupByKind([link({ kind: "mystery" })]);

		expect(groups.map((g) => g.kind)).toEqual(["mystery"]);
		expect(must(groups[0], 'fixture group 0').links).toHaveLength(1);
	});
});

describe("formatExpiry", () => {
	it("counts down in the unit that fits", () => {
		expect(formatExpiry("2026-07-27T12:00:00Z", NOW)).toBe("Expires in 3 days");
		expect(formatExpiry("2026-07-25T00:00:00Z", NOW)).toBe("Expires in 12 hours");
		expect(formatExpiry("2026-07-24T12:30:00Z", NOW)).toBe("Expires within the hour");
	});

	it("labels a link that lapsed while the tab sat open as Expired, not within-the-hour", () => {
		// The list is fetched once and read later; a negative delta fell into `hours < 1`.
		expect(formatExpiry("2026-07-24T11:00:00Z", NOW)).toBe("Expired");
	});

	it("says the singular without an off-by-one 's'", () => {
		expect(formatExpiry("2026-07-25T13:00:00Z", NOW)).toBe("Expires in 1 day");
	});
});

describe("formatOpens", () => {
	it("distinguishes never-opened from opened", () => {
		expect(formatOpens(link({ openCount: 0 }))).toBe("Not opened yet");
		expect(formatOpens(link({ openCount: 1, lastOpenedAt: null }))).toBe("Opened once");
		expect(formatOpens(link({ openCount: 5, lastOpenedAt: null }))).toBe("Opened 5×");
	});
});

describe("renderInventory", () => {
	it("says plainly when nothing is published", () => {
		expect(renderInventory([], NOW)).toContain("Nothing is shared right now");
	});

	it("counts the live links and renders one revoke button per row", () => {
		const html = renderInventory([link({ id: 11 }), link({ id: 12, kind: "segment" })], NOW);

		expect(html).toContain("2 live links");
		expect(html).toContain('data-revoke-id="11"');
		expect(html).toContain('data-revoke-id="12"');
	});

	it("escapes a resource name — an activity title is user-authored (FE-10)", () => {
		const html = renderInventory([link({ resourceName: '<img src=x onerror="alert(1)">' })], NOW);

		expect(html).not.toContain("<img");
		expect(html).toContain("&lt;img");
	});

	it("never renders a token — the plaintext exists only in the mint response", () => {
		const html = renderInventory([link()], NOW);

		expect(html).not.toContain("/s/");
	});
});
