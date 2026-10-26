/**
 * The race replay VIEW — the field, moving, on the map, plus the charts, the results table and the
 * playback chrome.
 *
 * This is the whole rendered experience, and it lives here rather than in a page controller so that BOTH
 * pages that show a race are thin shells over it:
 *
 *   pages/race-replay.ts     the owner's /Races/Replay — this view + the edit panels (import, riders,
 *                            settings, clear-field) + the mint dialog.
 *   pages/share-race-view.ts the anonymous /s/{token} fork — this view, `editable: false`, fed from the
 *                            redacted share payload.
 *
 * That split is the point of the arrangement: an owner-side control lands only in the owner shell (the
 * fork cannot show it, so there is nothing to gate), while any improvement to the map, the charts or the
 * results table lands here and BOTH pages get it. Neither page may grow rendering logic of its own.
 *
 * Thin by design in the other direction too: everything that can be got wrong lives in tested pure modules
 * (race-build, race-field, race-markers, race-charts). This wires them to the map shell and the playback
 * shell and does nothing clever of its own.
 */

import { createMapShell } from '../core/map-shell';
import { defaultBasemapId } from '../core/basemap-registry';
import { getAccountSettings } from '../core/account-settings';
import { initMapLibreTerrain, setMaplibreStyle } from '../maplibre/maplibre-terrain';
import { createReplayPlaybackShell } from '../core/replay-playback-shell';
import { createReplayTransport } from '../core/replay-transport';
import { CameraControls } from '../maplibre/camera-controls';
import { engageFollow, releaseFollow } from '../core/replay-follow-camera';
import { setupMapToolbar } from '../core/map-toolbar';
import type { RaceCheckpointDto, RaceReplayPayload } from './race-api';
import { buildRace, type BuiltRace, type RaceTimeBasis } from './race-build';
import { createRaceMarkers, type RaceMarkers } from './race-markers';
import { calloutOffIndices, initNameToggle, labelledIndices } from './race-name-labels';
import { createTickThrottle } from '../core/tick-throttle';
import { createRaceReplayOverlay, type RaceReplayOverlay } from './race-replay-overlay';
import { buildSidebarRows } from './race-sidebar-rows';
import { createReplayChartDrawer, type ReplayChartDrawer } from '../core/replay-chart-drawer';
import { buildLegColumns, buildResultRows } from './race-results-row';
import { initRaceResultsTable, type RaceResultsTable } from './race-results-table';
import { createRaceMapDisplay, type RaceMapDisplay } from './race-map-display';
import { createRaceCharts, type RaceCharts } from './race-charts';
import { computeFieldSpeedProfile } from './race-field-speed';
import { createRaceSpeedPanel, type RaceSpeedPanel } from './race-speed-panel';
import { drawCourseLine as drawRaceCourseLine } from './race-course-line';
import { initRaceCheckpointEditor, type RaceCheckpointEditor } from './race-checkpoint-editor';

/** -1 = nobody followed. Following pins the camera to one rider and labels him. */
const NOT_FOLLOWING = -1;

// The follow camera's base framing, applied ONCE when a rider is locked on. From then on the zoom,
// bearing and pitch are MapLibre's — you pinch, twist and tilt the map while it tracks him.
const FOLLOW_ZOOM = 15;
const FOLLOW_PITCH = 30;
const FOLLOW_BEARING = 0;

/** What engageFollow frames a locked-on rider with. */
const FOLLOW_SEED = { zoom: FOLLOW_ZOOM, bearing: FOLLOW_BEARING, pitch: FOLLOW_PITCH } as const;

/**
 * The leaderboard's refresh cap.
 *
 * It was rebuilt on EVERY rAF tick: field.order() sorts 426, leaderboard() allocated a row object per
 * participant, and the whole table went back through innerHTML — measured at 0.479 ms of a 0.494 ms
 * frame (97%) and 17.7 KB of garbage per frame, i.e. 10.4 MB per 10 s of playback. Meanwhile the marker
 * layer beside it was capped at 30 Hz under a comment saying "THIS is the dial that matters".
 *
 * 5 Hz: a change of position is not perceptible faster, and every path that MUST repaint at once (seek,
 * scrub, follow-toggle, restart, completion) already routes through render(true), which forces it.
 */
const BOARD_HZ = 5;

export interface RaceReplayViewOptions {
	readonly raceId: number;
	/**
	 * May the viewer change the field or the course? Gates the checkpoint editor and the profile's
	 * click-to-place, and decides whether a PAINT is written to the DB or stays in this browser — it does
	 * not hide the paint controls, which every viewer gets (see race-results-table's PaintMode). The share
	 * fork passes false; the owner page passes its server-rendered CanEdit (owner OR a permitted
	 * collaborator).
	 */
	readonly editable: boolean;
	/**
	 * Where the replay comes from. The ONE axis the two pages differ on: the owner page reads
	 * /api/RacesApi/{id}/replay, the fork reads the redacted /s/{token}/api/race/replay. Everything
	 * downstream of the payload is identical, which is why this is a function and not a base-URL string.
	 */
	readonly loadPayload: () => Promise<RaceReplayPayload>;
}

export interface RaceReplayView {
	/** The live checkpoint list — what the owner's import panel maps split columns against. */
	checkpoints(): readonly RaceCheckpointDto[];
	/** Re-read the payload and rebuild the field in place, instead of reloading the document. */
	reload(): Promise<void>;
	/** See the note at the bottom of createRaceReplayView for what this does and does not release. */
	destroy(): void;
}

function el<T extends HTMLElement>(id: string): T | null {
	return document.getElementById(id) as T | null;
}

export function createRaceReplayView(options: RaceReplayViewOptions): RaceReplayView {
	const { raceId, editable: canEdit } = options;

	let map: maplibregl.Map | null = null;
	let payload: RaceReplayPayload | null = null;
	let race: BuiltRace | null = null;
	let markers: RaceMarkers | null = null;
	let clock = 0;
	let endSeconds = 0;
	let followIndex = NOT_FOLLOWING;
	/**
	 * Net (default) or gun. Net is the previous behaviour and the right visualization for a time trial —
	 * it shows who was FAST. Gun places every rider on the road at his real start offset, which is the
	 * only way a wave start or a staggered relay is legible.
	 *
	 * Session state, not a persisted preference: it is a way of LOOKING at one race, and a race with no
	 * offsets at all reads identically either way.
	 */
	let timeBasis: RaceTimeBasis = 'net';
	// The rider the (follow) toggle re-follows — the last one followed this session, else the leader.
	let lastFollowIdx = NOT_FOLLOWING;
	let camera: CameraControls | null = null;
	let overlay: RaceReplayOverlay | null = null;

	let table: RaceResultsTable | null = null;
	let mapDisplay: RaceMapDisplay | null = null;

	/**
	 * Rider index → the colour the user painted him. Survives a map re-init, which the marker layer does
	 * not: attachRaceLayers rebuilds it from race.meta, and race.meta holds only the BUILD-time colour.
	 */
	const painted = new Map<number, string>();

	/**
	 * Unsaved paint/emphasis, owned HERE rather than in the results table because rebuildField()
	 * destroys and re-mounts that table — a flag living inside it would come back clean while the
	 * changes were still only in the browser, and the Save button would say "Saved" over lost work.
	 */
	let paintDirty = false;
	let charts: RaceCharts | null = null;
	let speedPanel: RaceSpeedPanel | null = null;
	let checkpointEditor: RaceCheckpointEditor | null = null;

	// The leaderboard's throttle. The transport (play button, scrubber, speed stepper, clock) owns the
	// dock ids now — see the createReplayTransport call below.
	const boardThrottle = createTickThrottle(1000 / BOARD_HZ);

	const nameToggle = initNameToggle({
		buttonId: 'raceNameToggle',
		prefKey: 'race-replay',
		onChange: () => { applyLabels(); markers?.flushNow(); },
	});

	const shell = createMapShell({
		containerId: 'raceMap',
		styleSwitcher: { containerId: 'raceStyleSwitcher', btnClass: 'map-dd-item' },
		// No fullscreenBtnId: the shared mapFullscreen toggles an unstyled `.map-fullscreen` and has no close
		// button. #raceMapFullscreen is wired by the overlay controller (class on #raceHero + close + Esc/Back).
		sizeControls: {
			growBtnId: 'raceMapGrow',
			shrinkBtnId: 'raceMapShrink',
			resetBtnId: 'raceMapReset',
			defaultHeight: window.innerWidth >= 901 ? 720 : 620, // one step up on desktop — mirrors races.css
		},
		deferInit: true,
		handleBfcache: true,
		createMapLibre(containerId, styleUrl, savedCam, basemapId) {
			map = initMapLibreTerrain(containerId, undefined, 12, undefined, styleUrl, basemapId, undefined, savedCam);
			// A re-init hands back a NEW map, so the old CameraControls is bound to a dead one.
			if (map) camera = new CameraControls(map);
			return map;
		},
		onMapLibreReady() {
			// `start()` is fire-and-forget, so nothing else will ever see its rejection. Unhandled, the page
			// simply sat on "Loading the field…" forever — for a dead response, a 404, a bad anchor, anything.
			// That silence is what made the last two payload bugs a browser hand-debug: there was no failure
			// to READ. A replay that cannot load must say so.
			void start().catch(showFailure);
		},
		/**
		 * Without this the Style menu was INERT: map-shell takes its in-place branch for a live map and,
		 * with no callback to hand the new style to, returns having updated nothing but its own bookkeeping.
		 * The toolbar label still changed, so it looked like it had worked.
		 */
		switchMapLibreStyle(m, styleUrl, basemapId) {
			markers = null;   // its sources die with the old style; render() no-ops until the re-attach
			void setMaplibreStyle(styleUrl, basemapId);
			// style.load, never isStyleLoaded(): that lies while tiles/DEM are still in flight, and gating
			// a deferred addSource on it strands the flush (it has bitten this codebase twice).
			m.once('style.load', () => {
				if (race) attachRaceLayers();
				render(true);
			});
		},
		/**
		 * The map is about to be destroyed and re-created. Stop the loop FIRST: a running rAF would keep
		 * calling render() → markers.flush() → getSource() on the removed map, and the throw escapes the
		 * rAF callback — so the loop never reschedules while isPlaying stays true, resume() early-returns,
		 * and the Play button is wedged for good.
		 */
		onBfcacheRestore() {
			if (playback.isPlaying()) transport.setPlayIcon(playback.togglePlayPause());
			playback.stopIdleCamera();
			markers?.destroy();
			markers = null;
		},
		destroyMapLibre() {
			// The layers drop with the map on EVERY teardown path — style switch, bfcache, destroy.
			markers?.destroy();
			markers = null;
			camera?.deactivate();
			camera = null;
		},
	});

	const playback = createReplayPlaybackShell({
		tick(dtMs, speed) {
			clock += (dtMs / 1000) * speed;
			if (clock >= endSeconds) {
				clock = endSeconds;
				render(true);
				return true;
			}
			render(false);
			return false;
		},
		isAtEnd: () => clock >= endSeconds,
		restart() {
			clock = 0;
			render(true);
		},
		// Paused + following → keep tracking him, so the user can still orbit and zoom around a
		// stationary rider. That is what the idle loop is for.
		wantsIdleLoop: () => followIndex !== NOT_FOLLOWING,
		idleCenter: () => followCenter(),
		getCameraControls: () => camera,
		onComplete() {
			transport.setPlayIcon(false);
		},
		idleOnComplete: false,
	});

	// The transport: play/pause, the scrubber (rAF-coalesced, write-back-guarded against the user's thumb),
	// the speed −/+ stepper and the elapsed clock. A race scrub seeks by fraction of the total duration.
	const transport = createReplayTransport(
		{
			playPauseId: 'raceReplayPlayPause',
			seekId: 'raceReplaySeek',
			currentTimeId: 'raceReplayCurrentTime',
			speedSelectId: 'raceReplaySpeed',
			speedSlowerId: 'raceReplaySpeedSlower',
			speedFasterId: 'raceReplaySpeedFaster',
			speedLabelId: 'raceReplaySpeedLabel',
			speedPrefKey: 'race',
		},
		{
			seekToPercent: (percent) => { clock = (percent / 100) * endSeconds; render(true); },
			togglePlayPause: () => playback.togglePlayPause(),
			setSpeed: (mult) => playback.setSpeed(mult),
		},
	);

	async function start(): Promise<void> {
		// The shell re-runs this on every map re-init (a bfcache restore hands back a NEW map). The field
		// is map-INDEPENDENT by construction — that is the point of race-field being pure — so only the
		// map-bound half is redone. Without the guard a back-navigation re-fetched the whole payload over
		// the network and re-ran the pacing model for 424 riders, reallocating a ~7 MB RaceField.
		if (payload && race) {
			attachRaceLayers();
			render(true);
			return;
		}

		payload = await options.loadPayload();

		// buildRace's default colour already honours a painted rider (race-build.raceColorOf), so a group
		// painted in an earlier session comes back coloured with no work here.
		race = buildRace(payload, { timeBasis });
		endSeconds = race.field.endSeconds;

		mountCharts();
		mountResultsTable();
		wireTimeBasisToggle();

		// Only on the FIRST init: on a re-init the shell restores the camera the user left, and on a style
		// switch fitting would snap his viewport back to the course bounds mid-replay.
		fitToCourse();
		hideLoading();
		if (race.field.size === 0) {
			// A race with no field yet still deserves its course drawn — it is the thing you just created.
			drawCourseLine();
			return;
		}

		// The ladder is scaled to THIS race's duration, so the default option watches the whole thing in
		// about two minutes — a fixed 120x ladder would be nonsense for a crit and an ultra alike. Same
		// ladder and stepper as the segment replay (core/speed-ladder, via the transport).
		transport.buildLadder(endSeconds);
		race.field.advanceTo(0);
		attachRaceLayers();
		render(true);
	}

	/**
	 * The profile strip and the checkpoint list. They are two views of one thing: the profile owns
	 * PLACEMENT (its x-axis is chainage, so a click there is unambiguous — see race-profile.ts) and the
	 * list owns precision (a typed chainage; a pixel on a 142 km course is ~150 m).
	 *
	 * The editor half is mount-gated, not flag-gated: the share fork simply does not render #raceCheckpoints,
	 * so the fork needs no "if share" anywhere and the owner page needs no extra wiring.
	 */
	function mountCharts(): void {
		const profileCanvas = el<HTMLCanvasElement>('raceProfile');
		const editorMount = el('raceCheckpoints');
		if (!profileCanvas || !payload) return;

		checkpointEditor = editorMount
			? initRaceCheckpointEditor({
				raceId,
				mount: editorMount,
				editable: canEdit,
				courseTotalMeters: payload.course.totalMeters,
				// A staged (unapplied) edit: preview it on the profile + course line, but do NOT re-pace the
				// 424-rider field — that waits for Apply.
				// Both surfaces draw the same course, so both hear every edit — the strip is the fullscreen
				// one, the speed panel the inline one, and they must never show different sections.
				onPreview: (checkpoints) => {
					setCheckpointsEverywhere(checkpoints);
					drawCourseLine(true);   // the legs are the course's colours — redraw it
				},
				onChanged: (checkpoints) => {
					if (payload) payload.checkpoints = checkpoints;
					setCheckpointsEverywhere(checkpoints);
					drawCourseLine(true);
					// The checkpoints ARE the pacing anchors (race-build's nodeOf), and they are the leg
					// columns' keys. Re-pace the field and rebuild the results grid on Apply only.
					rebuildField();
				},
				onArmPlacement: (armed) => {
					charts?.setPlaceable(armed);
					speedPanel?.setPlaceable(armed);
				},
				onSelect: (id) => {
					charts?.setSelected(id);
					speedPanel?.setSelected(id);
				},
			})
			: null;

		// One set of callbacks for both surfaces: a click on either places through the same editor.
		const placement = {
			onPlace: (metres: number) => void checkpointEditor?.place(metres),
			onMove: (id: number, metres: number) => void checkpointEditor?.move(id, metres),
			onSelect: (id: number) => checkpointEditor?.select(id),
		};

		charts = createRaceCharts({
			profileCanvas,
			course: payload.course,
			editable: canEdit,
			map: () => map,
			...placement,
		});

		// Absent by MOUNT, like the checkpoint editor: a page that renders no #raceSpeedChart gets null.
		// It carries the course too — terrain, section tints and the rules you click to split a section —
		// because the strip above is fullscreen-only now (race-speed-backdrop.ts).
		speedPanel = createRaceSpeedPanel({
			course: payload.course,
			checkpoints: payload.checkpoints,
			editable: canEdit,
			onFieldWalked: () => render(true),
			// Hovering the chart drops the dot on the course. The strip owns that link (its axis has
			// always been chainage) and keeps owning it — this is the door in from the inline surface.
			onHover: (metres) => charts?.hoverAt(metres),
			...placement,
		});

		checkpointEditor?.setCheckpoints(payload.checkpoints);
		setCheckpointsEverywhere(payload.checkpoints);

		// The field rides the profile too. The arrays are the field's OWN — read live every frame, never
		// copied, so animating 508 riders on the strip costs one blit and one path per colour.
		if (race && race.field.size > 0) {
			charts.setField({
				chainages: race.field.chainages,
				finished: race.field.finished,
				colors: race.meta.map((m) => m.color),
			});
			charts.setEmphasis(emphasisSet());
			updateSpeedProfile();
		}
	}

	/** The course's sections, to every surface that draws them. Two callers, one truth. */
	function setCheckpointsEverywhere(checkpoints: readonly RaceCheckpointDto[]): void {
		charts?.setCheckpoints(checkpoints);
		speedPanel?.setCheckpoints(checkpoints);
	}

	/**
	 * The emphasised riders, as FIELD indices.
	 *
	 * DERIVED from the participants rather than kept in a parallel map the way `painted` is: onEmphasised
	 * writes the flag back onto the participant object the payload owns, so a rebuild reads it for free
	 * and there is no second copy to fall out of step with the first.
	 */
	function emphasisSet(): Set<number> {
		const set = new Set<number>();
		race?.participants.forEach((p, i) => { if (p.emphasised === true) set.add(i); });
		return set;
	}

	/** Re-derive who is named and hand it to the markers. A UI tick, not a frame — see race-name-labels. */
	function applyLabels(): void {
		if (!race || !markers) return;
		markers.setLabelled(labelledIndices(
			race.meta.map((m) => m.isReal),
			emphasisSet(),
			followIndex === NOT_FOLLOWING ? null : followIndex,
			nameToggle?.namesOn ?? true,
			calloutOffIndices(race.participants),
		));
	}

	/** (Re)compute both speed views — the strip's field aggregate and the panel's per-rider lines. WALKS the
	 *  field, so a render() must follow to restore its cursor to the live clock (every caller ends in
	 *  render(true); the panel asks for its own via onFieldWalked when it is reached from elsewhere). */
	function updateSpeedProfile(): void {
		if (!race || !charts || !payload || race.field.size === 0) return;
		// One aggregate, both surfaces: the strip traces it over the terrain, the panel draws it as a
		// median inside its p10–p90 spread on the riders' own km/h axis.
		const profile = computeFieldSpeedProfile(race.field, payload.course.totalMeters);
		charts.setSpeedProfile(profile);
		speedPanel?.setFieldProfile(profile);
		speedPanel?.update(race, payload.course.totalMeters);
	}

	/**
	 * Re-pace the field after a checkpoint moved.
	 *
	 * A checkpoint is not decoration: its node is an ANCHOR the pacing model hits exactly, and its id keys
	 * a leg column. So dragging the ski→run boundary changes when every simulated rider changes pace, and
	 * splitting a section adds a column. Everything downstream of the anchors is rebuilt from the payload
	 * we already hold — no re-fetch: the checkpoints came back from the server on the write.
	 *
	 * The old results table and display controller are destroyed first; mountResultsTable creates a new
	 * mapDisplay each time, and the old one owns a coalescing timer.
	 */
	function rebuildField(): void {
		if (!payload) return;

		// Index-keyed viewer state must cross the rebuild by IDENTITY, not by index: buildRace compacts
		// (a rider it cannot place is skipped) and the checkpoint set decides who is placeable, so a
		// checkpoint edit can shift every index — follow landed on the wrong rider, paint on the wrong
		// group. Capture participant ids against the OLD race before it is replaced.
		const followedId = race && followIndex !== NOT_FOLLOWING
			? (race.participants[followIndex]?.participantId ?? null)
			: null;
		const paintedById = new Map<number, string>();
		if (race) {
			for (const [i, color] of painted) {
				const id = race.participants[i]?.participantId;
				if (id != null) paintedById.set(id, color);
			}
		}

		race = buildRace(payload, { timeBasis });
		endSeconds = race.field.endSeconds;

		// Remap into the new field; a rider this build dropped simply loses his state.
		painted.clear();
		for (const [id, color] of paintedById) {
			const i = race.indexByParticipantId.get(id);
			if (i !== undefined) painted.set(i, color);
		}
		followIndex = followedId != null
			? (race.indexByParticipantId.get(followedId) ?? NOT_FOLLOWING)
			: NOT_FOLLOWING;

		table?.destroy();
		table = null;
		mapDisplay?.destroy();
		mapDisplay = null;

		mountResultsTable();

		if (race.field.size > 0) {
			charts?.setField({
				chainages: race.field.chainages,
				finished: race.field.finished,
				colors: race.meta.map((m) => m.color),
			});
			charts?.setEmphasis(emphasisSet());
			updateSpeedProfile();
		}

		// The markers hold the OLD field's arrays — they must be rebuilt against the new one.
		attachRaceLayers();
		render(true);
	}

	function mountResultsTable(): void {
		const mount = el('raceResultsGrid');
		if (!mount || !payload || !race || race.field.size === 0) return;

		// race.roster, NOT payload.participants: it carries the field index each entrant ended up with,
		// null for one the field never had (a DNS). A payload index here would masquerade as a field index
		// the moment anyone is dropped, and every row from there on addressed the rider in front.
		const rows = buildResultRows(race.roster, payload.checkpoints);
		const legs = buildLegColumns(payload.checkpoints);

		// The map and the profile are two views of ONE field, so the filter drives both. Splitting them
		// here (rather than teaching the display controller about the profile) keeps that controller a
		// pure map concern — the profile just mirrors whatever the markers were told.
		mapDisplay = createRaceMapDisplay({
			// `indices` is the MATCHED set in both calls — the riders to keep. The profile used to read it
			// as the set to DIM, so the same array faded the map and the strip in opposite directions.
			setDimmed: (indices) => {
				markers?.setDimmed(indices);
				charts?.setFilter({ bright: indices == null ? null : new Set(indices) });
			},
			setVisible: (indices) => {
				markers?.setVisible(indices);
				charts?.setFilter({ visible: indices == null ? null : new Set(indices) });
			},
			flushNow: () => {
				markers?.flushNow();
				charts?.tick(true);
			},
		});

		table = initRaceResultsTable({
			raceId,
			mount,
			toolbarMount: el('raceResultsToolbar') ?? undefined,
			rows,
			legs,
			// A viewer paints too — the field is unreadable without it. Only the SAVE is owner-side.
			paintMode: canEdit ? 'staged' : 'local',
			dirty: paintDirty,
			onDirtyChange: (value) => { paintDirty = value; },
			onFiltered: (indices, filtering) => mapDisplay?.onFiltered(indices, filtering),
			onRowClick: (index) => toggleFollow(index),
			onPainted: (colors) => {
				// Remembered, because the marker layer is rebuilt from race.meta on every style switch and
				// bfcache restore and race.meta is frozen at build time. This map is what makes a paint
				// survive that.
				for (const [index, color] of colors) {
					painted.set(index, color);
					// RF-02: write the paint back onto the participant object buildRace reads
					// (race.participants[index] is the SAME reference as the payload entry, via race-build's
					// kept.push). A checkpoint Apply rebuilds the field from the payload, so without this the
					// charts reverted to the build-time default while the markers kept the paint — the two
					// views diverged. Writing it back carries the paint into BOTH views (and the results-table
					// swatch) on every rebuild.
					const p = race?.participants[index];
					if (p) p.color = color;
				}
				markers?.setColors(colors);
				markers?.flushNow();
				charts?.setColors(colors);
			},
			onEmphasised: (flags) => {
				// Written back onto the participant the payload owns, same seam as the paint above (RF-02).
				// That single write is what carries emphasis through a checkpoint Apply, a style switch and
				// a bfcache restore — emphasisSet() and race-build both read it back off this object.
				for (const [index, on] of flags) {
					const p = race?.participants[index];
					if (p) p.emphasised = on;
				}
				markers?.setEmphasis(flags);
				applyLabels();   // an emphasised rider gets his name too — that is half of "watch THESE"
				markers?.flushNow();
				charts?.setEmphasis(emphasisSet());
				updateSpeedProfile();   // emphasis decides who the speed panel draws — see race-speed-panel
			},
			onModeChange: (mode) => mapDisplay?.setMode(mode),
		});
	}

	/**
	 * (Re-)attach everything the race owns on the map. Safe to call twice: a style switch and a bfcache
	 * re-init both destroy the sources underneath us, and both come back through here.
	 */
	function attachRaceLayers(): void {
		if (!map || !race) return;

		// The map hover (map→chart) listener binds to whichever map object is live — re-bound here because a
		// style switch / bfcache restore hands back a NEW map.
		charts?.attachMap();
		drawCourseLine();
		if (race.field.size === 0) return;

		markers?.destroy();
		markers = createRaceMarkers(map, race.field, race.meta);

		// A rebuilt layer knows nothing the user did to the last one. race.meta carries only the
		// BUILD-time colour, so without this every painted rider reverted to the default on a style
		// switch or a Back-navigation — while the profile, which is NOT rebuilt, kept the paint. The two
		// views then disagreed, and the database went on holding colours the map denied.
		if (painted.size > 0) markers.setColors(painted);

		// Emphasis has the same staleness problem for the same reason — race.meta froze it at build time,
		// and a rider emphasised since then would come back the size of the field. Pushed as FULL state
		// (every index, on or off), not a delta: the fresh layer knows nothing, so "unchanged" is not a
		// safe answer for anyone.
		markers.setEmphasis(new Map(race.participants.map((p, i) => [i, p.emphasised === true])));

		// A followed rider keeps his halo and his camera across the re-attach; his LABEL comes back with
		// everyone else's in applyLabels() below.
		if (followIndex !== NOT_FOLLOWING) {
			markers.setHighlight(followIndex, true);

			// ...and the CAMERA has to be re-engaged, not just the halo. destroyMapLibre nulls `camera`, and
			// the re-init builds a fresh CameraControls with isActive = false — but followIndex is closure
			// state that survives. So after a Back-navigation the rider stayed haloed, labelled and flagged
			// as followed in the leaderboard, while updateFollowCamera() early-returned on !camera.isActive
			// and the camera never tracked him again. Clicking his row to fix it only UNfollowed him.
			if (map && camera) engageFollow(map, camera, FOLLOW_SEED);
			playback.syncIdleCamera();
		}
		applyLabels();

		// ...and the same for Highlight/Hide: the controller owns that state, so it re-asserts it here.
		// It ends in a flushNow of its own, which is why there is not a second one after this.
		mapDisplay?.reapply();
		markers.flushNow();
	}

	function hideLoading(): void {
		const loading = el('raceLoading');
		if (loading) loading.hidden = true;
	}

	/**
	 * The replay could not load. Say what happened, in the place the user is already looking.
	 *
	 * The message is the error's own — the fetch helpers distinguish a missing race from a server that
	 * began replying and then died mid-body, and that distinction is the whole point: one is the user's
	 * problem, the other is ours.
	 */
	function showFailure(error: unknown): void {
		hideLoading();
		// Logged unconditionally. If the pane is missing the message would otherwise vanish completely —
		// which is the exact silence this function exists to end, reintroduced by a markup change.
		console.error('[race-replay] the replay could not load', error);

		const pane = el('raceError');
		if (!pane) return;
		pane.textContent = error instanceof Error ? error.message : String(error);
		pane.hidden = false;
	}

	function fitToCourse(): void {
		if (!map || !payload) return;
		const { lat, lng } = payload.course;
		let west = 180, east = -180, south = 90, north = -90;
		for (let i = 0; i < lat.length; i++) {
			const x = lng[i] ?? NaN, y = lat[i] ?? NaN;
			if (x < west) west = x;
			if (x > east) east = x;
			if (y < south) south = y;
			if (y > north) north = y;
		}
		map.fitBounds([[west, south], [east, north]], { padding: 40, duration: 0 });
	}

	// The course line is a self-contained map concern — see race-course-line.ts. This just supplies the
	// live map + payload; a checkpoint edit passes redraw=true so the recoloured legs follow.
	function drawCourseLine(redraw = false): void {
		if (map && payload) drawRaceCourseLine(map, payload.course, payload.checkpoints, redraw);
	}

	function render(force: boolean): void {
		if (!race || !markers) return;
		race.field.advanceTo(clock);
		if (force) markers.flushNow();
		else markers.flush();

		updateFollowCamera();

		// The clock and the scrub head keep updating every frame — they are cheap, and the user watches
		// them. The transport writes both (and guards the scrubber against the user's own drag). Only the
		// board is throttled.
		transport.render(endSeconds > 0 ? (clock / endSeconds) * 100 : 0, clock);

		// The field rides the elevation strip too, and the charts' playhead follows whoever the viewer is
		// WATCHING: the leader normally, the followed rider while one is followed — with the leader kept on
		// as a second golden rule, so the gap between the two lines is the gap on the road. The charts
		// throttle themselves to 30 Hz (as the markers do), so this is a call, not a cost. leadChainage() is
		// O(P) with no alloc, NOT a full order() sort every frame (RF-01): the throttled sidebar already owns
		// the one legitimate per-tick sort.
		if (charts && race.field.size > 0) {
			const lead = race.field.leadChainage();
			const followed = followIndex !== NOT_FOLLOWING && followIndex < race.field.size
				? race.field.chainages[followIndex]
				: null;
			charts.setPlayhead(followed ?? lead);
			charts.setLeadLine(followed == null ? null : lead);
			charts.tick(force);
			// The same two rules on the inline chart. Per-frame safe: it repaints one transparent overlay
			// canvas, never the dozen rider lines underneath (core/chart-overlay.setOverlayPlayhead).
			speedPanel?.setPlayhead(followed ?? lead, followed == null ? null : lead);
		}

		renderSidebar(force);
	}

	/** The follow sidebar — the WHOLE field in true race-position order; the overlay virtualizes it (only the
	 *  visible rows reach the DOM) and pins the real riders above it, so 500 riders scroll cheaply. */
	/**
	 * The gun/net toggle. Only rendered when the field HAS offsets — on a mass start the two bases are the
	 * same picture, and a control that changes nothing is worse than no control.
	 */
	function wireTimeBasisToggle(): void {
		const toggle = el<HTMLInputElement>('raceGunTime');
		const wrapper = el('raceGunTimeControl');
		const staggered = (payload?.participants ?? []).some(
			(p) => p.startOffsetSeconds != null && p.startOffsetSeconds > 0);
		if (wrapper) wrapper.hidden = !staggered;
		if (!toggle || toggle.dataset['wired'] === '1') return;

		toggle.dataset['wired'] = '1';
		toggle.addEventListener('change', () => {
			timeBasis = toggle.checked ? 'gun' : 'net';
			// A full field rebuild, because the basis changes every rider's arrival array. rebuildField
			// already carries follow/paint across by identity, which is exactly what is needed here.
			rebuildField();
		});
	}

	function renderSidebar(force: boolean): void {
		if (!race) return;
		if (!boardThrottle.shouldRun(force)) return;
		overlay?.renderSidebar(buildSidebarRows(race.field, race.participants, race.statuses, race.dnfStandings), followIndex);
	}

	function followCenter(): maplibregl.LngLatLike {
		// Bounds check like render()'s: an out-of-range index would write a NaN centre every frame.
		if (!race || followIndex === NOT_FOLLOWING || followIndex >= race.field.size)
			return map ? map.getCenter() : [0, 0];
		return [race.field.lngs[followIndex] ?? NaN, race.field.lats[followIndex] ?? NaN];
	}

	/**
	 * Move the camera to the followed rider — through the shell, which writes the CENTRE and nothing
	 * else.
	 *
	 * Writing the whole camera every frame was the original bug: the user's own zoom/pan was wiped
	 * before it could take effect and the map felt dead. Only the centre is ours now; zoom, bearing
	 * and pitch stay whatever the user's gestures left them at (core/replay-follow-camera.ts).
	 */
	function updateFollowCamera(): void {
		if (!map || !race || followIndex === NOT_FOLLOWING) return;
		if (!camera || !camera.isActive) return;

		playback.applyCamera(map, { center: followCenter() as [number, number] });
	}

	/** Follow a rider (or stop). The board is rebuilt every frame, so the click is delegated. */
	function toggleFollow(index: number): void {
		if (!race || !markers) return;

		if (followIndex !== NOT_FOLLOWING) markers.setHighlight(followIndex, false);
		followIndex = followIndex === index ? NOT_FOLLOWING : index;
		// Remembered so the sidebar's (follow) toggle can re-follow him after an unfollow.
		if (followIndex !== NOT_FOLLOWING) lastFollowIdx = followIndex;

		if (followIndex !== NOT_FOLLOWING) markers.setHighlight(followIndex, true);
		applyLabels();
		markers.flushNow();

		// Following → hide the mouse-hover course dot (unwanted over the locked-on rider); free look restores it.
		charts?.setMapMarkerSuppressed(followIndex !== NOT_FOLLOWING);

		// The shared follow seam (core/replay-follow-camera) owns the canvas handover. Locking on frames
		// him with FOLLOW_SEED once; after that the camera is his centre plus whatever the user does.
		if (followIndex === NOT_FOLLOWING) {
			if (map) releaseFollow(map, camera);
			playback.stopIdleCamera();
		} else {
			if (map && camera) engageFollow(map, camera, FOLLOW_SEED);
			playback.syncIdleCamera();   // paused + follow → track him right away
		}
		overlay?.syncFollowToggle(followIndex !== NOT_FOLLOWING);
		render(true);
	}

	/** The sidebar header's (follow)/(unfollow) toggle: unfollow, or re-follow the last rider / the leader. */
	function followToggleAction(): void {
		if (followIndex !== NOT_FOLLOWING) { toggleFollow(followIndex); return; }
		if (!race || race.field.size === 0) return;
		const target = lastFollowIdx !== NOT_FOLLOWING ? lastFollowIdx : (race.field.order()[0] ?? NOT_FOLLOWING);
		if (target !== NOT_FOLLOWING) toggleFollow(target);
	}

	/**
	 * The chart strip pulls up and pushes down. Mounted here rather than in either page, so the owner page
	 * and the share fork get the same drawer from the same partial (_RaceHero.cshtml).
	 *
	 * Mount-gated like the checkpoint editor: no handle in the markup, no drawer — which is what keeps the
	 * page-level tests (whose DOM predates it) honest instead of stubbed.
	 */
	function mountChartDrawer(): ReplayChartDrawer | null {
		const host = el('raceChartsDrawer');
		const content = el('raceCharts');
		const handle = el('raceChartsHandle');
		if (!host || !content || !handle) return null;

		return createReplayChartDrawer({
			hostEl: host,
			contentEl: content,
			handleEl: handle,
			storageKey: 'raceChartsDrawer',
			// The profile fills the drawer now, so this is only what a viewer who never dragged it gets.
			defaultHeightPx: 150,
			minHeightPx: 90,
			onResize() {
				// The map grew or shrank with the drawer; the canvas re-fits itself via ResizeObserver,
				// but it must repaint at once rather than on the next frame the clock happens to run.
				map?.resize();
				charts?.tick(true);
			},
		});
	}

	const chartDrawer = mountChartDrawer();

	// The chrome: manual fullscreen (+ close, Esc, Back) and the follow sidebar. Row clicks and the
	// (follow) toggle come back here; playback and follow state stay owned here.
	overlay = createRaceReplayOverlay({
		onFollow: (index) => toggleFollow(index),
		onFollowToggle: () => followToggleAction(),
		// A fullscreen enter/exit is a big size change; MapLibre trackResize covers it, but nudge it too.
		// The strip only EXISTS in fullscreen (race-replay.css), so entering reveals a canvas that has
		// been sized to a display:none box — it must repaint, not wait for the next clock tick.
		onResize: () => {
			const m = map;
			if (m) window.setTimeout(() => m.resize(), 60);
			window.setTimeout(() => charts?.tick(true), 60);
		},
	});

	// The scrubber (rAF-coalesced), the play button and the speed <select> are all wired by the transport
	// created above — a race scrub is the worst in the app (each seek rewinds every rider's cursor and
	// rescans), so the coalescing that lives there matters most here.

	// Its teardown releases a document click listener and the dropdown MutationObservers. It used to be
	// dropped on the floor.
	const teardownToolbar = setupMapToolbar();

	// Honour the profile's default basemap ("Basemap used across all maps"), like every other map page —
	// falling back to Liberty when it can't resolve (e.g. a key-gated style with no MapTiler key). From the
	// seam, so both pages that mount this view read one island: the owner's saved choice on /Races/Replay,
	// global defaults on /s/{token} (decision 10) — where there is no key at all, so always keyless.
	const basemap = defaultBasemapId(getAccountSettings().map.defaultStyle);
	shell.switchToBasemap(basemap);
	shell.setActiveBasemap(basemap);

	return {
		checkpoints: () => payload?.checkpoints ?? [],

		/**
		 * Re-read the payload and rebuild the field in place — for an edit that changes what the replay
		 * SHOWS without changing what the page IS. A rider's name is on the map label, the sidebar, the
		 * results board and the speed legend, all of them built from the payload.
		 *
		 * The alternative was window.location.reload(), which is what the field-wholesale panels still do.
		 * It stops being acceptable the moment a panel holds unsaved text: the riders panel is a column of
		 * editable name boxes, and reloading the document to save one of them discards what the user typed
		 * into the others.
		 */
		async reload(): Promise<void> {
			payload = await options.loadPayload();
			rebuildField();
		},

		// The listeners bound above are page-lifetime one-shots — bound once, they die with the document,
		// and inventing a lifecycle for them would be ceremony. What genuinely needs releasing is the
		// toolbar's observers, the map, and the marker layers.
		destroy(): void {
			teardownToolbar();

			// STOP THE CLOCK FIRST. The rAF loop reschedules itself while isPlaying stays true, so tearing the
			// page down mid-playback left it running — driving a dead map, and holding the whole BuiltRace
			// (~7 MB for a 508-team field) alive through the closure for as long as the loop lived. Nothing
			// else here can release that while the loop still references it. destroy() stops the frame and
			// the idle loop without pause()'s idle-restart.
			playback.destroy();
			// The transport owns the coalesced-seek frame; its destroy() cancels it, so a scrub queued into
			// a frame that never arrives cannot render into the torn-down map.
			transport.destroy();

			markers?.destroy();
			markers = null;
			// Each of these holds an observer, a timer or listeners that outlive the document node.
			chartDrawer?.destroy();
			overlay?.destroy();
			mapDisplay?.destroy();
			charts?.destroy();
			speedPanel?.destroy();
			checkpointEditor?.destroy();
			table?.destroy();
			nameToggle.destroy();
			shell.destroy();
		},
	};
}
