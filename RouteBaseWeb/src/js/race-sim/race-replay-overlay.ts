/**
 * The race replay chrome: the fullscreen toggle, the follow sidebar and the fullscreen close button.
 *
 * Split from race-replay.ts (which owns playback and the field) to keep both under the file-size cap and to
 * keep this a pure DOM/UI concern. It reuses the shared history session (Esc/Back) exactly as the segment
 * replay does — but here it exits FULLSCREEN, not the replay: Play runs inline and is decoupled from
 * fullscreen, so closing fullscreen leaves the replay running.
 */

import { readData } from '../core/data-attrs';
import { must } from '../core/invariant';
import { createReplayHistorySession } from '../core/replay-history-session';
import { renderFollowToggleLabel } from '../core/map-utils-shared';
import { escapeHtml } from '../core/html';
import { formatGap } from '../core/format';
import { devicePrefKeys, getNumber, setNumber } from '../core/device-prefs';
import { createHoldOpenPanel } from '../core/panel-hold-open';
import { finishStatusShort, specOf } from './race-finish-status';
import { MAX_EMPHASIS_HIGHLIGHTS } from './race-name-labels';
import type { SidebarRow } from './race-sidebar-rows';

export interface RaceReplayOverlayOptions {
	/** Follow / unfollow a rider by field index — the same handler the results table row-click uses. */
	onFollow: (index: number) => void;
	/** The header (follow)/(unfollow) toggle: re-follow the last rider, or unfollow. */
	onFollowToggle: () => void;
	/** The map container changed size (fullscreen enter/exit). MapLibre trackResize usually covers it. */
	onResize?: () => void;
}

export interface RaceReplayOverlay {
	/** Hand over the whole ordered field (real riders first). The list renders only the visible rows. */
	renderSidebar(rows: readonly SidebarRow[], followIndex: number): void;
	/** Reflect follow state on the header toggle ((follow) ↔ (unfollow)). */
	syncFollowToggle(following: boolean): void;
	isFullscreen(): boolean;
	destroy(): void;
}

/** Fixed row height — the virtual list positions rows by index, so this MUST match the CSS .race-sb-row. */
const ROW_H = 28;
/** Rows rendered above/below the viewport, so a fast scroll never shows blank space. */
const ROW_BUFFER = 6;
/** Window enough rows to fill the EXPANDED body height (see .is-expanded), so a click-to-expand reveals
 *  already-rendered rows rather than blank space during the grow animation. */
const LIST_RENDER_PX = 460;
/** How long the list stays expanded after the last interaction. */
const EXPAND_HOLD_MS = 3500;
/** Rows visible in the compact list — MUST match the collapsed max-height in .race-replay-sidebar-body. */
const VISIBLE_ROWS = 5;

function byId<T extends HTMLElement>(id: string): T | null {
	return document.getElementById(id) as T | null;
}

/**
 * A compact, scannable name for the narrow sidebar: the first token (a surname in the Ski to Sea data,
 * "PILZ Lia") plus the next token's initial — "PILZ L." A single-token name (or a bib) is left as-is; the
 * CSS still ellipsis-truncates anything that overflows. The full name is the row's hover tooltip.
 */
function shortName(name: string): string {
	const parts = name.trim().split(/\s+/);
	if (parts.length <= 1) return name;
	return `${parts[0]} ${must(parts[1], 'length > 1 (guarded)').charAt(0).toUpperCase()}.`;
}

export function createRaceReplayOverlay(options: RaceReplayOverlayOptions): RaceReplayOverlay {
	const hero = byId('raceHero');
	const fsBtn = byId('raceMapFullscreen');
	const exitBtn = byId<HTMLButtonElement>('raceReplayExit');
	const sidebar = byId('raceReplaySidebar');
	const sidebarBody = byId('raceReplaySidebarBody');
	const pinnedEl = byId('raceReplaySidebarPinned');
	const sidebarHeader = byId('raceReplaySidebarHeader');
	const followToggle = byId('raceReplayFollowToggle');
	const resizeHandle = byId<HTMLElement>('raceReplaySidebarResize');

	let _fullscreen = false;

	// The dummy-entry / Back-button / Escape dance — shared with the segment replay. Here it exits
	// fullscreen (isActive === _fullscreen), leaving the replay running.
	const session = createReplayHistorySession({
		stateKey: 'raceFullscreen',
		isActive: () => _fullscreen,
		exit: () => exitFullscreen(),
	});

	function enterFullscreen(): void {
		if (_fullscreen || !hero) return;
		_fullscreen = true;
		hero.classList.add('race-replay-fullscreen');
		document.body.classList.add('race-replay-fullscreen-active');
		if (exitBtn) exitBtn.style.display = 'flex';
		if (fsBtn) fsBtn.innerHTML = '<i class="bi bi-fullscreen-exit"></i>';
		session.enter();
		options.onResize?.();
	}

	function exitFullscreen(): void {
		if (!_fullscreen || !hero) return;
		_fullscreen = false;
		hero.classList.remove('race-replay-fullscreen');
		document.body.classList.remove('race-replay-fullscreen-active');
		if (exitBtn) exitBtn.style.display = 'none';
		if (fsBtn) fsBtn.innerHTML = '<i class="bi bi-arrows-fullscreen"></i>';
		// Guarded history.back(): a no-op when a Back-button exit already moved us back.
		session.leave();
		options.onResize?.();
	}

	const onFsClick = (e: Event): void => {
		e.preventDefault();
		if (_fullscreen) exitFullscreen();
		else enterFullscreen();
	};
	fsBtn?.addEventListener('click', onFsClick);
	exitBtn?.addEventListener('click', exitFullscreen);

	// The header collapses the list; the follow button inside it must not also collapse it.
	// Collapsing also drops the latch — a highlight ring around a panel with nothing in it is noise, and
	// the pointerdown that got here has already toggled it (see the hold below).
	const onHeaderClick = (): void => {
		if (sidebar?.classList.toggle('rs-collapsed')) hold?.release();
	};
	sidebarHeader?.addEventListener('click', onHeaderClick);

	const onFollowToggleClick = (e: Event): void => {
		e.stopPropagation();
		options.onFollowToggle();
	};
	followToggle?.addEventListener('click', onFollowToggleClick);

	// A tap on a row follows that rider. Resolve the row on pointerDOWN and fire on pointerUP — NOT on
	// `click`: the list rebuilds every row node on each 5 Hz board tick, so a click whose mousedown row is
	// destroyed before mouseup is silently dropped (the "tap three times before it takes" bug). Capturing
	// the index at press time survives the rebuild; the movement slop + pointercancel keep a scroll-drag
	// from registering as a tap. Both the pinned copy and the in-field copy of a real rider carry the same
	// data-index, so either one follows him.
	const TAP_SLOP_PX = 8;
	let _tapIndex = -1;
	let _tapX = 0;
	let _tapY = 0;
	const onRowPointerDown = (e: PointerEvent): void => {
		const row = (e.target as HTMLElement).closest<HTMLElement>('[data-index]');
		_tapIndex = row ? Number(readData(row, 'index')) : -1;
		_tapX = e.clientX;
		_tapY = e.clientY;
	};
	const onRowPointerUp = (e: PointerEvent): void => {
		if (_tapIndex < 0) return;
		const index = _tapIndex;
		_tapIndex = -1;
		const moved = Math.abs(e.clientX - _tapX) > TAP_SLOP_PX || Math.abs(e.clientY - _tapY) > TAP_SLOP_PX;
		if (!moved) options.onFollow(index);
	};
	const onRowPointerCancel = (): void => { _tapIndex = -1; };
	for (const el of [sidebarBody, pinnedEl]) {
		el?.addEventListener('pointerdown', onRowPointerDown);
		el?.addEventListener('pointerup', onRowPointerUp);
		el?.addEventListener('pointercancel', onRowPointerCancel);
	}
	// Disarm on any release or new press ANYWHERE: a press whose release lands outside the lists left a
	// stale index armed, and a later unrelated release inside them (within slop of the old coords) fired
	// a phantom follow. The pointerdown reset runs in CAPTURE phase so it precedes the row handler that
	// arms the tap; the lists' own pointerup still reads the index before the window bubble clears it.
	// Named so destroy() can remove them — anonymous window listeners outlived the overlay (and held
	// _rows alive) across destroy/re-create cycles.
	const disarmTap = (): void => { _tapIndex = -1; };
	window.addEventListener('pointerup', disarmTap);
	window.addEventListener('pointercancel', disarmTap);
	window.addEventListener('pointerdown', disarmTap, true);

	// Collapse by default on a narrow screen so it does not swallow the inline map.
	if (sidebar && window.matchMedia('(max-width: 600px)').matches) {
		sidebar.classList.add('rs-collapsed');
	}

	// ── drag-to-resize the sidebar (mouse or touch, via pointer events) ───────────────────────────────────
	const SB_MIN = 150;
	const sbMax = (): number => Math.min(window.innerWidth * 0.72, 400);
	let sidebarWidth = 220;
	let dragStartX = 0;
	let dragStartW = 0;

	function setSidebarWidth(px: number): void {
		if (!sidebar) return;
		sidebarWidth = Math.round(Math.max(SB_MIN, Math.min(sbMax(), px)));
		sidebar.style.setProperty('--race-sb-width', `${sidebarWidth}px`);
	}

	// 0 means "never chosen": setSidebarWidth would clamp it to SB_MIN, which is a width nobody picked.
	const savedWidth = getNumber(devicePrefKeys.raceSidebarWidth, 0);
	if (savedWidth > 0) setSidebarWidth(savedWidth);

	const onResizeMove = (e: PointerEvent): void => setSidebarWidth(dragStartW + (e.clientX - dragStartX));
	const onResizeUp = (e: PointerEvent): void => {
		resizeHandle?.releasePointerCapture(e.pointerId);
		resizeHandle?.removeEventListener('pointermove', onResizeMove);
		resizeHandle?.removeEventListener('pointerup', onResizeUp);
		resizeHandle?.removeEventListener('pointercancel', onResizeUp);
		sidebar?.classList.remove('is-resizing');
		setNumber(devicePrefKeys.raceSidebarWidth, sidebarWidth);
	};
	const onResizeDown = (e: PointerEvent): void => {
		if (!sidebar || !resizeHandle) return;
		e.preventDefault();   // a drag must not select text or scroll
		dragStartX = e.clientX;
		dragStartW = sidebar.getBoundingClientRect().width;
		sidebar.classList.add('is-resizing');
		resizeHandle.setPointerCapture(e.pointerId);   // capture keeps the drag alive off the 10px strip
		resizeHandle.addEventListener('pointermove', onResizeMove);
		resizeHandle.addEventListener('pointerup', onResizeUp);
		resizeHandle.addEventListener('pointercancel', onResizeUp);
	};
	resizeHandle?.addEventListener('pointerdown', onResizeDown);

	// ── virtualized rider list ──────────────────────────────────────────────────────────────────────────
	// The sidebar can hold the whole field (500+). Only the rows in view are in the DOM: a sizer div gives
	// the scrollbar its full height, and each visible row is absolutely positioned at its index * ROW_H.
	let _rows: readonly SidebarRow[] = [];
	let _followIndex = -1;
	let sizer: HTMLDivElement | null = null;
	let _rafId: number | null = null;

	/** One row's HTML. `top` positions it in the virtual sizer; null marks a pinned (normal-flow) copy. */
	function rowHtml(r: SidebarRow, top: number | null): string {
		// Stopped says WHEN to swap the gap for a label; the STATUS says which label, and in what colour.
		// Conflating the two is how an abandoned rider used to read "fin", in success-green, stalled
		// mid-course. The badge class comes from the one status table, so the row and the results cell
		// cannot disagree about the same rider.
		const badge = specOf(r.status).badgeClass;
		const stoppedCls = r.stopped ? (badge == null ? ' is-finished' : ` ${badge}`) : '';
		const cls = `race-sb-row${r.isReal ? ' is-real' : ''}${r.emphasised ? ' is-emphasised' : ''}${stoppedCls}`
			+ `${r.index === _followIndex ? ' followed' : ''}${top == null ? ' is-pinned' : ''}`;
		// `DNF 2` — which number he was to drop out, not a place. Only riders who hold no place carry one,
		// so a finisher's slot is unchanged.
		const label = finishStatusShort(r.status) + (r.statusOrdinal == null ? '' : ` ${r.statusOrdinal}`);
		const gap = r.stopped ? label : r.position === 1 ? '—' : formatGap(r.gapMetres);
		const style = top == null ? '' : ` style="top:${top}px"`;
		// The frozen number is faded: it is the position he HELD when he stopped, kept so it stops climbing
		// as the field rides past him — a memory, and it should not read as a live standing.
		const posCls = `race-sb-pos${r.positionFrozen ? ' is-frozen' : ''}`;
		// Compact name in the row; the full name is the hover tooltip.
		return `<div class="${cls}" data-index="${r.index}"${style} title="${escapeHtml(r.name)}">
				<span class="${posCls}">${r.position}</span>
				<span class="race-sb-name">${escapeHtml(shortName(r.name))}</span>
				<span class="race-sb-gap">${gap}</span>
			</div>`;
	}

	function paintWindow(): void {
		if (!sidebarBody || !sizer) return;
		const n = _rows.length;
		sizer.style.height = `${n * ROW_H}px`;
		const scrollTop = sidebarBody.scrollTop;
		// Always window for the expanded height so growing the list never flashes blank rows.
		const start = Math.max(0, Math.floor(scrollTop / ROW_H) - ROW_BUFFER);
		const end = Math.min(n, Math.ceil((scrollTop + LIST_RENDER_PX) / ROW_H) + ROW_BUFFER);

		let html = '';
		for (let i = start; i < end; i++) html += rowHtml(must(_rows[i], 'window clamped to rows length'), i * ROW_H);
		// eslint-disable-next-line no-unsanitized/property -- rowHtml escapeHtml()s the name; every other field is numeric
		sizer.innerHTML = html;
	}

	/**
	 * The riders pinned above the field and always visible, in a distinct colour from their in-field copy:
	 * the real ones, plus whoever was emphasised by hand.
	 *
	 * Emphasis earns a pin for the same reason it earns a map label — you said "watch THESE", and a rider
	 * you are watching is useless at row 824 of a virtual list. Past MAX_EMPHASIS_HIGHLIGHTS it stops:
	 * "Emphasise" with no filter takes all 424, and 424 pinned rows are in NORMAL FLOW, so they would push
	 * the field list off the screen. Real riders always pin — they are few by nature, not by policy.
	 *
	 * Unless the owner hid the call-out: that switch means "stop shouting about him" on every surface at
	 * once, so a rider it silenced on the map and in the speed chart cannot still be pinned here.
	 */
	function paintPinned(): void {
		if (!pinnedEl) return;
		const callable = _rows.filter((r) => !r.calloutOff);
		const emphasised = callable.filter((r) => r.emphasised);
		const pin = emphasised.length <= MAX_EMPHASIS_HIGHLIGHTS
			? callable.filter((r) => r.isReal || r.emphasised)
			: callable.filter((r) => r.isReal);
		pinnedEl.style.display = pin.length > 0 ? '' : 'none';
		// eslint-disable-next-line no-unsanitized/property -- rowHtml escapeHtml()s the name; every other field is numeric
		pinnedEl.innerHTML = pin.map((r) => rowHtml(r, null)).join('');
	}

	// The list is compact (5 rows); interacting with it grows it (.is-expanded) for a few seconds so the
	// field scrolls comfortably, then it settles back. Each interaction restarts the hold timer.
	// Following a rider keeps HIM centred while the field shuffles around him: each board tick nudges the
	// scroll so his row stays in the middle (instant, so a rank change just slides the neighbours by a row).
	// A wheel or touch-drag hands control back for a few seconds — the list grows for easier scrolling — then
	// it eases him back to the middle. Cheap on mobile: the nudge only fires when his centred target actually
	// moves, i.e. on a rank change, not every frame.
	let _freeScroll = false;
	let _autoScrolling = false;   // true during a programmatic smooth scroll, so it is not read as a user scroll
	let _prevFollow = -1;
	let freeTimer: ReturnType<typeof setTimeout> | undefined;
	let autoTimer: ReturnType<typeof setTimeout> | undefined;

	function centerFollowed(smooth: boolean): void {
		if (!sidebarBody || _followIndex < 0) return;
		const row = _rows.findIndex((r) => r.index === _followIndex);
		if (row < 0) return;
		const compactH = VISIBLE_ROWS * ROW_H;
		const maxScroll = Math.max(0, _rows.length * ROW_H - compactH);
		// Centre him — clamped, so a leader sits at the top and the last-placed rider at the bottom.
		const target = Math.max(0, Math.min(maxScroll, row * ROW_H - (compactH - ROW_H) / 2));
		if (Math.abs(sidebarBody.scrollTop - target) < 0.5) return;
		if (smooth && typeof sidebarBody.scrollTo === 'function') {
			_autoScrolling = true;
			sidebarBody.scrollTo({ top: target, behavior: 'smooth' });
			if (autoTimer) clearTimeout(autoTimer);
			autoTimer = setTimeout(() => { _autoScrolling = false; }, 600);
		} else {
			sidebarBody.scrollTop = target;
		}
	}

	/** Each render tick: keep the followed rider centred unless the user is scrolling freely. Smooth on a
	 *  new selection (including picking a real rider from the pinned list), instant while tracking. */
	function maintainCenter(): void {
		if (_freeScroll || _autoScrolling || _followIndex < 0) { _prevFollow = _followIndex; return; }
		const isNewSelection = _followIndex !== _prevFollow;
		_prevFollow = _followIndex;
		centerFollowed(isNewSelection);
	}

	function beginFreeScroll(): void {
		if (!sidebarBody) return;
		_freeScroll = true;
		sidebarBody.classList.add('is-expanded');
		if (freeTimer) clearTimeout(freeTimer);
		// No timer while the panel is latched OR while a press is still down on it — a scrollbar drag is
		// the second one, and it emits neither wheel nor touchmove, so nothing else would keep it grown.
		if (!hold?.keepOpen) freeTimer = setTimeout(endFreeScroll, EXPAND_HOLD_MS);
	}
	function endFreeScroll(): void {
		if (hold?.keepOpen) return;
		_freeScroll = false;
		sidebarBody?.classList.remove('is-expanded');
		if (_followIndex >= 0) centerFollowed(true);
	}
	// Wheel (mouse) or touch-drag hands control to the user; a tap on a row stays a follow, not a free-scroll.
	sidebarBody?.addEventListener('wheel', beginFreeScroll, { passive: true });
	sidebarBody?.addEventListener('touchmove', beginFreeScroll, { passive: true });

	/**
	 * HELD OPEN — the third axis, and the one the other two could not express.
	 *
	 *   rs-collapsed  the header's own show/hide of the whole panel
	 *   is-expanded   the few seconds of extra height after a scroll, then it settles back
	 *   is-held       THIS: click the sidebar and it stays open until you click it again
	 *
	 * The rule and its reasoning live in core/panel-hold-open (the segment replay's effort list uses the
	 * same one). What is local is the consequence: releasing does NOT snap the panel shut — it hands
	 * control back to the ordinary hold timer, so letting go looks exactly like stopping a scroll. And
	 * auto-centring stays off while held, or reading the field would fight the followed rider.
	 *
	 * `.race-sb-row` is exempt because a tap there is already "follow him" — see the module.
	 */
	const hold = sidebar ? createHoldOpenPanel({
		panel: sidebar,
		ignoreSelector: '.race-sb-row',
		onChange: () => {
			if (freeTimer) clearTimeout(freeTimer);
			beginFreeScroll();   // staying open: expand with no timer. let go: expand WITH one.
		},
	}) : null;

	// Scroll only re-renders the window, coalesced to a frame so a fast drag does not thrash innerHTML.
	const onSidebarScroll = (): void => {
		if (_rafId !== null) return;
		_rafId = requestAnimationFrame(() => { _rafId = null; paintWindow(); });
	};
	sidebarBody?.addEventListener('scroll', onSidebarScroll);

	return {
		renderSidebar(rows, followIndex) {
			if (!sidebarBody) return;
			_rows = rows;
			_followIndex = followIndex;
			if (rows.length === 0) {
				sizer = null;
				if (pinnedEl) pinnedEl.style.display = 'none';
				sidebarBody.innerHTML = '<div class="race-empty">No field yet — import a results file.</div>';
				return;
			}
			// Build the sizer once; thereafter only its window of rows is rewritten.
			if (!sizer || sizer.parentElement !== sidebarBody) {
				sidebarBody.innerHTML = '<div class="race-sb-sizer"></div>';
				sizer = sidebarBody.querySelector<HTMLDivElement>('.race-sb-sizer');
			}
			maintainCenter();   // keep the followed rider centred BEFORE windowing, so the window is around him
			paintWindow();
			paintPinned();
		},
		syncFollowToggle(following) {
			if (!followToggle) return;
			renderFollowToggleLabel(followToggle, following);
			followToggle.title = following ? 'Free camera — stop following' : 'Follow a rider';
		},
		isFullscreen: () => _fullscreen,
		destroy() {
			fsBtn?.removeEventListener('click', onFsClick);
			exitBtn?.removeEventListener('click', exitFullscreen);
			sidebarHeader?.removeEventListener('click', onHeaderClick);
			followToggle?.removeEventListener('click', onFollowToggleClick);
			for (const el of [sidebarBody, pinnedEl]) {
				el?.removeEventListener('pointerdown', onRowPointerDown);
				el?.removeEventListener('pointerup', onRowPointerUp);
				el?.removeEventListener('pointercancel', onRowPointerCancel);
			}
			sidebarBody?.removeEventListener('scroll', onSidebarScroll);
			sidebarBody?.removeEventListener('wheel', beginFreeScroll);
			sidebarBody?.removeEventListener('touchmove', beginFreeScroll);
			window.removeEventListener('pointerup', disarmTap);
			window.removeEventListener('pointercancel', disarmTap);
			window.removeEventListener('pointerdown', disarmTap, true);
			hold?.destroy();
			if (freeTimer) clearTimeout(freeTimer);
			if (autoTimer) clearTimeout(autoTimer);
			resizeHandle?.removeEventListener('pointerdown', onResizeDown);
			// Tearing down mid-fullscreen must not leak the body class or the dummy history entry.
			if (_fullscreen) exitFullscreen();
			// ...and drop the session's global popstate + keydown handlers. This overlay is recreated per
			// mount, so without this every recreate stacked another live Escape/Back handler.
			session.destroy();
		},
	};
}
