/**
 * Segment Statistics - Replay UI Controller
 *
 * Manages the replay fullscreen overlay and controls.
 * Depends on: ctx (segment-statistics.ts)
 *             replayApi (segment-stats-replay.ts)
 *             maplibreApi (segment-stats-map-maplibre.ts)
 */
import { setData } from '../core/data-attrs';
import type { ReplayEffort, ReplayMode } from './segment-stats-replay-types';
import { ctx } from './segment-statistics';
import { getMaplibreMap } from '../core/map-state';
import { replayApi } from './segment-stats-replay';
import { loadFullResEfforts, buildReplayPerfData } from './segment-stats-replay-data';
import { maplibreApi } from './segment-stats-map-maplibre';
import { buildEffortLiveModel, computeEffortLiveRow, type EffortLiveModel } from './segment-stats-replay-live-data';
import { robustXMax } from '../core/chart-robust-scale';
import { formatTime } from '../core/format';
import { renderFollowToggleLabel } from '../core/map-utils-shared';
import { createReplayTransport, type ReplayTransport } from '../core/replay-transport';
import { createReplayHistorySession } from '../core/replay-history-session';
import { createTrailingThrottle } from '../core/trailing-throttle';
import { getSegmentReplayActive } from './seg-stats-shared';
import { escapeHtml } from '../core/html';
import { createEffortListExpand, type EffortListExpand } from './effort-list-expand';
import { createHoldOpenPanel, type HoldOpenPanel } from '../core/panel-hold-open';
import { createSegmentReplayProfile, type SegmentReplayProfile } from './segment-replay-profile';
import { createPendingActionGate } from '../core/pending-action-gate';
import { must } from '../core/invariant';
if (!ctx) { throw new Error("segment-stats-replay-ui: no ctx"); }

	// Shared history session (CR1-05): dummy-entry push, back-button + Esc exits.
	// (The old local back-button handler's `replayApi &&` check was dead — a module
	// const, always truthy — and is dropped here.)
	const session = createReplayHistorySession({
		stateKey: "segReplayActive",
		isActive: getSegmentReplayActive,
		exit: () => ctx.stopReplay(),
	});

	// DOM elements. The transport (play/pause, scrubber, speed stepper, elapsed clock) owns the dock ids —
	// created once in _showOverlay, since the overlay's controls are static and reused across sessions.
	let overlay: HTMLElement | null = null;
	let modeBtn: HTMLElement | null = null;
	let transport: ReplayTransport | null = null;

	// Replay sync domain mirrored from the engine so the scrubber labels format correctly.
	let _mode: ReplayMode = "time";

	// Aborts the in-flight full-res effort load (PS-03) when replay is exited or restarted.
	let _fullResAbort: AbortController | null = null;

	// Last effort the camera followed this session — the header's "(follow)"
	// re-follows it; falls back to the leader when nothing was followed yet.
	let _lastFollowIdx = -1;

	// State
	// (control listeners are bound per-element via _bindOnce, so no session flag)

	// ================================================================
	// PUBLIC API - overwrite the stubs declared in segment-statistics.js
	// ================================================================

	// RS2-03: performanceData is a dense placeholder array from the moment the chunked load begins,
	// so gate on the completion flag — otherwise replay starts on empty/partial geometry mid-load.
	function _replayDataReady(): boolean {
		return !!getMaplibreMap() && !!ctx.performanceData && !!ctx.performanceDataLoaded;
	}

	// Clicking Replay mid-load defers the start instead of alert()ing (which froze the very load it was
	// waiting for). The gate spins the button and calls back when the data lands.
	const startGate = createPendingActionGate({
		isReady: _replayDataReady,
		onReady: () => _beginReplay(),
		setPending: _setReplayBtnPending,
		onTimeout: () => alert("Map and performance data are still not loaded — reload the page to retry."),
	});

	ctx.startReplay = (): void => {
		// RP-01 twin (RS2-05): re-entrancy guard — a keyboard double-activation (double Enter/Space)
		// dispatches both clicks to the button; without this the second run rebuilds the engine and
		// pushes a duplicate history entry. The flag is set synchronously by the first run's replayApi.start().
		if (getSegmentReplayActive()) return;
		if (!startGate.request()) return;
		_beginReplay();
	};

	function _beginReplay(): void {
		const visibleIndices = ctx.getVisibleEffortIndices();
		// Nothing ticked: the button is disabled for exactly this reason (_syncReplayAvailability), so
		// this only fires if the selection emptied while a deferred start was waiting. Re-sync and stop
		// — the disabled button and its tooltip say why, where the fix is, without a modal over the
		// efforts table the user has to reach.
		if (visibleIndices.length === 0) {
			ctx._syncReplayAvailability?.();
			return;
		}

		document.body.classList.add('replay-active');
		_initReplay(visibleIndices);
	}

	// Nothing to race → the Replay button is off, with the reason in its tooltip. Re-run whenever the
	// visible-effort set changes (updateVisibleEfforts calls it through the ctx seam).
	ctx._syncReplayAvailability = (): void => {
		const btn = document.getElementById("segReplayBtn") as HTMLButtonElement | null;
		if (!btn || startGate.isPending()) return; // the waiting state owns the button while it spins
		const none = ctx.getVisibleEffortIndices().length === 0;
		btn.disabled = none;
		btn.title = none
			? "Tick at least one effort in the table below to replay"
			: "Race replay - compare efforts on the map";
	};
	ctx._syncReplayAvailability();

	// Spinner in / play icon out on the toolbar's Replay button while a start is deferred.
	// Class + icon swap only — the button's markup (Pages/Segments/_SegmentStatsMapPanel.cshtml) stays put.
	function _setReplayBtnPending(pending: boolean): void {
		const btn = document.getElementById("segReplayBtn");
		if (!btn) return;
		btn.classList.toggle("is-waiting", pending);
		btn.setAttribute("aria-busy", pending ? "true" : "false");
		const icon = btn.querySelector("i");
		if (icon) icon.className = pending ? "bi bi-arrow-repeat" : "bi bi-play-circle";
		const label = btn.querySelector(".seg-replay-label");
		if (label) label.textContent = pending ? "Loading…" : "Replay";
		btn.title = pending
			? "Waiting for map + effort data — replay starts by itself (click to cancel)"
			: "Race replay - compare efforts on the map";
	}

	ctx.stopReplay = (): void => {
		startGate.cancel();
		document.body.classList.remove('replay-active');
		// Cancel any in-flight full-res effort load (PS-03) — its cache survives for the next replay.
		if (_fullResAbort) { _fullResAbort.abort(); _fullResAbort = null; }
		if (replayApi) {
			replayApi.destroy();
		}
		_hideOverlay();

		// Restore static map layers that were hidden when replay started
		if (typeof maplibreApi.showStaticLayers === "function") {
			maplibreApi.showStaticLayers();
		}

		// Remove the dummy history entry that was pushed when replay started
		// (guarded — a back-button exit already moved us back).
		session.leave();
	};

	// ================================================================
	// HELPER FUNCTIONS (declared first)
	// ================================================================

	// Bind a handler to an element at most once. The replay controls are static
	// elements reused across replay sessions, so binding per session would stack
	// duplicate listeners (e.g. a doubly-bound play/pause toggles twice → no-op).
	// A per-element flag binds once, yet still binds a fresh element if one is
	// ever recreated (e.g. on a map-style switch).
	function _bindOnce(
		el: (HTMLElement & { _replayBound?: boolean }) | null,
		type: string,
		handler: EventListener,
	): void {
		if (!el || el._replayBound) return;
		el._replayBound = true;
		el.addEventListener(type, handler);
	}

	function _bindControls(): void {
		// Play/pause, the speed <select> and the rAF-coalesced scrubber are all wired by the transport
		// (created once in _showOverlay). Only the segment-specific controls stay here.
		_bindOnce(modeBtn, "click", _toggleMode);
		// RS2-06: the only exit control is segReplayExitFloat; the old "segReplayExit" binding was a
		// permanent no-op (that id never existed in the overlay markup) and has been dropped.
		_bindOnce(document.getElementById("segReplayExitFloat"), "click", ctx.stopReplay);

		_bindOnce(document.getElementById("replayFollowToggle"), "click", ((e: Event) => {
			e.stopPropagation(); // header click toggles the sidebar collapse
			if (!replayApi) return;
			const following = replayApi.getFollowIndex() >= 0;
			replayApi.setFollowEffort(following ? -1 : Math.max(0, _lastFollowIdx));
		}) as EventListener);

		// The bar is now a single always-visible row — no collapsible second row.
	}

	// Toggle the replay sync domain: time (clock) ↔ % completion (percent). Icon-only,
	// per the "symbols only" UI. Repositioning of markers is handled by the engine.
	function _toggleMode(): void {
		if (!replayApi) return;
		_mode = _mode === "time" ? "percent" : "time";
		replayApi.setMode(_mode);
		_syncModeButton();
		// Refresh the scrubber label immediately for the new domain.
		_updateProgress(replayApi.getProgressPct(), true);
	}

	// Reflect the current mode on the toggle button (icon + accent + tooltip).
	function _syncModeButton(): void {
		if (!modeBtn) return;
		const percent = _mode === "percent";
		const icon = modeBtn.querySelector("i");
		if (icon) icon.className = percent ? "bi bi-percent" : "bi bi-clock";
		modeBtn.classList.toggle("is-percent", percent);
		modeBtn.title = percent
			? "Sync by % completion — click to sync by time"
			: "Sync by time — click to sync by % completion";
	}

	function _populateSpeedOptions(): void {
		if (!replayApi || !transport) return;

		// Scale the ladder by a robust "typical" effort duration, not the absolute
		// max — one paused/monster effort would otherwise inflate baseSpeed and push
		// even the slowest option too fast. Reuses the chart's outlier-tolerant length
		// formula (P90-capped); falls back to the raw max when too few efforts to judge.
		const maxTime = robustXMax(replayApi.getEfforts().map((e) => e.elapsedTime)) ?? replayApi.getMaxTime();

		// One ladder, one stepper: the transport builds it from core/speed-ladder, sets the default speed
		// on the engine and refreshes the stepper — the same code the race and activity replays run.
		transport.buildLadder(maxTime);
	}

	function _populateEffortList(): void {
		const listContainer = document.getElementById("replayEffortList");
		if (!listContainer || !replayApi) return;

		const efforts = replayApi.getEfforts();
		listContainer.innerHTML = "";

		efforts.forEach((effort: ReplayEffort, idx: number) => {
			const rankClass = idx === 0 ? "gold" : idx === 1 ? "silver" : idx === 2 ? "bronze" : "";
			const rankSymbol =
				idx === 0 ? "🥇" : idx === 1 ? "🥈" : idx === 2 ? "🥉" : "#" + (idx + 1);
			const timeStr = formatTime(effort.elapsedTime);

			const item = document.createElement("div");
			item.className = "replay-effort-item";
			setData(item, 'effortIndex', String(idx));
			// eslint-disable-next-line no-unsanitized/property -- name escapeHtml'd (RS2-01); date toLocaleDateString; idx numeric; effort.color app palette; rank/time internal
			item.innerHTML =
				// Row A — colour swatch + name + rank badge
				'<div class="rei-row-a">' +
				'<input type="checkbox" class="replay-effort-checkbox" data-effort-index="' +
				idx +
				'" title="Highlight this effort">' +
				'<span class="rei-swatch" style="background:' +
				effort.color +
				'"></span>' +
				'<span class="rei-name">' +
				(effort.date
					? new Date(effort.date).toLocaleDateString("de-DE", { day: "2-digit", month: "2-digit", year: "numeric" })
					: escapeHtml(effort.name)) +
				"</span>" +
				'<span class="rei-rank ' +
				rankClass +
				'">' +
				rankSymbol +
				"</span>" +
				"</div>" +
				// Row B — finish time | live speed | gap-bar
				'<div class="rei-row-b">' +
				'<span class="rei-time">' +
				timeStr +
				"</span>" +
				'<span class="rei-speed" id="rei-speed-' +
				idx +
				'">-- km/h</span>' +
				'<span class="rei-gap"  id="rei-gap-' +
				idx +
				'"></span>' +
				"</div>" +
				// Speed bar (full width, colour-coded, animates each frame)
				'<div class="rei-bar-track">' +
				'<div class="rei-bar-fill" id="rei-bar-' +
				idx +
				'" style="background:' +
				effort.color +
				';width:0%"></div>' +
				"</div>";

			item.addEventListener("click", (e: Event) => {
				if ((e.target as HTMLElement).classList.contains("replay-effort-checkbox")) return;
				const currentFollow = replayApi.getFollowIndex();
				replayApi.setFollowEffort(currentFollow === idx ? -1 : idx);
			});

			item.addEventListener("mouseenter", () => {
				if (replayApi) replayApi.setHoverEffort(idx);
			});
			item.addEventListener("mouseleave", () => {
				if (replayApi) replayApi.setHoverEffort(-1);
			});

			const checkbox = item.querySelector(".replay-effort-checkbox") as HTMLInputElement;
			checkbox.addEventListener("change", function (this: HTMLInputElement, e: Event) {
				e.stopPropagation();
				replayApi.toggleEffortHighlight(idx, this.checked);
			});

			listContainer.appendChild(item);
		});

		// Cache speed-bar references so _updateEffortListLiveData avoids getElementById per frame
		_speedEls = [];
		_gapEls = [];
		_barEls = [];
		_lastSpeedText = [];
		efforts.forEach((_: ReplayEffort, idx: number) => {
			_speedEls.push(document.getElementById("rei-speed-" + idx));
			_gapEls.push(document.getElementById("rei-gap-" + idx));
			_barEls.push(document.getElementById("rei-bar-" + idx));
		});

		// Cache the scroll container + measure one row's height (rows are uniform).
		// _visibleRowRange uses these so the per-frame update only touches on-screen rows.
		_listContainer = listContainer;
		const firstItem = listContainer.firstElementChild as HTMLElement | null;
		_rowHeight = firstItem ? firstItem.offsetHeight : 0;

		// Refresh the visible rows when the list is scrolled while paused — no
		// animation frame is running then to pick up the new viewport window.
		if (_listScrollHandler) {
			listContainer.removeEventListener("scroll", _listScrollHandler);
		}
		// Through the throttle, not straight at the update: a scroll burst otherwise ran a full
		// _visibleRowRange (scrollTop/clientHeight → forced reflow) per event.
		_listScrollHandler = (): void => { _liveListThrottle.request(true); };
		listContainer.addEventListener("scroll", _listScrollHandler, { passive: true });

		// Compact-list grow + centre-the-followed-once. Rebuilt with the list; the previous one (if any) is
		// torn down first so its wheel/touch listeners do not stack.
		_effortListExpand?.destroy();
		_effortListExpand = createEffortListExpand({
			container: listContainer,
			rowHeight: () => _rowHeight,
			getFollowIndex: () => (replayApi ? replayApi.getFollowIndex() : -1),
			stayOpen: () => _sidebarHold?.keepOpen === true,
		});

		// Speed scale, label latches and distance series for this effort set (live-data module).
		_live = buildEffortLiveModel(efforts);
	}

	// Cached element arrays — filled in _populateEffortList, used every frame
	let _speedEls: (HTMLElement | null)[] = [];
	let _gapEls: (HTMLElement | null)[] = [];
	let _barEls: (HTMLElement | null)[] = [];
	// Last speed string written per row, so a steady rider costs no DOM writes at all.
	let _lastSpeedText: (string | null)[] = [];
	// Scroll-viewport virtualization: the per-frame update only writes DOM for the
	// rows currently on screen. Container + a once-measured (uniform) row height are
	// cached in _populateEffortList; the scroll handler refreshes rows when the list
	// is scrolled while replay is paused (no frame loop running to pick up the change).
	let _listContainer: HTMLElement | null = null;
	let _rowHeight = 0;
	let _listScrollHandler: (() => void) | null = null;
	/** The elevation strip + its drawer. Lives for one replay session, like the overlay itself. */
	let _profile: SegmentReplayProfile | null = null;
	// Compact-list grow + centre-the-followed-once (effort-list-expand.ts). Rebuilt with the list.
	let _effortListExpand: EffortListExpand | null = null;
	// Click-to-hold the sidebar open (core/panel-hold-open) — the same latch the race replay uses. Lives
	// for the replay session, not the list: the panel outlives every rebuild of its rows.
	let _sidebarHold: HoldOpenPanel | null = null;
	// Per-effort live-data collaborators (speed→width CDF, label latches, distance series), built
	// once per effort set in _populateEffortList so bars stay comparable across efforts/frames.
	let _live: EffortLiveModel | null = null;

	/**
	 * Compute the row window currently within the scroll viewport, with a small
	 * over-scan buffer so rows just outside the edges are ready before they appear.
	 * Rows are uniform height, so the window is derived from scrollTop/clientHeight.
	 * Falls back to the full range when the row height isn't known yet (degrades to
	 * the previous "update everything" behaviour rather than skipping rows).
	 */
	function _visibleRowRange(count: number): { first: number; last: number } {
		const BUFFER = 3;
		if (!_listContainer || _rowHeight <= 0) {
			// Lazily re-measure: the list may not have been laid out at populate time.
			const firstItem = _listContainer
				? (_listContainer.firstElementChild as HTMLElement | null)
				: null;
			_rowHeight = firstItem ? firstItem.offsetHeight : 0;
			if (!_listContainer || _rowHeight <= 0) return { first: 0, last: count - 1 };
		}
		const top = _listContainer.scrollTop;
		const first = Math.max(0, Math.floor(top / _rowHeight) - BUFFER);
		const last = Math.min(
			count - 1,
			Math.ceil((top + _listContainer.clientHeight) / _rowHeight) + BUFFER,
		);
		return { first, last };
	}

	/**
	 * Called every frame (from _updateProgress) to update live speed, time-gap
	 * and the speed bar for each effort.
	 *
	 * Only the rows currently within the scroll viewport are written to the DOM:
	 * off-screen rows are not visible, so refreshing them every frame is wasted
	 * work (and wasted allocations). With a few hundred efforts this scales the
	 * per-frame cost with the visible window instead of the total effort count.
	 */
	function _updateEffortListLiveData(): void {
		if (!replayApi) return;
		const efforts = replayApi.getEfforts();
		if (!efforts || efforts.length === 0) return;

		const effortStates = replayApi.getEffortStates();

		// Leader (effort 0, ranked fastest) drives every gap calc — always read it,
		// even when its row is scrolled out of view.
		const leader = replayApi.getEffortDataAtCurrentTime(0);
		const leaderTime = leader ? leader.time : 0;

		const { first, last } = _visibleRowRange(efforts.length);
		// One reading covers a fixed slice of WALL time, so its ride-time width follows the rate.
		const playbackRate = replayApi.getSpeed();

		// Per-row math (speed measurement, gap-to-leader, bar width) lives in the pure
		// segment-stats-replay-live-data module; this loop only reads the engine
		// getters and writes the derived values into the on-screen rows.
		for (let j = first; j <= last; j++) {
			const label = _live?.labels[j];
			if (!label) continue; // row arrays are built together in _populateEffortList
			const row = computeEffortLiveRow({
				data: replayApi.getEffortDataAtCurrentTime(j),
				finished: !!effortStates[j]?.finished,
				isLeader: j === 0,
				leaderTime,
				speedScale: _live?.speedScale ?? null,
				label,
				series: _live?.series[j] ?? null,
				playbackRate,
			});

			const speedEl = _speedEls[j], gapEl = _gapEls[j], barEl = _barEls[j];
			// The label only moves when the speed genuinely moves (core/speed-label), so skipping the
			// unchanged write keeps a steady rider off the DOM entirely.
			if (speedEl && _lastSpeedText[j] !== row.speedText) {
				speedEl.textContent = row.speedText;
				_lastSpeedText[j] = row.speedText;
			}
			if (gapEl) gapEl.textContent = row.gapText;
			if (barEl) barEl.style.width = row.barWidthPct.toFixed(1) + "%";
		}
	}

	// Reflect the camera state on the header toggle: shows the action a click
	// performs — "(unfollow)" while following, "(follow)" in free mode.
	function _syncFollowToggle(): void {
		const btn = document.getElementById("replayFollowToggle");
		if (!btn || !replayApi) return;
		const following = replayApi.getFollowIndex() >= 0;
		renderFollowToggleLabel(btn, following);
		btn.title = following
			? "Free camera — stop following"
			: _lastFollowIdx >= 0
				? "Re-follow effort #" + (_lastFollowIdx + 1)
				: "Follow the leader";
	}

	function _updateEffortListHighlight(): void {
		const listContainer = document.getElementById("replayEffortList");
		if (!listContainer || !replayApi) return;

		const followIdx = replayApi.getFollowIndex();
		const highlightedIdxs = replayApi.getHighlightedEfforts();
		const effortStates = replayApi.getEffortStates();

		const items = listContainer.querySelectorAll(".replay-effort-item");
		items.forEach((item: Element, idx: number) => {
			item.classList.toggle("followed", idx === followIdx);
			item.classList.toggle("checked", highlightedIdxs.indexOf(idx) >= 0);

			const checkbox = item.querySelector(".replay-effort-checkbox") as HTMLInputElement;
			if (checkbox) checkbox.checked = highlightedIdxs.indexOf(idx) >= 0;

			if (effortStates[idx]) {
				item.classList.toggle("finished", effortStates[idx].finished);
			}
		});
	}

	// PERF-2: the live effort list writes DOM and then reads scrollTop for the row window — a forced
	// reflow, every frame, measured on the race board at 97 % of its frame cost. 5 Hz is the number the
	// race board already uses (race-replay-view.ts BOARD_HZ), so both replays now behave identically;
	// a change of speed/gap text is not perceptible faster. Everything that MUST repaint at once — a
	// seek, a mode switch, a scroll revealing new rows — passes force.
	// Trailing-edge, so a dropped tick is never the last word: seeking while paused delivers exactly one
	// progress callback, and a leading-only gate would swallow it with no later frame to correct it.
	const _liveListThrottle = createTrailingThrottle(1000 / 5, () => _updateEffortListLiveData());

	function _updateProgress(percentage: number, force = false): void {
		if (!replayApi || !transport) return;

		// The transport writes the scrubber (guarded against the user's own drag) and the elapsed clock.
		// Elapsed comes straight from the engine's live currentTime — NOT (pct/100)*maxTime, which drifted
		// when PS-03 sharpening grew getMaxTime() mid-replay (the old _cachedMaxTime staleness). In % mode
		// the same playhead IS the shared completion, shown as a percentage.
		transport.render(
			percentage,
			replayApi.getCurrentTime(),
			_mode === "percent" ? Math.round(percentage) + "%" : undefined,
		);

		// Update per-effort live speed + gap in the sidebar (5 Hz unless forced — see _liveListThrottle).
		_liveListThrottle.request(force);

		// ...and move the dots on the elevation strip. It throttles itself to 30 Hz, so this is a call,
		// not a cost.
		_profile?.tick(replayApi.getCurrentTime());
	}

	function _onReplayComplete(): void {
		transport?.setPlayIcon(false);
	}

	function _highlightTopEfforts(): void {
		if (!replayApi) return;
		const count = Math.min(3, replayApi.getEfforts().length);
		for (let i = 0; i < count; i++) {
			replayApi.toggleEffortHighlight(i, true);
		}
		_updateEffortListHighlight();
	}

	// ================================================================
	// INITIALIZATION
	// ================================================================

	function _initReplay(visibleIndices: number[]): void {
		try {
			if (!replayApi.init) {
				throw new Error("Replay engine not initialized");
			}

			if (typeof maplibreApi.hideStaticLayers === "function") {
				maplibreApi.hideStaticLayers();
			}

			// Heatmap is the active view → show only the raster (chip keeps it attached) and race the
			// markers over it. Otherwise the chip has already detached the raster, so draw the tracks.
			const suppressTracks = !!ctx.isHeatmapShown?.();

			// Replay renders full-res paths: start from the best resolution already on hand (cached
			// full-res, else the chart's downsampled points as a placeholder), then stream the rest
			// in progressively (PS-03). The downsampled placeholder keeps the effort set stable so
			// marker/track indices don't shift as chunks land.
			const success = replayApi.init(
				must(getMaplibreMap(), 'replay needs the maplibre map'),
				buildReplayPerfData(must(ctx.performanceData, 'replay starts after performance data loads'), visibleIndices),
				visibleIndices,
				suppressTracks,
			);

			if (!success) throw new Error("Failed to initialize replay");

			_lastFollowIdx = -1;

			replayApi.onProgress((percentage: number) => {
				_updateProgress(percentage);
			});

			replayApi.onComplete(() => {
				_updateEffortListHighlight();
				_onReplayComplete();
			});

			replayApi.onEffortFinish(() => {
				_updateEffortListHighlight();
			});

			replayApi.onFollowChange((idx: number) => {
				if (idx >= 0) _lastFollowIdx = idx;
				_updateEffortListHighlight();
				_syncFollowToggle();
				// Move the strip's playhead onto (or off) him NOW — a follow chosen while paused must not
				// wait for the next frame that may never come.
				_profile?.tick(replayApi.getCurrentTime(), true);
				// Selecting an effort smoothly grows the list and glides it to the middle — once.
				if (idx >= 0) _effortListExpand?.expand(true);
			});

			_showOverlay();
			_highlightTopEfforts();

			// Dummy history entry so the browser back button exits the replay
			// instead of leaving the page (handled by the session module).
			session.enter();

			replayApi.start();

			// Lazily upgrade the replayed efforts to full resolution (PS-03). Truncated efforts are
			// fetched in chunks; each chunk merges into the running replay and the markers/tracks
			// sharpen. Efforts already full-res do nothing. Aborted on exit; the cache survives.
			_startFullResLoad(visibleIndices);
		} catch (err: unknown) {
			console.error("Error starting replay:", err);
			const msg = err instanceof Error ? err.message : String(err);
			alert("Failed to start replay: " + msg);
			ctx.stopReplay();
		}
	}

	// Kick off the lazy full-res fetch for the replayed efforts (PS-03). Chunks accumulate into the
	// session cache as they land, but the merge into the running replay is COALESCED to a single
	// addLoadedEfforts once the load settles. Each merge rebuilds the whole effort model and
	// re-tessellates the entire track line layer (setData replaces the whole GeoJSON source), so
	// merging per chunk hitched the animation once per chunk; one merge at the end = one brief hitch,
	// and it costs nothing visually because the downsampled placeholder animates until it lands.
	// A no-op when every effort is already full-res (cached / "Full" detail) — nothing loaded, no merge.
	function _startFullResLoad(visibleIndices: number[]): void {
		if (_fullResAbort) _fullResAbort.abort();
		const controller = new AbortController();
		_fullResAbort = controller;

		let loaded = 0;
		const onEffortsReady = (indices: number[]): void => {
			if (controller.signal.aborted) return;
			loaded += indices.length;
			// Progress counter only — the sharpened geometry is applied once, on completion below.
			const statusEl = document.getElementById("segReplayLoadingStatus");
			if (statusEl) statusEl.textContent = "Loading full detail… " + loaded;
		};

		loadFullResEfforts(ctx, visibleIndices, onEffortsReady, controller.signal)
			.then(() => {
				if (controller.signal.aborted || loaded === 0 || !ctx.performanceData) return;
				// Apply the sharpened full-res geometry in one pass, scheduled at idle so the single
				// rebuild lands between animation frames rather than competing with one.
				_whenIdle(() => {
					if (controller.signal.aborted || !ctx.performanceData) return;
					replayApi.addLoadedEfforts(buildReplayPerfData(ctx.performanceData, visibleIndices));
					const statusEl = document.getElementById("segReplayLoadingStatus");
					if (statusEl) statusEl.textContent = "";
				});
			})
			.catch((error: unknown) => {
				if (!controller.signal.aborted) console.error("Error loading full-res replay data:", error);
				// RS2-06: clear the "Loading full detail…" counter on failure so it doesn't stick.
				const statusEl = document.getElementById("segReplayLoadingStatus");
				if (statusEl) statusEl.textContent = "";
			});
	}

	// Run cb during browser idle time (falls back to a macrotask where unsupported). Used to place the
	// one-shot full-res merge off the animation frame's critical path.
	function _whenIdle(cb: () => void): void {
		if (typeof window.requestIdleCallback === "function") {
			window.requestIdleCallback(cb, { timeout: 500 });
		} else {
			setTimeout(cb, 0);
		}
	}

	// ================================================================
	// OVERLAY UI
	// ================================================================

	function _showOverlay(): void {
		overlay = document.getElementById("segmentReplayOverlay");
		if (!overlay) {
			console.error("Replay overlay element not found");
			return;
		}

		modeBtn = document.getElementById("segReplayMode");

		// Each session starts in time mode (the engine resets to it in E.build); mirror that.
		_mode = "time";
		_syncModeButton();

		// The transport owns play/pause, the scrubber (rAF-coalesced, write-back-guarded), the speed −/+
		// stepper and the elapsed clock. Created once: the overlay's controls are static across sessions,
		// so re-creating it would stack the stepper's button listeners. A seek after stop is guarded by
		// getSegmentReplayActive(). A segment scrub seeks by fraction of the timeline.
		if (!transport) {
			transport = createReplayTransport(
				{
					playPauseId: "segReplayPlayPause",
					seekId: "segReplaySeek",
					currentTimeId: "segReplayCurrentTime",
					speedSelectId: "segReplaySpeed",
					speedSlowerId: "segReplaySpeedSlower",
					speedFasterId: "segReplaySpeedFaster",
					speedLabelId: "segReplaySpeedLabel",
					speedPrefKey: "segment",
				},
				{
					seekToPercent: (percent) => {
						if (!getSegmentReplayActive()) return;
						replayApi.seek(percent); // no readout to reset: the speed is a function of the playhead
					},
					togglePlayPause: () => replayApi.togglePlayPause(),
					setSpeed: (mult) => replayApi.setSpeed(mult),
				},
			);
		}

		_populateSpeedOptions();
		_populateEffortList();
		_syncFollowToggle();

		overlay.classList.add("active");

		// Show floating exit button
		const exitFloat = document.getElementById("segReplayExitFloat");
		if (exitFloat) exitFloat.style.display = "flex";

		// Wire effort sidebar collapse toggle (once only)
		const sidebarHeader = document.getElementById("replayEffortSidebarHeader") as (HTMLElement & { _collapseWired?: boolean }) | null;
		const sidebar = document.getElementById("replayEffortSidebar");
		if (sidebarHeader && sidebar && !sidebarHeader._collapseWired) {
			sidebarHeader._collapseWired = true;
			sidebarHeader.addEventListener("click", () => {
				// Collapsing drops the latch too — a highlight ring around a hidden list is noise.
				if (sidebar.classList.toggle("rs-collapsed")) _sidebarHold?.release();
			});
		}

		// Click-to-hold, the same latch and the same rules as the race sidebar (core/panel-hold-open).
		// `.replay-effort-item` is exempt: a click on an effort row already selects/follows it.
		_sidebarHold?.destroy();
		_sidebarHold = sidebar ? createHoldOpenPanel({
			panel: sidebar,
			ignoreSelector: ".replay-effort-item",
			// Re-run the grow so releasing arms a fresh settle-back timer and latching clears it — the same
			// hand-back the race sidebar does, so letting go looks like the end of an ordinary scroll.
			onChange: () => _effortListExpand?.expand(false),
		}) : null;
		// Collapse by default on mobile
		if (sidebar && window.matchMedia("(max-width: 600px)").matches) {
			sidebar.classList.add("rs-collapsed");
		} else if (sidebar) {
			sidebar.classList.remove("rs-collapsed");
		}

		const mapEl = document.querySelector("#map");
		const mapCard = mapEl && mapEl.closest(".seg-hero");
		if (mapCard) {
			mapCard.classList.add("seg-replay-fullscreen");
			document.body.classList.add("seg-replay-fullscreen-active");
		}
		// MapLibre trackResize handles the container resize — no manual invalidateSize (P15).

		_bindControls();
		_mountProfileStrip();
	}

	/**
	 * The elevation strip under the transport. Mount-gated in two ways: no markup, no strip; and no
	 * usable elevation (a shapes segment whose efforts carry no altitude), no strip either — the module
	 * decides that from the data, so nothing here has to know what kind of segment this is.
	 */
	function _mountProfileStrip(): void {
		if (!replayApi) return;
		const efforts = replayApi.getEfforts();
		if (!efforts || efforts.length === 0) return;

		_profile = createSegmentReplayProfile({
			source: {
				kind: ctx.config.kind,
				hasGpsData: ctx.config.hasGpsData,
				segmentCoords: ctx.config.segmentCoords,
			},
			efforts,
			sample: (idx) => replayApi.getEffortDataAtCurrentTime(idx),
			follow: () => (replayApi ? replayApi.getFollowIndex() : -1),
			// The strip overlays the map, so the map's box is unchanged — but MapLibre still wants the
			// nudge after a layout move, exactly as the fullscreen toggle gives it.
			onResize: () => getMaplibreMap()?.resize(),
		});
	}

	function _hideOverlay(): void {
		_profile?.destroy();
		_profile = null;

		if (overlay) overlay.classList.remove("active");

		_liveListThrottle.cancel();

		// Detach the per-list scroll handler bound in _populateEffortList.
		if (_listContainer && _listScrollHandler) {
			_listContainer.removeEventListener("scroll", _listScrollHandler);
		}
		// Tear down the grow/centre helper (its wheel/touch listeners + collapse timer + .is-expanded).
		_effortListExpand?.destroy();
		_effortListExpand = null;
		_sidebarHold?.destroy();
		_sidebarHold = null;
		_listScrollHandler = null;
		_listContainer = null;
		_rowHeight = 0;

		const exitFloat = document.getElementById("segReplayExitFloat");
		if (exitFloat) exitFloat.style.display = "none";

		// Reset sidebar collapse state
		const sidebar = document.getElementById("replayEffortSidebar");
		if (sidebar) sidebar.classList.remove("rs-collapsed");

		const mapEl = document.querySelector("#map");
		const mapCard = mapEl && mapEl.closest(".seg-hero");
		if (mapCard) {
			mapCard.classList.remove("seg-replay-fullscreen");
			document.body.classList.remove("seg-replay-fullscreen-active");
		}
		// MapLibre trackResize handles the container resize — no manual invalidateSize (P15).
	}
