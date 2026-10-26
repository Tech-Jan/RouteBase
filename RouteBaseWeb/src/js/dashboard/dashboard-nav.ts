import { readData, requireData } from '../core/data-attrs';
import { FRIEND_REQUEST_RESOLVED_EVENT } from '../core/dom-events';
import { devicePrefKeys, getBool, setBool } from '../core/device-prefs';
import type { PendingCountResponse } from '../generated/contracts';

    // ── Expandable nav groups ──────────────────────────────────────────────

    function setupNavGroups(): void {
        const toggles = document.querySelectorAll<HTMLButtonElement>('[data-toggle-group]');
        toggles.forEach(btn => {
            const href = requireData(btn, 'toggleGroup'); // [data-toggle-group] selector guarantees it
            const group = document.querySelector<HTMLElement>(`.sidebar-nav-group[data-group="${href}"]`);
            if (!group) return;

            const prefKey = devicePrefKeys.navGroupOpen(href);
            // Keep aria-expanded in step with the restored `open` class — the click handler below already
            // does, but a group restored open from storage was announced as collapsed.
            if (getBool(prefKey, false)) { group.classList.add('open'); btn.setAttribute('aria-expanded', 'true'); }

            btn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                const isOpen = group.classList.toggle('open');
                setBool(prefKey, isOpen);
                btn.setAttribute('aria-expanded', String(isOpen));
            });
        });

        // If a sub-item is active, auto-open that group
        document.querySelectorAll<HTMLElement>('.sidebar-subnav-item.active').forEach(activeItem => {
            const group = activeItem.closest<HTMLElement>('.sidebar-nav-group');
            if (group) {
                group.classList.add('open');
                const href = readData(group, 'group');
                if (href) {
                    setBool(devicePrefKeys.navGroupOpen(href), true);
                    document.querySelector<HTMLButtonElement>(`[data-toggle-group="${href}"]`)
                        ?.setAttribute('aria-expanded', 'true');
                }
            }
        });
    }

    // ── Friends pending-count badge

    const FRIENDS_POLL_MS = 60_000;

    // PendingCountResponse imported from generated contracts (C# Models/FriendsContracts.cs).

    // A newer poll must win even if an older one's response lands later: the SO-03 event and the
    // 60 s interval can overlap, so two polls run concurrently, and last-to-arrive is not last-to-start.
    // Stamp each call and drop a response a later poll has already superseded — otherwise a stale count
    // flips the pip back after a fresher one settled it.
    let badgeGen = 0;

    // DT-04: scope to the friends pips only. Toggling every `.nav-pip` off the friends count
    // would force-toggle any future non-friends badge (e.g. the deferred /Record pending badge).
    export async function pollFriendsBadge(): Promise<void> {
        if (document.visibilityState === 'hidden') return; // DT-13: don't poll hidden tabs
        const gen = ++badgeGen;
        try {
            const res = await fetch('/api/friends/pending-count');
            if (!res.ok) return;
            const data: PendingCountResponse = await res.json();
            if (gen !== badgeGen) return; // a newer poll started while we awaited — its result is authoritative
            const visible = data.count > 0;
            document.querySelectorAll<HTMLElement>('#navFriendsBadge, #navProfileFriendsBadge').forEach(pip => {
                pip.classList.toggle('visible', visible);
            });
        } catch {
            // network error — silently skip
        }
    }

    function setupFriendsBadge(): void {
        void pollFriendsBadge();
        setInterval(() => void pollFriendsBadge(), FRIENDS_POLL_MS);
        // SO-03: refresh immediately when Friends resolves a request in-page (same document).
        document.addEventListener(FRIEND_REQUEST_RESOLVED_EVENT, () => void pollFriendsBadge());
    }

    // ── Sidebar toggle (tablet) ────────────────────────────────────────────

    function setupSidebarToggle(): void {
        const btn = document.getElementById('sidebar-toggle');
        const shell = btn?.closest<HTMLElement>('.cockpit-shell');
        if (!btn || !shell) return;
        btn.addEventListener('click', () => shell.classList.toggle('sidebar-off'));
    }

    // ── Mobile "More" sheet ────────────────────────────────────────────────

    function setupMoreSheet(): void {
        const moreBtn      = document.getElementById('tabbar-more-btn');
        const moreSheet    = document.getElementById('tabbar-more-sheet');
        const moreBackdrop = document.getElementById('tabbar-more-backdrop');
        if (!moreBtn || !moreSheet || !moreBackdrop) return;

        const open = (): void => {
            moreSheet.classList.add('open');
            moreBackdrop.classList.add('open');
            moreBtn.setAttribute('aria-expanded', 'true');
            moreSheet.setAttribute('aria-hidden', 'false');
            moreBackdrop.setAttribute('aria-hidden', 'false');
        };
        const close = (): void => {
            moreSheet.classList.remove('open');
            moreBackdrop.classList.remove('open');
            moreBtn.setAttribute('aria-expanded', 'false');
            moreSheet.setAttribute('aria-hidden', 'true');
            moreBackdrop.setAttribute('aria-hidden', 'true');
        };

        moreBtn.addEventListener('click', () => moreSheet.classList.contains('open') ? close() : open());
        moreBackdrop.addEventListener('click', close);
        document.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
    }

    // ── Init ───────────────────────────────────────────────────────────────

    document.addEventListener('DOMContentLoaded', () => {
        setupNavGroups();
        setupFriendsBadge();
        setupSidebarToggle();
        setupMoreSheet();
    });
