/**
 * track-edit-dom.ts — shared pure DOM factories for the track-edit dialogs/banners/toasts (R-10). No fetch,
 * no state — just element construction, extracted from the copies that lived in edit-dialogs / add-track-dialog
 * / append-dialog / entry-picker / edit-mode-controller. All user text goes through textContent by construction.
 */

/** A mounted dialog/overlay handle: its root element + a close() that removes it. */
export interface DialogHandle {
	el: HTMLElement;
	close(): void;
}

/** `document.createElement(tag)` with an optional className. */
export function el(tag: string, className?: string): HTMLElement {
	const node = document.createElement(tag);
	if (className) node.className = className;
	return node;
}

/** A cockpit-styled action button (`btn-cockpit <variant> btn-cockpit-sm`). */
export function actionButton(label: string, variant: string): HTMLButtonElement {
	const btn = document.createElement('button');
	btn.type = 'button';
	btn.className = `btn-cockpit ${variant} btn-cockpit-sm`;
	btn.textContent = label;
	return btn;
}

export interface RadioHandle {
	label: HTMLLabelElement;
	input: HTMLInputElement;
}

/** A labelled radio input. `labelText` is appended as a text node (space-prefixed); pass '' for the
 * append-dialog's "fill the label text later" pattern. `labelClass` picks the per-dialog CSS class. */
export function radio(
	name: string, value: string, checked: boolean,
	labelText = '', labelClass = 'track-edit-dialog-mode',
): RadioHandle {
	const label = document.createElement('label');
	label.className = labelClass;
	const input = document.createElement('input');
	input.type = 'radio';
	input.name = name;
	input.value = value;
	input.checked = checked;
	label.append(input, document.createTextNode(labelText ? ` ${labelText}` : ''));
	return { label, input };
}

export interface BuiltDialog {
	backdrop: HTMLElement;
	body: HTMLElement;
	close(): void;
	/**
	 * Set the action Escape performs — it must match what the dialog's Cancel/Keep button does, since some
	 * dialogs run cancel-only cleanup (e.g. the save dialog clears a re-entrancy guard in its onCancel).
	 * Defaults to close(); callers with extra cancel work pass the SAME handler their button uses.
	 */
	onEscape(fn: () => void): void;
}

let dialogTitleSeq = 0;

/**
 * Backdrop + dialog + `<h3>` title, mounted on document.body. `extraClass` adds a per-dialog modifier.
 *
 * Ships the modal a11y affordances so all six track-edit dialogs get them at once (they had none):
 * role="dialog"/aria-modal + aria-labelledby on the title, focus moved into the dialog on open and
 * restored to the trigger on close, and Escape-to-dismiss (see onEscape — it must run the caller's
 * cancel path, not a bare close, or a cancel-only guard is skipped).
 */
export function buildDialog(title: string, extraClass?: string): BuiltDialog {
	const opener = document.activeElement as HTMLElement | null;
	const backdrop = el('div', 'track-edit-dialog-backdrop');
	const dialog = el('div', extraClass ? `track-edit-dialog ${extraClass}` : 'track-edit-dialog');
	dialog.setAttribute('role', 'dialog');
	dialog.setAttribute('aria-modal', 'true');
	dialog.tabIndex = -1;
	const heading = el('h3');
	const headingId = `track-edit-dialog-title-${++dialogTitleSeq}`;
	heading.id = headingId;
	heading.textContent = title;
	dialog.setAttribute('aria-labelledby', headingId);
	dialog.appendChild(heading);
	backdrop.appendChild(dialog);
	document.body.appendChild(backdrop);
	dialog.focus();

	function close(): void {
		document.removeEventListener('keydown', onKeydown, true);
		backdrop.remove();
		// Restore focus to whatever opened the dialog, so a keyboard user isn't dumped to <body>.
		if (opener?.isConnected) opener.focus();
	}
	let dismiss = close;
	// Capture + stopPropagation so Escape dismisses THIS dialog and no page-level Escape (the toolbar's
	// no-op handler, a replay session's exit) also fires behind it.
	function onKeydown(e: KeyboardEvent): void {
		if (e.key === 'Escape') { e.stopPropagation(); e.preventDefault(); dismiss(); }
	}
	document.addEventListener('keydown', onKeydown, true);

	return { backdrop, body: dialog, close, onEscape(fn: () => void): void { dismiss = fn; } };
}

/** A `.track-edit-dialog-actions` row of buttons. */
export function actionsRow(...buttons: HTMLButtonElement[]): HTMLElement {
	const row = el('div', 'track-edit-dialog-actions');
	row.append(...buttons);
	return row;
}

export interface ToastOptions {
	className?: string;
	ms?: number;
	role?: string;
}

/** A transient toast that removes itself after `ms`. Defaults to the controller's track-edit-toast/4000; the
 * entry picker passes its own class/duration/role via the options bag. */
export function showToast(message: string, opts: ToastOptions = {}): void {
	const toast = el('div', opts.className ?? 'track-edit-toast');
	if (opts.role) toast.setAttribute('role', opts.role);
	toast.textContent = message;
	document.body.appendChild(toast);
	setTimeout(() => toast.remove(), opts.ms ?? 4000);
}
