/**
 * Activity Details — Unified Edit Mode
 *
 * Manages a single "Edit" button that toggles all editable fields (name, type,
 * visibility) simultaneously. One PATCH to /api/activities/{id} saves only the
 * changed fields.
 */

import { createInlineError } from '../core/inline-error';
import { readData, setData } from '../core/data-attrs';
import { renderVisibilityBadge } from '../core/visibility-badge';
import type { ApiError } from '../generated/contracts';

(function initActivityEdit() {
    const header = document.querySelector<HTMLElement>('[data-track-id]');
    if (!header) return;
    const trackId = readData(header, 'trackId');
    if (!trackId) return;

    const editBtn0   = document.getElementById('editActivityBtn');
    const saveBtn0   = document.getElementById('saveActivityBtn') as HTMLButtonElement | null;
    const cancelBtn0 = document.getElementById('cancelActivityBtn');
    if (!editBtn0 || !saveBtn0 || !cancelBtn0) return; // not owner

    const nameDisplay0  = document.getElementById('activityName');
    const nameEditor0   = document.getElementById('editNameInput') as HTMLInputElement | null;
    const typeBadge0    = document.getElementById('activityTypeBadge');
    const typeSelect0   = document.getElementById('activityTypeSelect') as HTMLSelectElement | null;
    const visBadge0     = document.getElementById('visibilityBadge');
    const visSelect0    = document.getElementById('visibilitySelect') as HTMLSelectElement | null;

    if (!nameDisplay0 || !nameEditor0 || !typeBadge0 || !typeSelect0 ||
        !visBadge0 || !visSelect0) return;

    // Narrowed rebinds (G3): control-flow narrowing does not cross into the closures below, and the
    // alternative was a `!` on every later use. All non-null from here on.
    const editBtn = editBtn0, saveBtn = saveBtn0, cancelBtn = cancelBtn0;
    const nameDisplay = nameDisplay0, nameEditor = nameEditor0, typeBadge = typeBadge0;
    const typeSelect = typeSelect0, visBadge = visBadge0, visSelect = visSelect0;

    // Initialise selects from data attributes
    visSelect.value  = readData(visSelect, 'current')  ?? 'friends';
    typeSelect.value = readData(typeSelect, 'current') ?? '';

    // Badge rendering lives in core/visibility-badge.ts — the segment header paints the same chip, and a
    // second copy of these maps is exactly how the two would drift.
    const updateVisBadge = (vis: string): void => renderVisibilityBadge(visBadge, vis);

    // Edit-mode messages (empty field, a refused PATCH) show under the header while the editor is open.
    // They used to be alert()s — a modal over a two-field inline edit, which the user then had to
    // dismiss before they could fix the field it was complaining about.
    const editError = createInlineError(
        nameEditor.closest('.page-header-left') ?? header,
        { fields: [nameEditor, typeSelect] },
    );

    // --- Edit mode state ---
    let originals: { name: string; activityType: string; visibility: string } = {
        name: '', activityType: '', visibility: 'friends'
    };

    function enterEditMode(): void {
        originals = {
            name:         nameDisplay.textContent?.trim() ?? '',
            // DC-06: source the current type from dataset.current (single source of truth,
            // like visibility) so an imported type outside the option whitelist still round-trips.
            activityType: readData(typeSelect, 'current') ?? '',
            visibility:   readData(visSelect, 'current') ?? 'friends',
        };

        nameEditor.value  = originals.name;
        typeSelect.value  = originals.activityType;
        visSelect.value   = originals.visibility;

        nameDisplay.style.display = 'none';
        nameEditor.style.setProperty('display', 'inline-block', 'important');
        typeBadge.style.display   = 'none';
        typeSelect.style.setProperty('display', 'inline-block', 'important');
        visBadge.style.display    = 'none';
        visSelect.style.setProperty('display', 'inline-block', 'important');

        editBtn.style.display   = 'none';
        saveBtn.style.display   = '';
        cancelBtn.style.display = '';

        nameEditor.focus();
        nameEditor.select();
    }

    function exitEditMode(): void {
        editError.clear(); // leaving the editor drops whatever it was complaining about
        nameDisplay.style.display = '';
        nameEditor.style.setProperty('display', 'none', 'important');
        typeBadge.style.display   = '';
        typeSelect.style.setProperty('display', 'none', 'important');
        visBadge.style.display    = '';
        visSelect.style.setProperty('display', 'none', 'important');

        editBtn.style.display   = '';
        saveBtn.style.display   = 'none';
        cancelBtn.style.display = 'none';
    }

    function cancelEdit(): void {
        nameEditor.value = originals.name;
        typeSelect.value = originals.activityType;
        visSelect.value  = originals.visibility;
        exitEditMode();
    }

    async function saveAll(): Promise<void> {
        const newName = nameEditor.value.trim();
        const newType = typeSelect.value.trim();
        const newVis  = visSelect.value;

        if (!newName || !newType) {
            editError.show('Name and type cannot be empty.');
            return;
        }
        editError.clear();

        const payload: Record<string, string> = {};
        if (newName !== originals.name)         payload['name']         = newName;
        if (newType !== originals.activityType)  payload['activityType'] = newType;
        if (newVis  !== originals.visibility)    payload['visibility']   = newVis;

        if (Object.keys(payload).length === 0) {
            exitEditMode();
            return;
        }

        saveBtn.disabled = true;
        try {
            const resp = await fetch(`/api/activities/${trackId}`, {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload),
            });
            if (!resp.ok) {
                const err = await resp.json().catch(() => ({})) as Partial<ApiError>;
                editError.show(err.message ?? resp.statusText);
                return;
            }

            if (payload['name'])         { nameDisplay.textContent = newName; }
            if (payload['activityType']) { typeBadge.textContent   = newType; setData(typeSelect, 'current', newType); }
            if (payload['visibility'])   { updateVisBadge(newVis);  setData(visSelect, 'current', newVis); }

            exitEditMode();
        } catch (err: unknown) {
            const msg = err instanceof Error ? err.message : String(err);
            editError.show(`Could not save: ${msg}`);
        } finally {
            saveBtn.disabled = false;
        }
    }

    editBtn.addEventListener('click', enterEditMode);
    cancelBtn.addEventListener('click', cancelEdit);
    saveBtn.addEventListener('click', () => { void saveAll(); });

    document.addEventListener('keydown', e => {
        if (saveBtn.style.display === 'none') return; // not in edit mode
        if (e.key === 'Escape') cancelEdit();
    });
})();
