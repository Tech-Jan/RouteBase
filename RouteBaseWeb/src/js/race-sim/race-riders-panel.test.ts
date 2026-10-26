import { beforeEach, describe, expect, it, vi } from 'vitest';
import { initRaceRidersPanel } from './race-riders-panel';

/**
 * The pacing reference — the real effort the simulated field copies its terrain response from.
 *
 * The server side of this shipped complete and UNREACHABLE: the column, the FK, PUT /api/RacesApi/{id},
 * SetReferenceEffortAsync and the whole cost-curve cache-key rework existed, and no client ever called
 * them. Which meant the problem it was built for stayed unsolvable — a MERGED course (what /Races tells
 * you to build for a multi-day route) has no usable timestamps, so nothing could be fitted and the field
 * silently fell back to a generic mountain-bike curve, pacing a relay's ski, canoe and kayak legs with
 * bike physics.
 */
function mount(referenceValue = '', riderName = 'Morning ride'): { reloaded: () => number } {
	document.body.innerHTML = `
		<section id="raceRiders" data-race-id="1">
			<ul class="race-riders">
				<li style="--rider-color: #ffd43b">
					<input class="race-rider-color" type="color" value="#ffd43b" data-participant-id="7" />
					<form class="race-rider-rename" data-participant-id="7">
						<input class="race-input race-rider-name" value="${riderName}" />
						<button type="submit">save</button>
					</form>
					<button class="race-rider-callout" data-participant-id="7" aria-pressed="false"><i class="bi bi-eye"></i></button>
					<button class="race-rider-remove" data-participant-id="7">x</button>
				</li>
				<li style="--rider-color: #f06595">
					<input class="race-rider-color" type="color" value="#f06595" data-participant-id="9" />
					<form class="race-rider-rename" data-participant-id="9">
						<input class="race-input race-rider-name" value="Evening ride" />
						<button type="submit">save</button>
					</form>
					<button class="race-rider-callout" data-participant-id="9" aria-pressed="true"><i class="bi bi-eye-slash"></i></button>
					<button class="race-rider-remove" data-participant-id="9">x</button>
				</li>
			</ul>
			<form id="raceRiderForm">
				<input id="raceRiderFilter" />
				<select id="raceRiderActivity"><option value="">Choose…</option></select>
			</form>
			<form id="raceReferenceForm">
				<input id="raceReferenceFilter" />
				<select id="raceReferenceActivity">
					<option value="">The course ride itself (default)</option>
					<option value="42">Ski to Sea — my leg</option>
					<option value="77">Another ride</option>
				</select>
			</form>
			<p id="raceRiderStatus"></p>
		</section>`;

	const select = document.getElementById('raceReferenceActivity') as HTMLSelectElement;
	select.value = referenceValue;

	let reloads = 0;
	initRaceRidersPanel(1, () => { reloads += 1; });
	return { reloaded: () => reloads };
}

function submitReference(): void {
	document.getElementById('raceReferenceForm')!.dispatchEvent(
		new Event('submit', { bubbles: true, cancelable: true }));
}

function submitRename(): void {
	document.querySelector('.race-rider-rename')!.dispatchEvent(
		new Event('submit', { bubbles: true, cancelable: true }));
}

function nameBoxes(): HTMLInputElement[] {
	return [...document.querySelectorAll<HTMLInputElement>('.race-rider-name')];
}

function colorInputs(): HTMLInputElement[] {
	return [...document.querySelectorAll<HTMLInputElement>('.race-rider-color')];
}

function calloutButtons(): HTMLButtonElement[] {
	return [...document.querySelectorAll<HTMLButtonElement>('.race-rider-callout')];
}

/** What the rename endpoint answers with: the row, carrying the name the SERVER resolved. */
function rowResponse(name: string | null): Response {
	return new Response(
		JSON.stringify({ participantId: 7, kind: 'real', name, bib: null, trackId: 3 }),
		{ status: 200, headers: { 'Content-Type': 'application/json' } });
}

describe('the pacing-reference picker', () => {
	beforeEach(() => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 200 })));
	});

	it('points the race at the chosen effort', async () => {
		const { reloaded } = mount('42');

		submitReference();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1', expect.objectContaining({
			method: 'PUT',
			body: JSON.stringify({ referenceTrackId: 42 }),
		}));
	});

	it('sends null for "the course ride itself", which is what the server means by no reference', async () => {
		const { reloaded } = mount('');

		submitReference();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1', expect.objectContaining({
			body: JSON.stringify({ referenceTrackId: null }),
		}));
	});

	it('reloads, because a new reference re-paces the whole field', async () => {
		const { reloaded } = mount('77');

		submitReference();

		// The curve is refitted from a different ride, so every rider's pacing changes — the payload has
		// to be fetched again rather than patched in place.
		await vi.waitFor(() => expect(reloaded()).toBe(1));
	});

	it('reports DONE, because nothing else ever clears the pending message', async () => {
		// The panel refreshes IN PLACE. The pending line used to be wiped by the document reload; without
		// a terminal message the last thing the user sees is "Setting the pacing reference…", which is
		// indistinguishable from a request that hung.
		const { reloaded } = mount('42');

		submitReference();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		await vi.waitFor(() =>
			expect(document.getElementById('raceRiderStatus')!.textContent)
				.toBe('Pacing reference saved.'));
	});

	it('says the field fell back to the course ride when the reference is cleared', async () => {
		const { reloaded } = mount('');

		submitReference();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		await vi.waitFor(() =>
			expect(document.getElementById('raceRiderStatus')!.textContent)
				.toBe('Pacing reference cleared — falling back to the course ride.'));
	});

	it('says what the server said when it refuses, instead of "failed"', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 403 })));
		mount('42');

		submitReference();

		await vi.waitFor(() =>
			expect(document.getElementById('raceRiderStatus')!.textContent)
				.toBe('You cannot see that ride.'));
	});
});

/**
 * Naming a rider after the PERSON, not after the ride file he came from.
 *
 * `display_name` existed end to end — column, entity, request DTO, and a `?? activity.TrackName`
 * fallback — and no client ever set it, so the fallback always fired and the field read "Morning ride".
 */
describe('renaming a rider', () => {
	beforeEach(() => {
		vi.stubGlobal('fetch', vi.fn(async () => rowResponse('Marco')));
	});

	it('sends the typed name to the rider it belongs to', async () => {
		const { reloaded } = mount('', 'Marco');

		submitRename();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1/participants/name', expect.objectContaining({
			method: 'PUT',
			body: JSON.stringify({ participantId: 7, displayName: 'Marco' }),
		}));
	});

	it('sends an EMPTY name rather than resolving the fallback itself', async () => {
		// "Blank means the ride's own name" is the server's rule. A client that substituted the activity
		// name here would be a second copy of that rule, free to drift from it.
		const { reloaded } = mount('', '');

		submitRename();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1/participants/name', expect.objectContaining({
			body: JSON.stringify({ participantId: 7, displayName: '' }),
		}));
	});

	it('refreshes the replay, because the name is on the map, the board and the speed legend', async () => {
		const { reloaded } = mount('', 'Marco');

		submitRename();

		await vi.waitFor(() => expect(reloaded()).toBe(1));
	});

	it('shows the name the SERVER resolved, not the one that was typed', async () => {
		// Clearing the box means "the ride's own name again", and only the server knows what that is.
		vi.stubGlobal('fetch', vi.fn(async () => rowResponse('Morning ride')));
		const { reloaded } = mount('', '');

		submitRename();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(nameBoxes()[0]!.value).toBe('Morning ride');
	});

	it('leaves a simulated rider\'s box empty when the server resolved no name', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => rowResponse(null)));
		const { reloaded } = mount('', 'Marco');

		submitRename();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(nameBoxes()[0]!.value).toBe('');
	});

	it('does not disturb the OTHER riders\' boxes — they may hold unsaved edits', async () => {
		// The reason this panel refreshes in place instead of reloading the document. Every row is an
		// editable box; saving one rider must not throw away what was typed into the others.
		const { reloaded } = mount('', 'Marco');
		const [, second] = nameBoxes();
		second!.value = 'Sarah, typed but not yet saved';

		submitRename();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(nameBoxes()[1]!.value).toBe('Sarah, typed but not yet saved');
		expect(fetch).toHaveBeenCalledTimes(1);
	});

	it('reads the message out of the ApiError body instead of showing raw JSON', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response(
			JSON.stringify({ code: 'bad_request', message: "A rider's name must be 255 characters or fewer." }),
			{ status: 400 })));
		mount('', 'Marco');

		submitRename();

		await vi.waitFor(() =>
			expect(document.getElementById('raceRiderStatus')!.textContent)
				.toBe("A rider's name must be 255 characters or fewer."));
	});
});

/**
 * Painting one rider from his own pill.
 *
 * Two real riders used to come out identically gold — colour was a function of KIND — which is
 * unreadable exactly where it matters: two solid gold lines on the speed chart. They are handed distinct
 * colours on add now; this is the control that disagrees with that choice.
 */
describe('painting one rider', () => {
	beforeEach(() => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response(
			JSON.stringify({ painted: 1 }), { status: 200, headers: { 'Content-Type': 'application/json' } })));
	});

	it('paints THAT rider through the group endpoint, with a one-id list', async () => {
		// Not its own endpoint: a single rider is not a special case of painting, and a second route would
		// be a second copy of the colour validation and the edit gate.
		const { reloaded } = mount();
		const swatch = colorInputs()[1]!;
		swatch.value = '#63e6be';

		swatch.dispatchEvent(new Event('change', { bubbles: true }));
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1/participants/color', expect.objectContaining({
			method: 'PUT',
			body: JSON.stringify({ participantIds: [9], color: '#63e6be' }),
		}));
	});

	it('recolours the pill, which the in-place refresh does not rebuild', async () => {
		const { reloaded } = mount();
		const swatch = colorInputs()[0]!;
		swatch.value = '#ff922b';

		swatch.dispatchEvent(new Event('change', { bubbles: true }));
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(swatch.closest('li')!.style.getPropertyValue('--rider-color')).toBe('#ff922b');
	});

	it('says what the server said when the paint is refused', async () => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response(
			JSON.stringify({ code: 'bad_request', message: 'Not a #rrggbb colour.' }), { status: 400 })));
		mount();

		colorInputs()[0]!.dispatchEvent(new Event('change', { bubbles: true }));

		await vi.waitFor(() =>
			expect(document.getElementById('raceRiderStatus')!.textContent).toBe('Not a #rrggbb colour.'));
	});
});

/**
 * Hiding a rider's call-out.
 *
 * A real rider earns a name label, a pinned sidebar row and a speed-chart line by BEING real, so before
 * this switch existed the only way to get a second rider off the chart was to delete him from the race.
 */
describe('hiding a rider\'s highlight', () => {
	beforeEach(() => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response(
			JSON.stringify({ updated: 1 }), { status: 200, headers: { 'Content-Type': 'application/json' } })));
	});

	it('hides a rider who is currently called out', async () => {
		const { reloaded } = mount();

		calloutButtons()[0]!.click();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1/participants/callout-off', expect.objectContaining({
			method: 'PUT',
			body: JSON.stringify({ participantIds: [7], calloutOff: true }),
		}));
	});

	it('restores one who is already hidden — the same button, the other way', async () => {
		const { reloaded } = mount();

		calloutButtons()[1]!.click();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1/participants/callout-off', expect.objectContaining({
			body: JSON.stringify({ participantIds: [9], calloutOff: false }),
		}));
	});

	it('carries its own pressed state, because the refresh rebuilds the replay and not this panel', async () => {
		const { reloaded } = mount();
		const button = calloutButtons()[0]!;

		button.click();
		await vi.waitFor(() => expect(reloaded()).toBe(1));

		expect(button.getAttribute('aria-pressed')).toBe('true');
		expect(button.querySelector('i')!.classList.contains('bi-eye-slash')).toBe(true);
		expect(button.closest('li')!.classList.contains('is-callout-off')).toBe(true);
	});

	it('leaves the button alone when the server refuses', async () => {
		// A pressed button over a rider the server never hid is a lie about persisted state.
		vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 403 })));
		mount();
		const button = calloutButtons()[0]!;

		button.click();

		await vi.waitFor(() =>
			expect(document.getElementById('raceRiderStatus')!.textContent).toBe('You cannot see that ride.'));
		expect(button.getAttribute('aria-pressed')).toBe('false');
	});
});
