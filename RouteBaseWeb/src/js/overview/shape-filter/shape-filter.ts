/**
 * shape-filter/shape-filter.ts — composition root.
 *
 * Wires store ↔ layer ↔ interactions ↔ service ↔ panel and owns the small bits of
 * cross-cutting state (current tool + selection). It is the only module that knows
 * about all the others; each of them depends only on abstractions.
 *
 * Data flow is one-directional: interactions → store → {layer, service, summary} → panel.
 */

import type { MapShellInstance } from '../../core/core-types';
import { ShapeStore } from './shape-store';
import { ShapeFilterService, type PreviewResult } from './shape-filter-service';
import { ShapeFilterPanel } from './shape-filter-panel';
import { ShapeLayer } from './shape-layer';
import { ShapeInteractions, type CycleInfo, type Tool } from './shape-interactions';
import { createMapLibreAdapter, type MapAdapter } from './map-adapter';
import { openSegmentWhenAnalyzed } from '../../core/segment-analysis-wait';
import { startHeatmapPolling } from '../../core/op-polling-bridge';
import { getActivitiesOwnerFilter, setActivitiesOwnerFilter, setActivitiesSpatialFilter } from '../overview-grid-bridge';
import { OWNER_ME_TOKEN } from '../../core/grid/owner-tokens';
import { getActivitiesMapShell, setOverviewHighlight } from '../activities-overview';
import { buildRuleDocument, parseRuleDocument, DEFAULT_GATE_DEBOUNCE_SECONDS, RULES_VERSION, type MembershipRuleDto } from './rule-document';
import { buildRuleSummary } from './shape-summary';
import { formatGridFilters } from './grid-filters-format';
import { badgeLabel, hasGatesExtentConfig, nextGateRole } from './shape-model';
import type { CrossDirection, GateRole, MatchMode, ZoneRole } from './shape-model';
import type { ShapeSegmentEditResponse, ShapeSegmentResponse } from '../../generated/contracts';

const SVG_NS = 'http://www.w3.org/2000/svg';

interface Bound {
	overlay: HTMLDivElement;
	adapter: MapAdapter;
	layer: ShapeLayer;
	interactions: ShapeInteractions;
}

class ShapeFilter {
	private readonly store = new ShapeStore();
	private readonly panel: ShapeFilterPanel;
	private readonly service: ShapeFilterService;

	private tool: Tool | null = null; // null = map-navigation (no tool armed)
	private defaultZoneRole: ZoneRole = 'any';
	// null = auto (first gate → start, rest → finish); a pick pins it for the next drawn gate.
	private defaultGateRole: GateRole | null = null;
	private defaultGateDirection: CrossDirection = 'any';
	private matchMode: MatchMode = 'whole';
	// Same-gate crossing debounce. Only reaches the wire under a gates extent, and only when it
	// differs from the server default (see buildRuleDocument).
	private gateDebounceSeconds: number = DEFAULT_GATE_DEBOUNCE_SECONDS;
	// Gate membership rules the paint UI cannot author, held from the loaded document so a resave keeps
	// them instead of silently dropping them (DM-1). Cleared with the edit context.
	private carryMembership: MembershipRuleDto[] = [];
	private selectedId: string | null = null;
	private selectionCycle: CycleInfo | null = null; // stack position of the selection, for the "(2/3)" hint
	private bound: Bound | null = null;
	private active = false; // paint mode on → dim non-matching tracks
	private awaitingMap = false; // armed before the map loaded; activation fires when it lands
	private lastCounts: { count: number; exact: boolean; effortCount?: number | undefined } = { count: 0, exact: true };
	private tableFilterTimer: number | undefined; // debounce for the shape→table restriction push (merge)

	// Edit mode: when set, we're editing an existing segment loaded via ?editSegment=<id>.
	// Saving with the same name overwrites this id; renaming saves a fresh copy instead.
	private editingSegmentId: number | null = null;
	private originalName = '';

	constructor(host: HTMLElement, private readonly getShell: () => MapShellInstance | null) {
		this.panel = new ShapeFilterPanel(host, {
			onToggleActive: (active) => this.setActive(active),
			onToolChange: (t) => this.armTool(t),
			onZoneRoleChange: (role) => this.applyZoneRole(role),
			onGateRoleChange: (role) => this.applyGateRole(role),
			onGateDirectionChange: (direction) => this.applyGateDirection(direction),
			// Extent can change which tracks match (e.g. stays-within on the clipped span),
			// so the preview count must refresh with it.
			onMatchModeChange: (mode) => { this.matchMode = mode; this.service.refresh(); this.updateSummary(); this.syncTableFilter(); },
			// Changes which crossings count, so it changes the effort count — same refresh as the extent.
			onGateDebounceChange: (seconds) => { this.gateDebounceSeconds = seconds; this.service.refresh(); this.updateSummary(); },
			// Merge toggle / adopted table filters changed: refresh the preview count + summary, and (de)apply
			// the shape→table restriction so the table stays in step with the map.
			onAdoptedFiltersChange: () => { this.service.refresh(); this.updateSummary(); this.syncTableFilter(); },
			onDeleteSelected: () => this.deleteSelected(),
			onClear: () => { this.store.clear(); this.select(null); },
			onSave: () => void this.save(),
		});

		this.store.subscribe(() => {
			this.updateSummary();
			// Extent options depend on what's been drawn: clip needs a non-avoid zone (avoid zones don't
			// widen the clip span, so they can't enable "Clip to zones" — SP-05); gates need gates.
			this.panel.setShapeCounts(
				this.store.zones.filter((z) => z.role !== 'avoid').length, this.store.gates.length);
			// A shape edit changes what the merge restricts the table to — keep the table in step.
			this.syncTableFilter();
		});
		this.service = new ShapeFilterService(
			this.store, () => this.matchMode, () => this.panel.getAdoptedGridFilters(),
			() => this.gateDebounceSeconds, (r) => this.onPreview(r));
		this.service.start();
	}

	// ── activation: lazily bind to the live map ──────────────────────────────

	private setActive(active: boolean): void {
		if (active) {
			// Armed before the overview map finished loading (it only exists once the GPS data is in).
			// Wait for it rather than refusing — the old alert asked the user to hand-retry exactly what
			// waitForMap does, while a modal froze the load it was waiting for.
			if (!this.bound && !this.mapReady()) {
				void this.activateWhenMapReady();
				return;
			}
			if (!this.bound && !this.bind()) {
				this.panel.setActive(false);
				return;
			}
			this.active = true;
			this.bound?.layer.render();
			this.disarm(); // start in map-navigation; user picks a tool to begin
			this.applyHighlight();
			document.addEventListener('keydown', this.onKeyDown);
		} else {
			this.awaitingMap = false; // toggling off while it spins is the cancel
			this.active = false;
			this.disarm();
			document.removeEventListener('keydown', this.onKeyDown);
			setOverviewHighlight('paint', null); // release the paint source → grid mode shows through
		}
		this.syncTableFilter(); // (de)apply the shape→table restriction for the new active state
	}

	/**
	 * Hold the activation until the map exists, then run it. The button reads "Waiting for map…"
	 * meanwhile; a second click (which toggles paint mode off) cancels. Same wait beginEdit uses.
	 */
	private async activateWhenMapReady(): Promise<void> {
		if (this.awaitingMap) return;
		this.awaitingMap = true;
		this.panel.setWaiting(true);
		const ready = await this.waitForMap();
		this.panel.setWaiting(false);
		const stillWanted = this.awaitingMap;
		this.awaitingMap = false;
		if (!stillWanted) return; // cancelled while waiting
		if (!ready) {
			console.warn('shape-filter: map never became ready, cannot enter paint mode');
			this.panel.setActive(false);
			return;
		}
		this.setActive(true);
	}

	/**
	 * Push (or release) the drawn shapes as the activities table's spatial filter — the shape→table half of the
	 * "Merge table ↔ shapes" toggle. Debounced (the store fires per pointer move while drawing/resizing). Built
	 * with null grid filters: the grid applies its own column filters, so the table shows shape ∩ columns and
	 * the map follows filteredIds. Off / inactive / no shapes → null (grid drops the restriction).
	 */
	private syncTableFilter(): void {
		if (this.tableFilterTimer !== undefined) clearTimeout(this.tableFilterTimer);
		this.tableFilterTimer = window.setTimeout(() => {
			this.tableFilterTimer = undefined;
			const on = this.active && this.panel.mergeRestrictsTable() && this.store.count > 0;
			setActivitiesSpatialFilter(on ? buildRuleDocument(this.store.getAll(), this.matchMode) : null);
		}, 250);
	}

	/** Escape deselects the current shape (and clears the overlap cycle anchor). */
	private onKeyDown = (e: KeyboardEvent): void => {
		if (e.key === 'Escape' && this.selectedId) {
			this.select(null);
			this.bound?.interactions.resetCycle();
		}
	};

	/** While painting, dim every track except the ones the current shapes match. */
	private applyHighlight(): void {
		if (!this.active) return;
		// With shapes drawn, lastTrackIds (possibly []) is the paint highlight — [] correctly dims
		// every track. With no shapes, null releases the source so the grid's display mode shows through.
		const ids = this.store.count > 0 ? this.lastTrackIds : null;
		setOverviewHighlight('paint', ids);
	}

	private bind(): boolean {
		const adapter = this.buildAdapter();
		if (!adapter) {
			// Unreachable via setActive/beginEdit — both gate on mapReady() first. A null here means the
			// map went away between the check and the bind, which the caller reports as a failed activation.
			console.error('shape-filter: no MapLibre map at bind time');
			return false;
		}

		const overlay = document.createElement('div');
		overlay.className = 'shape-filter-overlay';
		const svg = document.createElementNS(SVG_NS, 'svg');
		svg.setAttribute('class', 'shape-filter-svg');
		overlay.appendChild(svg);
		adapter.getContainer().appendChild(overlay);

		const layer = new ShapeLayer(svg, adapter, this.store, () => this.selectedId);
		const interactions = new ShapeInteractions(overlay, adapter, this.store, {
			getTool: () => this.tool ?? 'draw-zone', // only read while a tool is armed (non-null)
			getSelectedId: () => this.selectedId,
			onSelect: (id, cycle) => this.select(id, cycle),
			// Keep the just-drawn shape selected and switch to Select, so its role/move/resize are
			// editable immediately (previously we disarmed to navigation and dropped the selection).
			onDrawFinished: (placed) => { if (placed) this.armTool('select'); },
			getDefaultZoneRole: () => this.defaultZoneRole,
			getDefaultGateRole: () => this.effectiveGateRole(),
			getDefaultGateDirection: () => this.defaultGateDirection,
		});

		this.bound = { overlay, adapter, layer, interactions };
		return true;
	}

	/** Build the draw-overlay adapter over the live MapLibre map (null until it exists). */
	private buildAdapter(): MapAdapter | null {
		const map = this.getShell()?.getMapLibreMap();
		return map ? createMapLibreAdapter(map) : null;
	}

	// ── state transitions ────────────────────────────────────────────────────

	/** Arm a tool, or — if it's already the active tool — toggle back to map-navigation. */
	private armTool(tool: Tool): void {
		if (this.tool === tool) {
			this.disarm();
			return;
		}
		this.tool = tool;
		this.panel.setTool(tool);
		this.bound?.interactions.enable();
		this.syncRoles();
	}

	/** Disarm to map-navigation: release the overlay so the map can pan/zoom again. */
	private disarm(): void {
		this.tool = null;
		this.bound?.interactions.disable();
		this.panel.setTool(null);
		// Toggling a tool off is an explicit "done editing" — drop the selection (and its handles)
		// and fall back to the zone draft row. Post-draw editing goes through Select, not here.
		this.select(null);
	}

	private select(id: string | null, cycle?: CycleInfo): void {
		this.selectedId = id;
		this.selectionCycle = cycle ?? null;
		this.syncRoles();
		this.bound?.layer.render();
	}

	/** Effective role the next drawn gate gets: an explicit pick, else auto (start-if-none-else-finish). */
	private effectiveGateRole(): GateRole {
		return this.defaultGateRole ?? nextGateRole(this.store.getAll());
	}

	/** Push the role UI so the visible row + highlight match the current focus: the selected shape's
	 * kind (its role), or — with nothing selected — the armed draw tool's kind (the pending default). */
	private syncRoles(): void {
		const selected = this.selectedId ? this.store.get(this.selectedId) ?? null : null;
		if (selected) {
			this.panel.setSelected(selected, badgeLabel(this.store.getAll(), selected.id), this.selectionCycle);
			return;
		}
		// No selection: a draw tool armed → the draft-role row for its kind; otherwise the "tap a shape" hint.
		const draftKind = this.tool === 'draw-zone' ? 'zone' : this.tool === 'draw-gate' ? 'gate' : null;
		this.panel.setDraftRole(draftKind, this.defaultZoneRole, this.effectiveGateRole(), this.defaultGateDirection);
	}

	private applyZoneRole(role: ZoneRole): void {
		this.defaultZoneRole = role;
		const id = this.selectedId;
		const selected = id ? this.store.get(id) : null;
		if (id && selected?.kind === 'zone') this.store.setZoneRole(id, role);
		this.select(this.selectedId); // re-render + re-highlight (or refresh the draft highlight)
	}

	// Gate role/direction act on the selected gate when there is one; otherwise a role click pre-picks
	// the default for the next drawn gate. Either way syncRoles re-pushes the highlight so it never
	// goes stale (and the direction cycle doesn't get stuck on its first step).

	private applyGateRole(role: GateRole): void {
		const id = this.selectedId;
		const selected = id ? this.store.get(id) : null;
		if (id && selected?.kind === 'gate') this.store.setGateRole(id, role);
		else this.defaultGateRole = role;
		this.select(this.selectedId);
	}

	private applyGateDirection(direction: CrossDirection): void {
		const id = this.selectedId;
		const selected = id ? this.store.get(id) : null;
		if (id && selected?.kind === 'gate') this.store.setGateDirection(id, direction);
		else this.defaultGateDirection = direction;
		this.select(this.selectedId);
	}

	private deleteSelected(): void {
		if (!this.selectedId) return;
		this.store.remove(this.selectedId);
		this.select(null);
	}

	private lastTrackIds: number[] = [];

	private onPreview(r: PreviewResult): void {
		this.panel.setCount(r.count, r.exact);
		this.panel.setSaveEnabled(this.store.count > 0);
		this.lastTrackIds = r.trackIds;
		this.lastCounts = { count: r.count, exact: r.exact, effortCount: r.effortCount };
		this.updateSummary();
		this.applyHighlight();
	}

	private updateSummary(): void {
		const filtersText = formatGridFilters(this.panel.getAdoptedGridFilters());
		this.panel.setSummary(
			buildRuleSummary(this.store.getAll(), this.matchMode, filtersText, this.lastCounts, this.gateDebounceSeconds));
	}

	// ── edit mode: re-hydrate paint mode from an existing segment ─────────────

	/**
	 * Load an existing shape segment (opened via ?editSegment=<id>) into paint mode: its shapes,
	 * extent and name. Waits for the overview map to finish loading before binding the overlay,
	 * since the map shell only exists once GPS data has loaded.
	 */
	async beginEdit(segmentId: number): Promise<void> {
		if (!(await this.waitForMap())) {
			console.warn('shape-filter: map never became ready, cannot enter edit mode');
			return;
		}

		let payload: ShapeSegmentEditResponse;
		try {
			const resp = await fetch(`/api/SegmentsApi/${segmentId}/shape-segment`);
			if (!resp.ok) {
				window.alert('Could not load this segment for editing (HTTP ' + resp.status + ').');
				return;
			}
			const body: ShapeSegmentEditResponse = await resp.json();
			payload = body;
		} catch (err) {
			console.error('load shape segment for editing failed', err);
			window.alert('Could not load this segment for editing.');
			return;
		}

		// A newer server rule version would be silently stripped to v3 on parse + re-save (parseRuleDocument
		// drops unknown geom/trackFilter types), destroying the document — refuse to edit it here (SP-06).
		if ((payload.rules.version ?? 0) > RULES_VERSION) {
			window.alert('This segment was saved by a newer version of the app and cannot be edited here — reload the page to update.');
			return;
		}

		const parsed = parseRuleDocument(payload.rules);
		const { shapes, matchMode, gridFilters, ownerUserIds, carryMembership } = parsed;
		this.store.load(shapes);
		this.carryMembership = carryMembership;

		const name = payload.segmentName ?? '';
		this.matchMode = matchMode;
		// Before setMatchMode, which decides whether the debounce row is visible at all.
		this.gateDebounceSeconds = parsed.gateDebounceSeconds;
		this.panel.setGateDebounce(this.gateDebounceSeconds);
		this.panel.setMatchMode(this.matchMode);
		// Restore the segment's saved column filters into the adopt row, frozen ("saved") — the grid's
		// current table filters are the user's browsing state and must not silently rewrite the segment.
		this.panel.setStoredGridFilters(gridFilters);
		this.panel.setName(name);
		this.panel.setEditContext(name);
		this.editingSegmentId = segmentId;
		this.originalName = name.trim();

		// Restore the owner scope into the grid owner filter so the map shows the same owners this segment
		// matches and a re-save preserves the scope. Prefer the doc's owner filter, falling back to the
		// legacy column.
		const owners = ownerUserIds ?? payload.matchOwnerIds;
		if (owners && owners.length > 0) {
			setActivitiesOwnerFilter({ in: owners, text: '' });
		} else {
			// Own-only (legacy null) or match-nobody ([]) segment: pin the grid to Me so a re-save preserves
			// the scope instead of silently widening it to the current browsing owner filter (SP-02). The
			// server resolves Me → [currentUserId], the explicit-own equivalent of legacy null.
			setActivitiesOwnerFilter({ in: [OWNER_ME_TOKEN], text: '' });
		}

		// Activate paint mode — binds the overlay, renders the loaded shapes, and the preview
		// service (subscribed to the store) refreshes the count + track highlight automatically.
		this.panel.setActive(true);
		this.setActive(true);
	}

	/** True once the overview map shell exists and its MapLibre map is live. */
	private mapReady(): boolean {
		return !!this.getShell()?.getMapLibreMap();
	}

	/** Poll until the map is ready (or give up after ~9s). */
	private waitForMap(): Promise<boolean> {
		return new Promise((resolve) => {
			let tries = 0;
			const tick = (): void => {
				if (this.mapReady()) { resolve(true); return; }
				if (tries++ > 60) { resolve(false); return; }
				window.setTimeout(tick, 150);
			};
			tick();
		});
	}

	// ── save → create or overwrite shape segment → open its statistics page ──

	private async save(): Promise<void> {
		const shapes = this.store.getAll();
		if (shapes.length === 0) return;

		// Save-time refusals show in the panel, next to the control that has to change. A modal here
		// would cover the map holding the shapes the message is about.
		this.panel.clearError();

		const name = this.panel.getName();
		if (!name) {
			this.panel.setError('Segment name cannot be empty.');
			this.panel.focusName();
			return;
		}

		// An incomplete gates config would silently fall back to whole-track (buildExtent) and lose
		// the gate roles on the next edit — refuse to save until both roles are assigned.
		if (this.matchMode === 'gates' && !hasGatesExtentConfig(shapes)) {
			this.panel.setError('"Between gates" needs at least one start gate and one finish gate — assign the roles, or switch the effort mode.');
			return;
		}

		// Clip needs a non-avoid zone; avoid zones don't widen the clip span (buildExtent) so a clip mode
		// with only avoid zones silently saves as whole-track — refuse it instead of losing the extent (SP-05).
		if (this.matchMode === 'clip' && !shapes.some((s) => s.kind === 'zone' && s.role !== 'avoid')) {
			this.panel.setError('"Clip to zones" needs at least one non-avoid zone — add one, or switch the effort mode.');
			return;
		}

		// Editing + unchanged name → overwrite that exact segment (by id). Renaming, or creating
		// fresh, → POST a new segment, leaving any original untouched.
		const overwrite = this.editingSegmentId !== null && name === this.originalName;
		const url = overwrite ? `/api/SegmentsApi/${this.editingSegmentId}/shapes` : '/api/SegmentsApi/shapes';
		const method = overwrite ? 'PUT' : 'POST';

		const body = {
			segmentName: name,
			activityType: '',
			// Match against the owners currently shown on the map (the grid owner filter); null = own-only.
			owner: getActivitiesOwnerFilter(),
			rules: buildRuleDocument(shapes, this.matchMode, {
				gridFilters: this.panel.getAdoptedGridFilters(),
				carryMembership: this.carryMembership,
				gateDebounceSeconds: this.gateDebounceSeconds,
			}),
		};

		this.panel.setSaving(true);
		try {
			const resp = await fetch(url, {
				method,
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(body),
			});
			if (!resp.ok) {
				const msg = (await resp.text().catch(() => '')).slice(0, 300);
				// Stays in the panel: the save failed, so the user is still here with their shapes.
				this.panel.setError('Failed to save segment (HTTP ' + resp.status + ')' + (msg ? ': ' + msg : '.'));
				this.panel.setSaving(false);
				return;
			}
			// SA-09 (SS-05): analysisQueued=false means the background match analysis couldn't be
			// enqueued (queue busy) — segment saved but efforts are stale/absent until re-analyzed.
			const saved: ShapeSegmentResponse = await resp.json();

			// Heatmap opt-in: attach after the shapes POST so the render job is enqueued behind the
			// match analysis (FIFO queue → efforts exist before it runs). The heatmap toast resumes
			// on the statistics page once analysis finishes and the render starts. Its enqueue failure
			// is already non-fatal, so this stays ahead of the queue-busy navigation below.
			const heatmap = this.panel.getHeatmapOptions();
			if (heatmap.enabled) await this.attachHeatmap(saved.segmentId, heatmap.types);

			if (saved.analysisQueued === false) {
				// SS-05: don't poll a job that was never enqueued (openSegmentWhenAnalyzed would spin to
				// its ~3-min safety cap). Go straight to the statistics page where Re-analyze is offered.
				window.alert('Segment saved, but match analysis could not be queued (queue busy) — use Re-analyze on the statistics page.');
				window.location.href = '/Segments/Statistics?id=' + saved.segmentId;
				return;
			}

			// The backend queues match analysis in the background, so efforts aren't ready yet.
			// Wait for it to finish before opening the statistics page — otherwise it loads empty.
			await openSegmentWhenAnalyzed(saved.segmentId);
		} catch (err) {
			console.error('save shape segment failed', err);
			this.panel.setError('Failed to save segment — check the connection and try again.');
			this.panel.setSaving(false);
		}
	}

	/** Enqueue a heatmap render on the just-saved segment; failures are non-fatal to the save flow. */
	private async attachHeatmap(segmentId: number, types: string[]): Promise<void> {
		try {
			const resp = await fetch('/api/heatmaps/' + segmentId, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ activityTypes: types }),
			});
			if (resp.ok) startHeatmapPolling();
			else console.error('attach heatmap failed (HTTP ' + resp.status + ')');
		} catch (err) {
			console.error('attach heatmap failed', err);
		}
	}
}

function bootstrap(): void {
	const host = document.getElementById('shapeFilterPanel');
	if (!host) return; // panel markup only present on the activities map page
	const filter = new ShapeFilter(host, () => getActivitiesMapShell());

	// Opened from a segment's "Edit" button → load that segment into paint mode.
	const editId = new URLSearchParams(location.search).get('editSegment');
	if (editId && /^\d+$/.test(editId)) void filter.beginEdit(Number(editId));
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', bootstrap);
} else {
	bootstrap();
}
