#!/usr/bin/env node
// Recadre les captures de la CI en figures des guides, d'après figures.json : coin haut gauche, dimensions fixes.
// Sans dépendance (zlib de Node). Les outils qui recadrent au centre (sips) coupent la barre du haut : ne pas les utiliser.
//
//   node crop_figures.js --captures <dossier>... [--out <dossier>] [--only <figure>...]
//
// Chaque figure déclare la fin de chemin de sa capture source ("source") ; la première capture trouvée sous les dossiers
// indiqués est recadrée aux "pixels" déclarés et écrite sous son nom dans --out (défaut : images/). Les figures sans
// source, ou dont la capture est absente des dossiers fournis, sont listées et laissées telles quelles.
'use strict';
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const SIGNATURE = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const CHANNELS = { 0: 1, 2: 3, 4: 2, 6: 4 };

function chunks(buffer) {
  if (!buffer.subarray(0, 8).equals(SIGNATURE)) throw new Error('signature PNG absente');
  const out = []; let offset = 8;
  while (offset + 12 <= buffer.length) {
    const length = buffer.readUInt32BE(offset); const type = buffer.toString('ascii', offset + 4, offset + 8);
    out.push({ type, data: buffer.subarray(offset + 8, offset + 8 + length) }); offset += 12 + length;
    if (type === 'IEND') break;
  }
  return out;
}

function decode(buffer) {
  const list = chunks(buffer); const header = list.find(c => c.type === 'IHDR');
  if (!header) throw new Error('IHDR absent');
  const width = header.data.readUInt32BE(0), height = header.data.readUInt32BE(4);
  const depth = header.data[8], color = header.data[9], interlace = header.data[12];
  const channels = CHANNELS[color];
  if (depth !== 8 || !channels || interlace !== 0) throw new Error('PNG 8 bits non entrelacé attendu (gris, RVB ou alpha)');
  const raw = zlib.inflateSync(Buffer.concat(list.filter(c => c.type === 'IDAT').map(c => c.data)));
  const stride = width * channels, pixels = Buffer.alloc(stride * height);
  let previous = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)]; const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    const row = pixels.subarray(y * stride, (y + 1) * stride);
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? row[i - channels] : 0, b = previous[i], c = i >= channels ? previous[i - channels] : 0;
      let predictor = 0;
      if (filter === 1) predictor = a; else if (filter === 2) predictor = b; else if (filter === 3) predictor = (a + b) >> 1;
      else if (filter === 4) { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
      else if (filter !== 0) throw new Error(`filtre PNG inconnu : ${filter}`);
      row[i] = (line[i] + predictor) & 255;
    }
    previous = row;
  }
  return { width, height, channels, color, pixels };
}

function crc32(buffer) {
  let crc = ~0;
  for (const byte of buffer) { crc ^= byte; for (let k = 0; k < 8; k++) crc = crc & 1 ? (crc >>> 1) ^ 0xEDB88320 : crc >>> 1; }
  return ~crc >>> 0;
}

function chunk(type, data) {
  const length = Buffer.alloc(4); length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]); const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

// Filtre choisi ligne par ligne par la somme des écarts, comme libpng ; sortie compacte et sans perte.
function encode(image, x0, y0, width, height) {
  const { channels, pixels } = image; const stride = width * channels, sourceStride = image.width * channels;
  const raw = Buffer.alloc((stride + 1) * height);
  let previous = Buffer.alloc(stride);
  const candidates = [0, 1, 2, 3, 4].map(() => Buffer.alloc(stride));
  for (let y = 0; y < height; y++) {
    const row = pixels.subarray((y0 + y) * sourceStride + x0 * channels, (y0 + y) * sourceStride + (x0 + width) * channels);
    let best = 0, bestSum = Infinity;
    for (let filter = 0; filter < 5; filter++) {
      const out = candidates[filter]; let sum = 0;
      for (let i = 0; i < stride; i++) {
        const a = i >= channels ? row[i - channels] : 0, b = previous[i], c = i >= channels ? previous[i - channels] : 0;
        let predictor = 0;
        if (filter === 1) predictor = a; else if (filter === 2) predictor = b; else if (filter === 3) predictor = (a + b) >> 1;
        else if (filter === 4) { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
        const value = (row[i] - predictor) & 255; out[i] = value; sum += value < 128 ? value : 256 - value;
      }
      if (sum < bestSum) { bestSum = sum; best = filter; }
    }
    raw[y * (stride + 1)] = best; candidates[best].copy(raw, y * (stride + 1) + 1); previous = row;
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4);
  header[8] = 8; header[9] = image.color; header[10] = 0; header[11] = 0; header[12] = 0;
  return Buffer.concat([SIGNATURE, chunk('IHDR', header), chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

function cropFile(source, destination, [width, height]) {
  const image = decode(fs.readFileSync(source));
  if (image.width < width || image.height < height) throw new Error(`${source} : ${image.width}×${image.height}, plus petite que la figure ${width}×${height}`);
  fs.writeFileSync(destination, encode(image, 0, 0, width, height));
}

function findCapture(roots, suffix) {
  const wanted = suffix.split('/');
  const stack = [...roots];
  while (stack.length) {
    const directory = stack.pop();
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) { if (!['node_modules', '.git'].includes(entry.name)) stack.push(full); continue; }
      const parts = full.split(path.sep);
      if (parts.length >= wanted.length && wanted.every((part, i) => parts[parts.length - wanted.length + i] === part)) return full;
    }
  }
  return null;
}

function main() {
  const args = process.argv.slice(2); const captures = [], only = []; let out = path.join(__dirname, 'images'); let mode = null;
  for (const arg of args) {
    if (arg === '--captures' || arg === '--only' || arg === '--out') { mode = arg; continue; }
    if (mode === '--captures') captures.push(path.resolve(arg)); else if (mode === '--only') only.push(arg); else if (mode === '--out') out = path.resolve(arg);
    else { console.error('usage : node crop_figures.js --captures <dossier>... [--out <dossier>] [--only <figure>...]'); process.exit(2); }
  }
  if (!captures.length) { console.error('indiquer au moins un dossier de captures avec --captures'); process.exit(2); }
  const figures = JSON.parse(fs.readFileSync(path.join(__dirname, 'figures.json'), 'utf8'));
  fs.mkdirSync(out, { recursive: true });
  let written = 0; const missing = [], manual = [];
  for (const [name, figure] of Object.entries(figures)) {
    if (only.length && !only.includes(name)) continue;
    if (!figure.source) { manual.push(name); continue; }
    const source = findCapture(captures, figure.source);
    if (!source) { missing.push(`${name} (${figure.source})`); continue; }
    const destination = path.join(out, `${name}.png`);
    cropFile(source, destination, figure.pixels); written++;
    console.log(`${name}.png ← ${path.relative(process.cwd(), source)} (${figure.pixels.join('×')})`);
  }
  if (missing.length) console.log(`captures absentes des dossiers fournis : ${missing.join(', ')}`);
  if (manual.length) console.log(`figures sans capture CI déclarée, inchangées : ${manual.join(', ')}`);
  console.log(`${written} figure(s) écrite(s) dans ${out}`);
}

if (require.main === module) main();
module.exports = { decode, encode, cropFile };
