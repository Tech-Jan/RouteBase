import { describe, it, expect } from 'vitest';
import { buildRuleDocument, parseRuleDocument, DEFAULT_GATE_DEBOUNCE_SECONDS, RULES_VERSION } from './rule-document';
import { gateFromPoints, zoneFromBounds, type Shape, type ZoneRole, type ZoneShape } from './shape-model';

const bounds = { south: 0, west: 0, north: 1, east: 1 };
const zoneRolesOf = (shapes: Shape[]): ZoneRole[] =>
	shapes.filter((s): s is ZoneShape => s.kind === 'zone').map((z) => z.role);

describe('rule-document round-trip', () => {
	it('exposes RULES_VERSION 3 (server CurrentVersion parity, SP-06)', () => {
		expect(RULES_VERSION).toBe(3);
	});

	it('clip extent with only avoid zones falls back to whole (SP-05)', () => {
		const shapes: Shape[] = [zoneFromBounds(bounds, 'avoid')];
		const doc = buildRuleDocument(shapes, 'clip');
		expect(doc.extent.type).toBe('whole');
	});

	it('parseRuleDocument returns ownerUserIds null when doc has no owner filter (SP-02)', () => {
		// Owner scope is server-authored — buildRuleDocument never emits an owner filter. The pin-to-Me
		// fix in beginEdit relies on this null so own-only/legacy segments re-save own-scoped.
		const doc = buildRuleDocument([zoneFromBounds(bounds, 'any')], 'whole');
		expect(parseRuleDocument(doc).ownerUserIds).toBeNull();
	});

	it('round-trips all five membership-rule kinds (all/any/within/avoid/endpointInZone)', () => {
		const mk = (n: number, role: ZoneRole, groupId = 0): ZoneShape =>
			zoneFromBounds({ south: n, west: n, north: n + 1, east: n + 1 }, role, groupId);
		const shapes: Shape[] = [
			mk(0, 'all'),
			mk(2, 'any', 5),
			mk(4, 'within'),
			mk(6, 'avoid'),
			mk(8, 'start'), // endpointInZone
		];
		const doc = buildRuleDocument(shapes, 'whole');

		// The wire doc carries exactly one membership rule per kind.
		expect(doc.membership.map((m) => m.type).sort()).toEqual(
			['avoids', 'endpointInZone', 'passesAll', 'passesAny', 'staysWithin'],
		);

		// …and each zone role survives the round-trip in store order, group id included.
		const back = parseRuleDocument(doc).shapes;
		expect(zoneRolesOf(back)).toEqual(['all', 'any', 'within', 'avoid', 'start']);
		const anyZone = back.find((s): s is ZoneShape => s.kind === 'zone' && s.role === 'any')!;
		expect(anyZone.groupId).toBe(5);
	});

	it.each(['start', 'finish', 'either'] as const)('round-trips an endpointInZone anchored at %s', (endpoint) => {
		const doc = buildRuleDocument([zoneFromBounds(bounds, endpoint)], 'whole');
		expect(doc.membership).toEqual([{ type: 'endpointInZone', endpoint, zones: [1] }]);
		expect(zoneRolesOf(parseRuleDocument(doc).shapes)).toEqual([endpoint]);
	});

	it('keeps startsAtGate/endsAtGate rules a resave cannot author (DM-1)', () => {
		// The paint UI expresses gate intent through the extent, so it never builds these two rules — but
		// C# defines them (RuleDocument.cs) and a stored doc can carry them. Before DM-1 the union omitted
		// them and an owner's resave rewrote the document WITHOUT them: silent rule loss, no error.
		const gate = gateFromPoints({ lat: 0, lng: 0 }, { lat: 0, lng: 1 }, 'start');
		const stored = {
			...buildRuleDocument([gate], 'whole'),
			membership: [
				{ type: 'startsAtGate' as const, zones: [1] },
				{ type: 'endsAtGate' as const, zones: [1] },
			],
		};

		const parsed = parseRuleDocument(stored);
		expect(parsed.carryMembership).toEqual(stored.membership);
		// A gate named by both rules reads as 'both' — the rules paint the role the extent doesn't state.
		expect(parsed.shapes.map((s) => (s.kind === 'gate' ? s.role : ''))).toEqual(['both']);

		const resaved = buildRuleDocument(parsed.shapes, parsed.matchMode, { carryMembership: parsed.carryMembership });
		expect(resaved.membership).toEqual(stored.membership);
	});

	it('does not promote a gate to both when a gate rule and the extent agree on its role', () => {
		// The extent's role accumulation used to test PRESENCE, not the stored role: a gate that a
		// startsAtGate rule had already marked 'start' was flipped to 'both' by startGates naming it
		// 'start' again, and the resave then wrote it into finishGates too — silently turning a
		// start-only gate into a lap course. Only the OTHER role may promote to 'both'.
		const start = gateFromPoints({ lat: 0, lng: 0 }, { lat: 0, lng: 1 }, 'start');
		const finish = gateFromPoints({ lat: 1, lng: 0 }, { lat: 1, lng: 1 }, 'finish');
		const stored = {
			...buildRuleDocument([start, finish], 'gates'),
			membership: [{ type: 'startsAtGate' as const, zones: [1] }],
		};
		expect(stored.extent).toEqual({ type: 'gates', startGates: [1], finishGates: [2] });

		const parsed = parseRuleDocument(stored);
		expect(parsed.shapes.map((s) => (s.kind === 'gate' ? s.role : ''))).toEqual(['start', 'finish']);

		// …and the resave reproduces the extent it was given, rather than widening gate 1 into finishGates.
		const resaved = buildRuleDocument(parsed.shapes, parsed.matchMode, { carryMembership: parsed.carryMembership });
		expect(resaved.extent).toEqual(stored.extent);
		expect(resaved.membership).toEqual(stored.membership);
	});

	describe('gateDebounceSeconds', () => {
		const start = gateFromPoints({ lat: 0, lng: 0 }, { lat: 0, lng: 1 }, 'start');
		const finish = gateFromPoints({ lat: 1, lng: 0 }, { lat: 1, lng: 1 }, 'finish');

		it('omits the field at the server default so unrelated docs stay clean', () => {
			const doc = buildRuleDocument([start, finish], 'gates', { gateDebounceSeconds: DEFAULT_GATE_DEBOUNCE_SECONDS });
			expect(doc.gateDebounceSeconds).toBeUndefined();
			// …and the parse supplies the same number the server would.
			expect(parseRuleDocument(doc).gateDebounceSeconds).toBe(DEFAULT_GATE_DEBOUNCE_SECONDS);
		});

		it('emits and round-trips a non-default value', () => {
			const doc = buildRuleDocument([start, finish], 'gates', { gateDebounceSeconds: 30 });
			expect(doc.gateDebounceSeconds).toBe(30);
			expect(parseRuleDocument(doc).gateDebounceSeconds).toBe(30);
		});

		it('emits 0 — "count every crossing" is a real choice, not an absent one', () => {
			const doc = buildRuleDocument([start, finish], 'gates', { gateDebounceSeconds: 0 });
			expect(doc.gateDebounceSeconds).toBe(0);
			expect(parseRuleDocument(doc).gateDebounceSeconds).toBe(0);
		});

		it('is dropped when the extent is not gates', () => {
			// Gated on the EMITTED extent, so an incomplete gates config (which falls back to whole)
			// does not smuggle a debounce onto a whole-track document either.
			expect(buildRuleDocument([start, finish], 'whole', { gateDebounceSeconds: 30 }).gateDebounceSeconds).toBeUndefined();
			expect(buildRuleDocument([start], 'gates', { gateDebounceSeconds: 30 }).extent.type).toBe('whole');
			expect(buildRuleDocument([start], 'gates', { gateDebounceSeconds: 30 }).gateDebounceSeconds).toBeUndefined();
		});
	});

	it('round-trips gate roles start/finish under whole extent via auto-assign (SP-07)', () => {
		const start = gateFromPoints({ lat: 0, lng: 0 }, { lat: 0, lng: 1 }, 'start');
		const finish = gateFromPoints({ lat: 1, lng: 0 }, { lat: 1, lng: 1 }, 'finish');
		const doc = buildRuleDocument([start, finish], 'whole');

		const { shapes } = parseRuleDocument(doc);
		const roles = shapes.filter((s) => s.kind === 'gate').map((s) => (s.kind === 'gate' ? s.role : ''));
		expect(roles).toEqual(['start', 'finish']);
	});
});
