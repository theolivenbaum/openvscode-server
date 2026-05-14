#!/usr/bin/env node
// Trim the openvscode-server build for a pure C# / BYO-language-server
// environment, by:
//   1. Appending unneeded local extensions to `excludedExtensions` in
//      build/lib/extensions.ts so compileNonNativeExtensionsBuildTask /
//      compileNativeExtensionsBuildTask skip them.
//   2. Clearing `builtInExtensions` in product.json so the marketplace
//      .vsix download step is a no-op.
//   3. Swapping the reh-web compile step from compileBuildWithManglingTask
//      to compileBuildWithoutManglingTask. The mangler holds a full
//      symbol-rename map in memory while the TS compiler is also active,
//      which is the main source of OOM/heavy-swap during compile-src on
//      hosted 7 GiB CI agents.
//
// All three files are first restored from HEAD, so the script is idempotent.
// Wired into scripts/build-vscode-release.{sh,ps1} behind the
// VSCODE_MINIMAL_BUILD=1 env var.

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');

const EXTENSIONS_TS = join(repoRoot, 'build', 'lib', 'extensions.ts');
const PRODUCT_JSON  = join(repoRoot, 'product.json');
const GULPFILE_REH  = join(repoRoot, 'build', 'gulpfile.reh.ts');

// Local extensions (under extensions/) to drop. Kept:
//   configuration-editing, json-language-features, markdown-language-features,
//   media-preview, merge-conflict, references-view, search-result,
//   csharp, razor, json, log, markdown-basics, shellscript, diff, ini, xml,
//   yaml,
//   all theme-* packs.
const DROP_LOCAL = [
	// Rich (esbuild-built) extensions not needed for a C# environment.
	'css-language-features',
	'emmet',
	'extension-editing',
	'grunt',
	'gulp',
	'html-language-features',
	'jake',
	'markdown-math',
	'mermaid-chat-features',
	'npm',
	'php-language-features',
	'simple-browser',
	'terminal-suggest',
	'tunnel-forwarding',
	'typescript-language-features',

	// JS-only extensions (no esbuild) not needed for a C# environment.
	// Note: dropping `git` and `github` is what makes it safe to also drop
	// `git-base`, which only those two extensions depend on.
	'debug-auto-launch',
	'debug-server-ready',
	'git',
	'git-base',
	'github',
	'github-authentication',
	'ipynb',
	'microsoft-authentication',
	'notebook-renderers',

	// Language "basics" packs (TextMate grammars + snippets) for languages
	// not used. Kept: csharp, razor, json, log, markdown-basics, shellscript,
	// diff, ini, xml, yaml.
	'bat',
	'clojure',
	'coffeescript',
	'cpp',
	'css',
	'dart',
	'docker',
	'dotenv',
	'fsharp',
	'go',
	'groovy',
	'handlebars',
	'hlsl',
	'html',
	'java',
	'javascript',
	'julia',
	'latex',
	'less',
	'lua',
	'make',
	'objective-c',
	'perl',
	'php',
	'powershell',
	'prompt-basics',
	'pug',
	'python',
	'r',
	'restructuredtext',
	'ruby',
	'rust',
	'scss',
	'shaderlab',
	'sql',
	'swift',
	'typescript-basics',
	'vb'
];

// Restore the patched files from git so the patch always runs against a
// known baseline. Re-running the script is a no-op.
execFileSync(
	'git',
	['checkout', 'HEAD', '--',
		relative(repoRoot, EXTENSIONS_TS),
		relative(repoRoot, PRODUCT_JSON),
		relative(repoRoot, GULPFILE_REH)],
	{ cwd: repoRoot, stdio: 'inherit' }
);

// --- build/lib/extensions.ts ------------------------------------------------
let ts = readFileSync(EXTENSIONS_TS, 'utf8');
const marker = 'const excludedExtensions = [';
if (!ts.includes(marker)) {
	throw new Error(`Cannot find '${marker}' in ${EXTENSIONS_TS}`);
}
const additions = DROP_LOCAL.map(n => `\t'${n}',`).join('\n');
ts = ts.replace(marker, `${marker}\n${additions}`);
writeFileSync(EXTENSIONS_TS, ts);

// --- product.json -----------------------------------------------------------
// Preserve key order and tab indentation by parsing/serialising round-trip.
const product = JSON.parse(readFileSync(PRODUCT_JSON, 'utf8'));
product.builtInExtensions = [];
writeFileSync(PRODUCT_JSON, JSON.stringify(product, null, '\t') + '\n');

// --- build/gulpfile.reh.ts --------------------------------------------------
// Swap compileBuildWithManglingTask -> compileBuildWithoutManglingTask. The
// mangler is what spikes memory during compile-src on small CI agents.
let reh = readFileSync(GULPFILE_REH, 'utf8');
const manglerReplacements = [
	{
		from: `import { compileBuildWithManglingTask } from './gulpfile.compile.ts';`,
		to: `import { compileBuildWithoutManglingTask as compileBuildWithManglingTask } from './gulpfile.compile.ts';`
	}
];
for (const { from, to } of manglerReplacements) {
	if (!reh.includes(from)) {
		throw new Error(`Cannot find expected text in ${GULPFILE_REH}:\n  ${from}`);
	}
	reh = reh.replace(from, to);
}
writeFileSync(GULPFILE_REH, reh);

console.log(
	`==> Minimal build: excluded ${DROP_LOCAL.length} local extensions, ` +
	`cleared product.json builtInExtensions, and disabled the mangler ` +
	`for compile-src.`
);
