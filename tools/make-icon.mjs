// Builds Assets/OmeblyExplorer.ico (and PNGs) from the designer's approved SVGs: the simpler small drawing for
// 16-32 px, the full glass drawing for 48 px and up. Needs Chrome and playwright-core (any copy on this PC).
// Usage: node tools/make-icon.mjs <path to playwright-core>
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const DESIGN = 'D:/my latest project/Omebly Designs/Omebly Explorer/1 First look';
const pw = await import(pathToFileURL(join(process.argv[2], 'index.mjs')).href);
const small = readFileSync(join(DESIGN, 'omebly-explorer-icon-small.svg'), 'utf8');
const big = readFileSync(join(DESIGN, 'omebly-explorer-icon.svg'), 'utf8');
const out = join(ROOT, 'src', 'OmeblyExplorer', 'Assets'); mkdirSync(out, { recursive: true });
const browser = await pw.chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
const page = await browser.newPage();
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const pngs = [];
for (const s of sizes) {
  const svg = s <= 32 ? small : big;
  await page.setViewportSize({ width: s, height: s });
  await page.setContent(`<html><body style="margin:0;background:transparent"><div style="width:${s}px;height:${s}px">${svg.replace('<svg', `<svg width="${s}" height="${s}"`)}</div></body></html>`);
  const png = await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: s, height: s } });
  pngs.push([s, png]);
  if ([48, 256].includes(s)) writeFileSync(join(out, `icon-${s}.png`), png);
}
await browser.close();
// ICO file: a header, one directory entry per size, then the PNG images.
const head = Buffer.alloc(6); head.writeUInt16LE(0, 0); head.writeUInt16LE(1, 2); head.writeUInt16LE(pngs.length, 4);
let offset = 6 + 16 * pngs.length;
const dir = [], data = [];
for (const [s, png] of pngs) {
  const e = Buffer.alloc(16);
  e.writeUInt8(s >= 256 ? 0 : s, 0); e.writeUInt8(s >= 256 ? 0 : s, 1); e.writeUInt8(0, 2); e.writeUInt8(0, 3);
  e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6); e.writeUInt32LE(png.length, 8); e.writeUInt32LE(offset, 12);
  offset += png.length; dir.push(e); data.push(png);
}
writeFileSync(join(out, 'OmeblyExplorer.ico'), Buffer.concat([head, ...dir, ...data]));
console.log('icon written', sizes.join(', '));
