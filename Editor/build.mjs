import { build } from 'esbuild';
import { copyFile, mkdir, writeFile } from 'node:fs/promises';
const shared = { bundle: true, minify: true, target: 'es2022', loader: { '.ttf': 'file' }, assetNames: '[name]-[hash]', logLevel: 'info' };
await mkdir('dist', { recursive: true });
await build({ ...shared, entryPoints: ['editor.js'], outfile: 'dist/editor.js', format: 'iife' });
for (const [name, entry] of Object.entries({
  editor: 'editor/editor.worker.js', json: 'language/json/json.worker.js',
  css: 'language/css/css.worker.js', html: 'language/html/html.worker.js', typescript: 'language/typescript/ts.worker.js'
})) await build({ ...shared, entryPoints: [`node_modules/monaco-editor/esm/vs/${entry}`], outfile: `dist/${name}.worker.js`, format: 'iife' });
await copyFile('index.html', 'dist/index.html');
for (const [name, source] of Object.entries({ 'LICENSE.monaco.txt': 'node_modules/monaco-editor/LICENSE', 'LICENSE.dompurify.txt': 'node_modules/dompurify/LICENSE', 'LICENSE.marked.txt': 'node_modules/marked/LICENSE.md' })) await copyFile(source, `dist/${name}`);
await writeFile('dist/.build-stamp', new Date().toISOString());
