/**
 * Owner/rider combo keyword tokens — the wire values for the Me/Others/Everyone checkboxes shared by
 * the Activities Owner column and the efforts Rider column. Must stay in sync with the server's
 * `OwnerFilterResolver` (RouteBaseWeb/Helpers/Grid/OwnerFilterResolver.cs).
 */

export const OWNER_ME_TOKEN = '__me__';
export const OWNER_OTHERS_TOKEN = '__others__';
export const OWNER_EVERYONE_TOKEN = '__everyone__';

export const OWNER_KEYWORD_OPTIONS = [
	{ value: OWNER_ME_TOKEN, label: 'Me' },
	{ value: OWNER_OTHERS_TOKEN, label: 'Others' },
	{ value: OWNER_EVERYONE_TOKEN, label: 'Everyone' },
];
