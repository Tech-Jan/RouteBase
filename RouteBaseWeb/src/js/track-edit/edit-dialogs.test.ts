import { describe, expect, it, vi, beforeEach } from 'vitest';
import { renderConsequences, openSaveDialog, openDiscardDialog, openRevertFlow } from './edit-dialogs';
import type { SavePreflightResponse, CommitSummaryResponse } from './edit-session-api';
import { EditApiError, getCommitSummary, revertCommit } from './edit-api';

vi.mock('./edit-api', async (importOriginal) => {
	const actual = await importOriginal<typeof import('./edit-api')>();
	return { ...actual, getCommitSummary: vi.fn(), revertCommit: vi.fn() };
});

const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

function preflight(overrides: Partial<SavePreflightResponse> = {}): SavePreflightResponse {
	return {
		mode: 'replace',
		updated: [{ trackId: 1, name: 'Morning Ride' }],
		created: [{ slotId: 1, name: 'Morning Ride (1)' }],
		trashed: [{ trackId: 2, name: 'Lunch loop' }],
		blockedBy: null, // wire always writes the field (null = no blocker) — D1
		...overrides,
	};
}

function buttonByText(text: string): HTMLButtonElement | undefined {
	return [...document.querySelectorAll('button')].find((b) => b.textContent === text) as HTMLButtonElement | undefined;
}

function commitSummary(overrides: Partial<CommitSummaryResponse> = {}): CommitSummaryResponse {
	return {
		commitId: 1, saveMode: 'replace', committedAt: '2026-07-17T00:00:00Z',
		replaced: [], trashed: [], created: [], revertedAt: null, revertible: true, blockedBy: null,
		...overrides,
	};
}

beforeEach(() => {
	document.body.innerHTML = '';
	vi.mocked(getCommitSummary).mockReset();
	vi.mocked(revertCommit).mockReset();
});

describe('edit-dialogs', () => {
	it('renderConsequences lists updated/created/trashed verbatim from the preflight DTO', () => {
		const el = renderConsequences(preflight());
		expect(el.textContent).toContain('update “Morning Ride”');
		expect(el.textContent).toContain('create “Morning Ride (1)”');
		expect(el.textContent).toContain('move “Lunch loop” to trash');
	});

	it('save mode defaults to replace and re-requests preflight on change', async () => {
		const fetchPreflight = vi.fn().mockResolvedValue(preflight());
		openSaveDialog({ fetchPreflight, onConfirm: vi.fn() });
		await tick();
		expect(fetchPreflight).toHaveBeenNthCalledWith(1, 'replace'); // default

		const copy = document.querySelector('input[value="copy"]') as HTMLInputElement;
		copy.checked = true;
		copy.dispatchEvent(new Event('change'));
		await tick();
		expect(fetchPreflight).toHaveBeenCalledWith('copy');
	});

	it('blocked preflight renders error state and disables confirm', async () => {
		const fetchPreflight = vi.fn().mockResolvedValue(preflight({ blockedBy: 'nothingToSave', updated: [], created: [], trashed: [] }));
		openSaveDialog({ fetchPreflight, onConfirm: vi.fn() });
		await tick();
		expect(document.body.textContent).toContain('No changes to save.');
		expect(buttonByText('Save')?.disabled).toBe(true);
	});

	it('discard dialog shows the edit count', () => {
		openDiscardDialog({ editCount: 3, onConfirm: vi.fn() });
		expect(document.body.textContent).toContain('Discard all 3 edits');
	});

	describe('revert dialog — item J (inputsPurged dedicated copy)', () => {
		it('blockedBy=inputsPurged on the pre-check gets its own message, not the generic "supersedes" one', async () => {
			vi.mocked(getCommitSummary).mockResolvedValue(commitSummary({ revertible: false, blockedBy: 'inputsPurged' }));

			await openRevertFlow(1, { trackId: 42 });

			expect(document.body.textContent).toContain('one of its inputs has since been purged and cannot be restored');
			expect(document.body.textContent).not.toContain('a newer edit supersedes it');
		});

		it('another blockedBy reason still gets the generic "supersedes" message', async () => {
			vi.mocked(getCommitSummary).mockResolvedValue(commitSummary({ revertible: false, blockedBy: 'supersededCommit' }));

			await openRevertFlow(1, { trackId: 42 });

			expect(document.body.textContent).toContain('a newer edit supersedes it');
		});

		it('a revert that fails with inputsPurged shows the dedicated message and leaves Revert disabled', async () => {
			vi.mocked(getCommitSummary).mockResolvedValue(commitSummary());
			vi.mocked(revertCommit).mockRejectedValue(new EditApiError(409, 'inputsPurged'));

			await openRevertFlow(1, { trackId: 42 });
			buttonByText('Revert')!.click();
			await tick();

			expect(document.body.textContent).toContain('one of its inputs has since been purged and cannot be restored');
			expect(buttonByText('Revert')!.disabled).toBe(true); // retrying cannot help — stays disabled
		});

		it('a revert that fails for any other reason shows the generic retry message and re-enables Revert', async () => {
			vi.mocked(getCommitSummary).mockResolvedValue(commitSummary());
			vi.mocked(revertCommit).mockRejectedValue(new EditApiError(409, 'staleTrack'));

			await openRevertFlow(1, { trackId: 42 });
			buttonByText('Revert')!.click();
			await tick();

			expect(document.body.textContent).toContain('Revert failed — please try again.');
			expect(buttonByText('Revert')!.disabled).toBe(false);
		});
	});
});
