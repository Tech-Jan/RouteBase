/* stylelint config — CSS lint for wwwroot/css/*.css (run via `npm run lint:css`).
 *
 * Deliberately MINIMAL: it does NOT extend stylelint-config-standard, so there is
 * zero pure-formatting noise. Only the handful of rules that would have caught the
 * debt the CSS-system cleanup removed are enabled:
 *   - no-duplicate-selectors ............ the "10 near-identical card copies" class
 *   - declaration-block-no-duplicate-properties ... a property set twice in one block
 *   - declaration-block-no-shorthand-property-overrides ... `margin-left; margin:0` wipes
 *   - property-no-unknown ............... typo'd / invalid properties
 * This is a GREEN BASELINE to ratchet UP from — add rules over time; do not relax
 * these to silence a finding (fix the CSS instead).
 *
 * NOTE: authored without running stylelint. Run `npm install`, then `npm run lint:css` to
 * confirm the baseline is green. If no-duplicate-selectors flags legitimate @media
 * overrides on this tree, that is the first rule to tune (e.g. via its options).
 */
module.exports = {
	rules: {
		'no-duplicate-selectors': true,
		'declaration-block-no-duplicate-properties': [
			true,
			{ ignore: ['consecutive-duplicates-with-different-values'] },
		],
		'declaration-block-no-shorthand-property-overrides': true,
		'property-no-unknown': true,
	},
};
