/**
 * shape-filter/rule-document.ts — translates the paint UI's zones/gates + extent to/from
 * the backend's versioned rule document (Segment.match_rules_json; see RuleDocument.cs).
 *
 * Pure translation, no DOM/map/network. The UI keeps its role-per-shape model; the wire
 * format decouples numbered geometry from membership/extent rules, so richer rule editing
 * can later replace this mapping without touching the backend.
 */

import { gateFromPoints, nextGateRole, zoneFromBounds } from './shape-model';
import { must } from '../../core/invariant';
import type { GateRole, GateShape, MatchMode, Shape, ZoneRole, ZoneShape } from './shape-model';
import type {
	ColumnFilter,
	ExtentRule,
	MembershipRule,
	RuleDocument,
	ShapeDef,
	ShapeGeom,
	ShapeGeomLineGeom,
	ShapeGeomRectGeom,
	TrackFilter,
	TrackFilterActivityTypeFilter,
	TrackFilterDateRangeFilter,
	TrackFilterGridFiltersFilter,
	TrackFilterOwnerFilter,
} from '../../generated/contracts';

export const RULES_VERSION = 3;

/** Server default for `RuleDocument.GateDebounceSeconds` (RuleDocument.cs). Same-gate crossings closer
 * than this collapse into one, so a track lingering on the line does not read as several efforts. The
 * doc omits the field at this value, and the server then supplies the same number. */
export const DEFAULT_GATE_DEBOUNCE_SECONDS = 15;

// The wire types are GENERATED from RuleDocument.cs (K3) — these aliases keep the established
// `…Dto` names so consumers read naturally next to the UI's own Shape/role model. The unions
// are real discriminated unions: narrowing on `type` narrows the payload.
export type RectGeomDto = ShapeGeomRectGeom;
export type LineGeomDto = ShapeGeomLineGeom;
export type ShapeGeomDto = ShapeGeom;
export type ShapeDefDto = ShapeDef;
export type ExtentDto = ExtentRule;

/** Generated union of C# MembershipRule (RuleDocument.cs). `zones` carries shape numbers: ZONE
 * numbers for the spatial rules, GATE numbers for 'startsAtGate'/'endsAtGate' (the C# base calls
 * the list Zones either way). The paint UI authors no gate rule today — it expresses gate intent
 * through the extent — so the two gate branches exist to READ and preserve a stored doc that
 * carries them (DM-1). */
export type MembershipRuleDto = MembershipRule;

/** The membership rules the paint UI cannot author, kept verbatim across a parse → resave so an owner
 * opening someone's hand-authored document does not silently drop them (DM-1). */
export function isGateMembershipRule(rule: MembershipRuleDto): boolean {
	return rule.type === 'startsAtGate' || rule.type === 'endsAtGate';
}

export type DateRangeFilterDto = TrackFilterDateRangeFilter;
export type ActivityTypeFilterDto = TrackFilterActivityTypeFilter;
export type OwnerFilterDto = TrackFilterOwnerFilter;

/**
 * One activities-table column's filter value on the wire — the generated `ColumnFilter`, all-optional
 * (K3 made it dual-use, so the doc itself is relaxed), so omit-when-empty construction types truthfully.
 * `in`/`range` are the single-box multiselect + numeric-range forms the grid emits; `min`/`max` are the
 * legacy two-box params (parsed but never emitted by the UI).
 */
export type ColumnFilterDto = ColumnFilter;

/** Column filters keyed by grid column key (distance/name/type/…). Mirror of `Dictionary<string, ColumnFilter>`. */
export type GridFiltersMap = Record<string, ColumnFilterDto>;

/** v3 filter: activities-table filters adopted as live rules. Owner stays a separate server-folded filter. */
export type GridFiltersFilterDto = TrackFilterGridFiltersFilter;

export type TrackFilterDto = TrackFilter;

export type RuleDocumentDto = RuleDocument;

/** Everything beyond the shapes and the extent mode that shapes the emitted document. An object
 * rather than trailing positional params — the call sites pass different subsets, and
 * `buildRuleDocument(shapes, mode, null, undefined, 30)` reads like nothing at all. */
export interface BuildRuleDocumentOptions {
	/** Activities-table column filters adopted as live rules (null / empty → no track filter emitted). */
	gridFilters?: GridFiltersMap | null;
	/** Gate membership rules the paint UI cannot author, carried verbatim from a parse (DM-1). */
	carryMembership?: readonly MembershipRuleDto[];
	/** Same-gate crossing debounce, seconds. Emitted only under a gates extent and only when it
	 * differs from {@link DEFAULT_GATE_DEBOUNCE_SECONDS}, so unrelated docs stay clean. */
	gateDebounceSeconds?: number;
}

/** Build the wire document from the painted shapes + the panel's extent mode. Shape numbers are
 * a single sequence across zones and gates, assigned by store order. */
export function buildRuleDocument(
	shapes: readonly Shape[],
	matchMode: MatchMode,
	{ gridFilters, carryMembership, gateDebounceSeconds }: BuildRuleDocumentOptions = {},
): RuleDocumentDto {
	const noOf = new Map<string, number>();
	shapes.forEach((s, i) => noOf.set(s.id, i + 1));

	const defs: ShapeDefDto[] = shapes.map((s) => ({
		no: must(noOf.get(s.id), 'noOf built from the same shapes array'),
		kind: s.kind,
		geom:
			s.kind === 'zone'
				? { type: 'rect', south: s.south, west: s.west, north: s.north, east: s.east }
				: { type: 'line', lat1: s.a.lat, lng1: s.a.lng, lat2: s.b.lat, lng2: s.b.lng, direction: s.direction },
	}));

	const zones = shapes.filter((s): s is ZoneShape => s.kind === 'zone');
	const gates = shapes.filter((s): s is GateShape => s.kind === 'gate');

	const numbersByRole = (role: ZoneRole): number[] =>
		zones.filter((z) => z.role === role).map((z) => must(noOf.get(z.id), 'noOf built from the same shapes array'));

	const membership: MembershipRuleDto[] = [];

	const all = numbersByRole('all');
	if (all.length > 0) membership.push({ type: 'passesAll', zones: all });

	// 'any' zones OR within their group, AND across groups → one passesAny rule per group.
	const anyGroups = new Map<number, number[]>();
	for (const z of zones) {
		if (z.role !== 'any') continue;
		const list = anyGroups.get(z.groupId) ?? [];
		list.push(must(noOf.get(z.id), 'noOf built from the same shapes array'));
		anyGroups.set(z.groupId, list);
	}
	for (const [group, list] of anyGroups) membership.push({ type: 'passesAny', zones: list, group });

	const within = numbersByRole('within');
	if (within.length > 0) membership.push({ type: 'staysWithin', zones: within });

	const avoid = numbersByRole('avoid');
	if (avoid.length > 0) membership.push({ type: 'avoids', zones: avoid });

	// Endpoint-anchor zones: one endpointInZone rule per endpoint (start/finish/either), OR'd within.
	for (const endpoint of ['start', 'finish', 'either'] as const) {
		const zs = numbersByRole(endpoint);
		if (zs.length > 0) membership.push({ type: 'endpointInZone', endpoint, zones: zs });
	}

	// Gate membership rules the UI cannot paint ride through untouched (DM-1) — their gate numbers are
	// stable because the shape sequence is rebuilt in the same store order it was parsed from.
	if (carryMembership) membership.push(...carryMembership);

	const extent = buildExtent(matchMode, zones, gates, noOf);
	const doc: RuleDocumentDto = {
		version: RULES_VERSION,
		shapes: defs,
		extent,
		membership,
	};

	// Gated on the EMITTED extent, not the requested mode: an incomplete gates config falls back to
	// whole-track, and a debounce stored on a whole-track document would be dead weight.
	if (extent.type === 'gates' && gateDebounceSeconds !== undefined && gateDebounceSeconds !== DEFAULT_GATE_DEBOUNCE_SECONDS)
		doc.gateDebounceSeconds = gateDebounceSeconds;

	// v3 adopted-grid-filters form. Owner is folded in server-side, never here.
	const nonEmptyGrid = gridFilters && Object.keys(gridFilters).length > 0 ? gridFilters : null;
	if (nonEmptyGrid) doc.trackFilters = [{ type: 'gridFilters', filters: nonEmptyGrid }];
	return doc;
}

/** Clip mode spans first→last sample inside the target zones. Avoid zones must not widen the
 * span; gates mode needs ≥1 start + ≥1 finish gate. Either falls back to whole-track until the
 * user has drawn enough shapes to support the chosen extent — that fallback only serves the
 * live preview; the save path refuses an incomplete gates config (see shape-filter.save). */
function buildExtent(
	matchMode: MatchMode,
	zones: readonly ZoneShape[],
	gates: readonly GateShape[],
	noOf: Map<string, number>,
): ExtentDto {
	if (matchMode === 'gates') {
		const startGates = gates.filter((g) => g.role === 'start' || g.role === 'both').map((g) => must(noOf.get(g.id), 'noOf built from the same shapes array'));
		const finishGates = gates.filter((g) => g.role === 'finish' || g.role === 'both').map((g) => must(noOf.get(g.id), 'noOf built from the same shapes array'));
		if (startGates.length > 0 && finishGates.length > 0) return { type: 'gates', startGates, finishGates };
		return { type: 'whole' };
	}
	if (matchMode === 'clip') {
		const clipZones = zones.filter((z) => z.role !== 'avoid').map((z) => must(noOf.get(z.id), 'noOf built from the same shapes array'));
		if (clipZones.length > 0) return { type: 'zoneSpan', zones: clipZones };
		return { type: 'whole' };
	}
	return { type: 'whole' };
}

/** Rebuild the paint UI state (zones/gates with roles + extent mode + track filters) from a stored
 * document. `ownerUserIds` is the doc-authored owner scope (null when the doc carries no owner filter). */
export function parseRuleDocument(doc: RuleDocumentDto): {
	shapes: Shape[];
	matchMode: MatchMode;
	/** Adopted activities-table filters: explicit v3 `gridFilters`, or legacy v2 date/type converted to the
	 * same shape (`date:{from,to}` / `type:{in}`). Null when the doc carries no non-spatial track filter. */
	gridFilters: GridFiltersMap | null;
	ownerUserIds: string[] | null;
	/** Verbatim gate membership rules — pass back to buildRuleDocument so a resave keeps them (DM-1). */
	carryMembership: MembershipRuleDto[];
	/** Same-gate crossing debounce; the server default when the doc omits it. */
	gateDebounceSeconds: number;
} {
	const zoneRoleByNo = new Map<number, { role: ZoneRole; groupId: number }>();
	const carryMembership: MembershipRuleDto[] = [];
	for (const rule of doc.membership ?? []) {
		if (isGateMembershipRule(rule)) { carryMembership.push(rule); continue; } // gate numbers, not zone numbers
		for (const no of rule.zones ?? []) {
			switch (rule.type) {
				case 'passesAll': zoneRoleByNo.set(no, { role: 'all', groupId: 0 }); break;
				case 'passesAny': zoneRoleByNo.set(no, { role: 'any', groupId: rule.group ?? 0 }); break;
				case 'staysWithin': zoneRoleByNo.set(no, { role: 'within', groupId: 0 }); break;
				case 'avoids': zoneRoleByNo.set(no, { role: 'avoid', groupId: 0 }); break;
				case 'endpointInZone': zoneRoleByNo.set(no, { role: rule.endpoint ?? 'start', groupId: 0 }); break;
			}
		}
	}

	const gateRoleByNo = new Map<number, GateRole>();
	// One shared accumulation rule for every source that names a gate. A gate only becomes 'both' when
	// it is claimed as the OTHER role — merely being named twice as the same role is idempotent. The
	// membership and extent loops used to carry separate copies of this, and the copies disagreed:
	// the extent one tested presence instead of the stored role, so a gate a startsAtGate rule had
	// already marked 'start' was promoted to 'both' and a resave then wrote it into finishGates too.
	const markGate = (no: number, role: GateRole): void => {
		const prev = gateRoleByNo.get(no);
		gateRoleByNo.set(no, prev !== undefined && prev !== role ? 'both' : role);
	};
	// A gate rule paints its gate's role even when the extent does not (the two say different things:
	// membership filters which tracks qualify, extent clips the effort).
	for (const rule of carryMembership)
		for (const no of rule.zones ?? []) markGate(no, rule.type === 'startsAtGate' ? 'start' : 'finish');
	if (doc.extent?.type === 'gates') {
		for (const no of doc.extent.startGates ?? []) markGate(no, 'start');
		for (const no of doc.extent.finishGates ?? []) markGate(no, 'finish');
	}

	const shapes: Shape[] = [];
	for (const s of doc.shapes ?? []) {
		if (s.geom?.type === 'rect') {
			const bound = zoneRoleByNo.get(s.no) ?? { role: 'any' as ZoneRole, groupId: 0 };
			shapes.push(zoneFromBounds(s.geom, bound.role, bound.groupId));
		} else if (s.geom?.type === 'line') {
			const role = gateRoleByNo.get(s.no) ?? nextGateRole(shapes); // whole/clip drop roles → draw-time auto-assign (SP-07)
			shapes.push(gateFromPoints({ lat: s.geom.lat1, lng: s.geom.lng1 }, { lat: s.geom.lat2, lng: s.geom.lng2 }, role, s.geom.direction));
		}
	}

	const matchMode: MatchMode = doc.extent?.type === 'zoneSpan' ? 'clip' : doc.extent?.type === 'gates' ? 'gates' : 'whole';

	let explicitGrid: GridFiltersMap | null = null;
	const legacyGrid: GridFiltersMap = {};
	let ownerUserIds: string[] | null = null;
	for (const f of doc.trackFilters ?? []) {
		if (f.type === 'dateRange') {
			if (f.from || f.to) legacyGrid.date = { ...(f.from ? { from: f.from } : {}), ...(f.to ? { to: f.to } : {}) };
		} else if (f.type === 'activityType') {
			if (f.types.length > 0) legacyGrid.type = { in: [...f.types] };
		} else if (f.type === 'owner') {
			ownerUserIds = [...f.userIds];
		} else if (f.type === 'gridFilters') {
			explicitGrid = { ...f.filters };
		}
	}

	const gridFilters = explicitGrid ?? (Object.keys(legacyGrid).length > 0 ? legacyGrid : null);
	return {
		shapes,
		matchMode,
		gridFilters,
		ownerUserIds,
		carryMembership,
		gateDebounceSeconds: doc.gateDebounceSeconds ?? DEFAULT_GATE_DEBOUNCE_SECONDS,
	};
}
