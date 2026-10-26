// Analyzer dragnet, TS half — INVENTORY config, deliberately separate from
// eslint.config.mjs so `npm run lint` (gate 4) is untouched until families are adopted.
// Run: npx eslint -c eslint.dragnet.config.mjs src/js
// Adoption = move the family into eslint.config.mjs at "error" with sites fixed; this file
// only ever measures.
import tsparser from "@typescript-eslint/parser";
import sonarjs from "eslint-plugin-sonarjs";
import unicorn from "eslint-plugin-unicorn";

export default [
	{
		files: ["src/js/**/*.ts"],
		ignores: ["src/js/generated/**"],
		languageOptions: {
			parser: tsparser,
			parserOptions: { project: "./tsconfig.json", sourceType: "module" },
		},
		plugins: { sonarjs, unicorn },
		rules: {
			...sonarjs.configs.recommended.rules,
			// unicorn: curated correctness subset only — the full preset is style-opinionated
			// (prevent-abbreviations etc.) and would drown the inventory.
			"unicorn/no-array-callback-reference": "warn",
			"unicorn/no-await-expression-member": "warn",
			"unicorn/no-invalid-remove-event-listener": "warn",
			"unicorn/no-thenable": "warn",
			"unicorn/no-useless-promise-resolve-reject": "warn",
			"unicorn/prefer-add-event-listener": "warn",
			"unicorn/require-array-join-separator": "warn",
			"unicorn/no-instanceof-builtins": "warn",
			"unicorn/consistent-function-scoping": "warn",
		},
	},
];
