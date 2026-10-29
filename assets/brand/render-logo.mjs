// Renders the KASPER pixel bunny from kasper-logo.grid.txt.
//   node render-logo.mjs            -> writes kasper-logo.svg and prints the terminal version
//   node render-logo.mjs --html DIR -> also writes DIR/preview.html (dark + light)
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const colors = { B: "#0D0D0D", G: "#F5B800", D: "#B37F00", W: "#FFF6D6" };

const rows = fs
  .readFileSync(path.join(here, "kasper-logo.grid.txt"), "utf8")
  .split(/\r?\n/)
  .filter((l) => l && !l.startsWith("#"));
const width = rows[0].length;
rows.forEach((r, i) => {
  if (r.length !== width) throw new Error(`row ${i} has ${r.length} pixels, expected ${width}`);
});

const rects = rows
  .flatMap((r, y) =>
    [...r].map((ch, x) =>
      colors[ch] ? `<rect x="${x}" y="${y}" width="1" height="1" fill="${colors[ch]}"/>` : ""
    )
  )
  .filter(Boolean)
  .join("\n");
const svg = (w, h) =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${rows.length}" width="${w}" height="${h}" shape-rendering="crispEdges">\n<title>KASPER</title>\n${rects}\n</svg>\n`;

fs.writeFileSync(path.join(here, "kasper-logo.svg"), svg(width * 16, rows.length * 16));

// Terminal version: two pixel rows per text line using the upper-half block (▀),
// foreground = top pixel, background = bottom pixel. Truecolor ANSI works in
// Windows Terminal, cmd and PowerShell on Windows 10+.
const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16)).join(";");
const lines = [];
for (let y = 0; y < rows.length; y += 2) {
  let line = "";
  for (let x = 0; x < width; x++) {
    const top = colors[rows[y][x]];
    const bottom = y + 1 < rows.length ? colors[rows[y + 1][x]] : undefined;
    if (!top && !bottom) line += "\x1b[0m ";
    else if (top && bottom) line += `\x1b[38;2;${rgb(top)};48;2;${rgb(bottom)}m▀`;
    else if (top) line += `\x1b[0;38;2;${rgb(top)}m▀`;
    else line += `\x1b[0;38;2;${rgb(bottom)}m▄`;
  }
  lines.push(line + "\x1b[0m");
}
fs.writeFileSync(path.join(here, "kasper-logo.ansi"), lines.join("\n") + "\n");
console.log(lines.join("\n"));

// Windows icon: 32-bit BMP entries, square canvas (grid padded to 16x16), nearest-neighbour scaling.
const side = Math.max(width, rows.length);
const padTop = Math.floor((side - rows.length) / 2);
const pixelAt = (x, y) => {
  const gy = y - padTop;
  if (gy < 0 || gy >= rows.length || x >= width) return null;
  return colors[rows[gy][x]] ?? null;
};
const sizes = [16, 24, 32, 48, 64, 128, 256].filter((s) => s % side === 0 || s === 24);
const images = sizes.map((size) => {
  const andRow = Math.ceil(size / 32) * 4;
  const buf = Buffer.alloc(40 + size * size * 4 + andRow * size);
  buf.writeUInt32LE(40, 0);
  buf.writeInt32LE(size, 4);
  buf.writeInt32LE(size * 2, 8);
  buf.writeUInt16LE(1, 12);
  buf.writeUInt16LE(32, 14);
  let o = 40;
  for (let y = size - 1; y >= 0; y--) {
    for (let x = 0; x < size; x++) {
      const hex = pixelAt(Math.floor((x * side) / size), Math.floor((y * side) / size));
      if (hex) {
        buf[o] = parseInt(hex.slice(5, 7), 16);
        buf[o + 1] = parseInt(hex.slice(3, 5), 16);
        buf[o + 2] = parseInt(hex.slice(1, 3), 16);
        buf[o + 3] = 255;
      }
      o += 4;
    }
  }
  return { size, buf };
});
const header = Buffer.alloc(6 + 16 * images.length);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(images.length, 4);
let offset = header.length;
images.forEach(({ size, buf }, i) => {
  const e = 6 + 16 * i;
  header[e] = size === 256 ? 0 : size;
  header[e + 1] = size === 256 ? 0 : size;
  header.writeUInt16LE(1, e + 4);
  header.writeUInt16LE(32, e + 6);
  header.writeUInt32LE(buf.length, e + 8);
  header.writeUInt32LE(offset, e + 12);
  offset += buf.length;
});
fs.writeFileSync(path.join(here, "kasper.ico"), Buffer.concat([header, ...images.map((i) => i.buf)]));

const htmlIdx = process.argv.indexOf("--html");
if (htmlIdx > -1) {
  const dir = process.argv[htmlIdx + 1];
  const tile = (bg, fg) =>
    `<div style="background:${bg};display:flex;gap:30px;align-items:center;padding:24px">${svg(192, 180)}<div style="font:700 48px monospace;color:${fg};letter-spacing:8px">KASPER</div>${svg(32, 30)}</div>`;
  fs.writeFileSync(
    path.join(dir, "preview.html"),
    `<html><body style="margin:0">${tile("#0D0D0D", "#F5B800")}${tile("#FFFBEA", "#0D0D0D")}</body></html>`
  );
}
