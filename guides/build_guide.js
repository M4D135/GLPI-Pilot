#!/usr/bin/env node
// Fabrique un guide Word (.docx) et PDF à partir d'un texte en Markdown simplifié et d'une fiche JSON.
// Usage : node build_guide.js guide.json [--no-pdf]
// Syntaxe du texte et champs de la fiche : voir PROMPT_GUIDES_AUTRE_LOGICIEL.md (étape 6) et LIRE-KIT.md. Le PDF est imprimé par Google Chrome, Chromium ou Edge sans interface (aucun envoi
// réseau) ; chemin forcé par la variable CHROME. Fonctionne sous macOS, Windows et Linux ; images PNG ou JPEG.
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { pathToFileURL } = require('url');
const d = require(process.env.PESTO_DOCX_MODULE || 'docx');

// Navigateur pour le PDF : variable CHROME, sinon emplacements habituels de Chrome, Chromium et Edge.
function findChrome() {
  if (process.env.CHROME) return fs.existsSync(process.env.CHROME) ? process.env.CHROME : null;
  const pf = [process.env['PROGRAMFILES'], process.env['PROGRAMFILES(X86)'], process.env['LOCALAPPDATA']].filter(Boolean);
  const candidates = [
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    path.join(process.env.HOME || '', 'Applications', 'Google Chrome.app', 'Contents', 'MacOS', 'Google Chrome'),
    '/Applications/Chromium.app/Contents/MacOS/Chromium',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    ...pf.map(d => path.join(d, 'Google', 'Chrome', 'Application', 'chrome.exe')),
    ...pf.map(d => path.join(d, 'Microsoft', 'Edge', 'Application', 'msedge.exe')),
    ...pf.map(d => path.join(d, 'Chromium', 'Application', 'chrome.exe')),
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser', '/snap/bin/chromium',
  ];
  for (const c of candidates) if (fs.existsSync(c)) return c;
  for (const name of ['google-chrome', 'google-chrome-stable', 'chromium', 'chromium-browser', 'microsoft-edge']) {
    try { const found = execFileSync('which', [name], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim(); if (found) return found; } catch (e) { /* suivant */ }
  }
  return null;
}
const BLUE = '0B5CAD', INK = '1F2937', MUTED = '5B6573', LINE = 'D9D9D9', HEAD = 'E3EDF9';
const CALLOUTS = {
  ATTENTION: { label: 'Attention', bar: 'C2410C', fill: 'FFF4EC' },
  ASTUCE: { label: 'Astuce', bar: '15803D', fill: 'EEF8F0' },
  RESULTAT: { label: 'Résultat attendu', bar: BLUE, fill: 'EEF4FC' },
  NOTE: { label: 'À savoir', bar: '6B7280', fill: 'F3F4F6' },
};
const PAGE_W = 11906, MARGIN = 1134, CONTENT = PAGE_W - 2 * MARGIN; // A4, marges 2 cm, largeur utile en DXA

// ---------- Lecture ----------
// Texte UTF-8 (BOM accepté) ; UTF-16 et fichiers illisibles refusés avec un message clair.
function readText(file) {
  let b;
  try { b = fs.readFileSync(file); } catch (e) { console.error(`erreur : lecture impossible : ${file} (${e.code || e.message})`); process.exit(1); }
  if ((b[0] === 0xFF && b[1] === 0xFE) || (b[0] === 0xFE && b[1] === 0xFF) || b.includes(0)) {
    console.error(`erreur : ${file} est en UTF-16 ou binaire : l'enregistrer en UTF-8`); process.exit(1);
  }
  let t;
  try { t = new TextDecoder('utf-8', { fatal: true }).decode(b); } catch (e) { console.error(`erreur : ${file} n'est pas en UTF-8 valide : l'enregistrer en UTF-8`); process.exit(1); }
  t = t.replace(/^\uFEFF/, '');
  const bad = t.split(/\r?\n/).findIndex(l => /[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/.test(l));
  if (bad >= 0) { console.error(`erreur : ${file}, ligne ${bad + 1} : caractère de contrôle (saut de page, échappement…) à retirer`); process.exit(1); }
  return t;
}
function parse(text) {
  const blocks = [];
  const lines = text.replace(/\r/g, '').split('\n');
  const problems = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trimEnd();
    if (!line.trim()) continue;
    let m;
    const last = blocks[blocks.length - 1];
    if (!/^(```|~~~)/.test(line)) {
      const plain = line.replace(/\*\*[^*]+\*\*/g, '');
      const why = /^\s/.test(line) ? 'ligne indentée'
        : /^(\* |\+ )/.test(line) ? 'puce * ou +'
        : /^\d+\) /.test(line) ? 'liste 1)'
        : /\\\|/.test(line) ? 'barre verticale échappée'
        : /<\/[a-z][a-z0-9]*>|<(br|hr|img)\b[^>]*>|<!--/i.test(line) ? 'balise HTML ou commentaire'
        : /<https?:\/\/|\]\[[^\]]*\]|\]\((?!fig:[\w-]+\)$)/.test(line) ? 'lien ou image hors fig:'
        : /\*\*\*/.test(line) || /(^|[^\w*])\*[^\s*](?:[^*]*?[^\s*])?\*(?![\w*])/.test(plain)
          || /(^|[\s(«])_[^\s_](?:[^_]*?[^\s_])?_(?=$|[\s).,;:!?»])/.test(line) ? 'italique' : null;
      if (why) problems.push(`ligne ${i + 1} : syntaxe non prise en charge (${why}) : ${line.trim().slice(0, 60)}`);
    }
    if (/^(```|~~~)/.test(line)) {
      const fence = line.slice(0, 3);
      const code = [], start = i + 1;
      while (++i < lines.length && !lines[i].trimEnd().startsWith(fence)) code.push(lines[i].replace(/\s+$/, ''));
      if (i >= lines.length) problems.push(`ligne ${start} : bloc de commandes ouvert ici, non fermé`);
      blocks.push({ type: 'code', lines: code });
    } else if ((m = line.match(/^# (.+)$/))) blocks.push({ type: 'h1', text: m[1].trim() });
    else if ((m = line.match(/^## (.+)$/))) blocks.push({ type: 'h2', text: m[1].trim() });
    else if ((m = line.match(/^!\[(.*)\]\(fig:([\w-]+)\)$/))) blocks.push({ type: 'fig', caption: m[1].trim(), id: m[2], line: i + 1 });
    else if ((m = line.match(/^> \[!(\w+)\]\s*(.+)$/))) {
      const kind = m[1].toUpperCase();
      if (!CALLOUTS[kind]) problems.push(`ligne ${i + 1} : encadré inconnu ${kind}`);
      blocks.push({ type: 'callout', kind: CALLOUTS[kind] ? kind : 'NOTE', text: m[2].trim() });
    } else if ((m = line.match(/^(\d+)\. (.+)$/))) {
      if (last && last.type === 'steps') last.items.push({ n: m[1], text: m[2] });
      else blocks.push({ type: 'steps', items: [{ n: m[1], text: m[2] }] });
    } else if ((m = line.match(/^- (.+)$/))) {
      if (last && last.type === 'bullets') last.items.push(m[1]);
      else blocks.push({ type: 'bullets', items: [m[1]] });
    } else if (line.startsWith('|')) {
      const cells = line.replace(/^\|/, '').replace(/\|\s*$/, '').split('|').map(c => c.trim());
      if (cells.every(c => /^:?-{2,}:?$/.test(c))) continue;
      if (last && last.type === 'table' && lines[i - 1] && lines[i - 1].trim().startsWith('|')) {
        if (cells.length !== last.rows[0].length) problems.push(`ligne ${i + 1} : ${cells.length} cellules au lieu de ${last.rows[0].length} (une barre verticale dans une cellule ?)`);
        last.rows.push(cells);
      }
      else blocks.push({ type: 'table', rows: [cells] });
    } else if (/^(#{3,}|>)/.test(line)) {
      problems.push(`ligne ${i + 1} : syntaxe non prise en charge : ${line.slice(0, 60)}`);
      blocks.push({ type: 'p', text: line.replace(/^[#>`\s]+/, '') });
    } else blocks.push({ type: 'p', text: line.trim() });
  }
  // Les étapes et les puces séparées par une ligne vide restent une seule liste : rien à faire, la ligne vide est ignorée.
  return { blocks, problems };
}

// Segments gras / normal à partir de **…**.
function spans(text) {
  const out = [];
  text.split(/(\*\*[^*]+\*\*)/).forEach(part => {
    if (!part) return;
    if (part.startsWith('**') && part.endsWith('**')) out.push({ t: part.slice(2, -2), b: true });
    else out.push({ t: part.replace(/\*\*/g, ''), b: false });
  });
  return out;
}

// Dimensions d'une image PNG ou JPEG, sans dépendance.
function imageInfo(file) {
  if (!fs.existsSync(file)) throw new Error(`image introuvable : ${file}`);
  const b = fs.readFileSync(file);
  if (b.toString('ascii', 1, 4) === 'PNG') return { w: b.readUInt32BE(16), h: b.readUInt32BE(20), data: b, type: 'png' };
  if (b[0] === 0xFF && b[1] === 0xD8) {
    let i = 2;
    while (i + 9 < b.length) {
      if (b[i] !== 0xFF) { i++; continue; }
      const marker = b[i + 1];
      if (marker >= 0xC0 && marker <= 0xCF && ![0xC4, 0xC8, 0xCC].includes(marker)) return { h: b.readUInt16BE(i + 5), w: b.readUInt16BE(i + 7), data: b, type: 'jpg' };
      i += 2 + b.readUInt16BE(i + 2);
    }
  }
  throw new Error(`${file} : image PNG ou JPEG attendue`);
}

function chapterAnchor(i) { return `chapitre_${i + 1}`; }

// Couverture : 17 cm de large et 10 cm de haut au plus (la page de garde porte aussi titres et informations).
const coverCm = img => Math.min(17, 10 * img.w / img.h);

// Largeurs de colonnes (DXA) selon la longueur moyenne du texte, bornées.
function columnWidths(rows, total) {
  const count = Math.max(...rows.map(row => row.length));
  // Give field names and short mode labels enough room before sharing the rest.
  // Fractions keep the same layout for Word (twips) and HTML (percentages).
  const minima = [], weights = [];
  for (let column = 0; column < count; column++) {
    const texts = rows.map(row => (row[column] || '').replace(/\*\*/g, ''));
    const words = texts.flatMap(text => text.match(/[A-Za-zÀ-ÿ0-9_.[\]-]+/g) || []);
    const longest = Math.max(4, ...words.map(word => word.length));
    minima.push(Math.min(0.38, Math.max(0.13, (longest * 5.7 + 16) / 482)));
    weights.push(Math.min(70, Math.max(10, texts.reduce((sum, text) => sum + text.length, 0) / texts.length)));
  }
  const initial = minima.reduce((a, b) => a + b, 0);
  const scale = initial > 0.85 ? 0.85 / initial : 1;
  const minimum = minima.map(value => value * scale);
  const spare = 1 - minimum.reduce((a, b) => a + b, 0);
  const weight = weights.reduce((a, b) => a + b, 0);
  const widths = minimum.map((value, index) => Math.round(total * (value + spare * weights[index] / weight)));
  widths[count - 1] += total - widths.reduce((a, b) => a + b, 0);
  return widths;
}

// ---------- Word ----------
function runs(text, opts = {}) {
  return spans(text).map(s => new d.TextRun({ text: s.t, bold: s.b || opts.bold, color: opts.color, size: opts.size, italics: opts.italics }));
}
const NONE = { style: d.BorderStyle.NONE, size: 0, color: 'FFFFFF' };
const noBorders = { top: NONE, bottom: NONE, left: NONE, right: NONE, insideHorizontal: NONE, insideVertical: NONE };

function docxBlocks(blocks, meta, base, state) {
  const out = [];
  let chapter = -1;
  for (const [index, b] of blocks.entries()) {
    // A chapter heading already separates the preceding table. An empty
    // paragraph here can spill onto an otherwise blank page in Word.
    const tableGap = () => {
      if (blocks[index + 1]?.type !== 'h1') {
        out.push(new d.Paragraph({ spacing: { before: 0, after: 60, line: 20, lineRule: 'exact' }, children: [] }));
      }
    };
    if (b.type === 'h1') {
      chapter++;
      out.push(new d.Paragraph({
        heading: d.HeadingLevel.HEADING_1, pageBreakBefore: true,
        children: [new d.Bookmark({ id: chapterAnchor(chapter), children: [new d.TextRun(b.text)] })],
      }));
    } else if (b.type === 'h2') out.push(new d.Paragraph({ heading: d.HeadingLevel.HEADING_2, keepNext: true, children: [new d.TextRun(b.text)] }));
    else if (b.type === 'p') out.push(new d.Paragraph({ children: runs(b.text) }));
    else if (b.type === 'bullets') b.items.forEach(t => out.push(new d.Paragraph({ numbering: { reference: 'puces', level: 0 }, children: runs(t) })));
    else if (b.type === 'steps') {
      const rows = b.items.map((it, k) => new d.TableRow({
        cantSplit: true,
        children: [
          new d.TableCell({
            width: { size: 760, type: d.WidthType.DXA }, borders: { top: NONE, left: NONE, right: NONE, bottom: k === b.items.length - 1 ? NONE : { style: d.BorderStyle.SINGLE, size: 4, color: 'E5E7EB' } },
            margins: { top: 70, bottom: 70, left: 60, right: 60 },
            children: [new d.Paragraph({ alignment: d.AlignmentType.CENTER, spacing: { after: 0 }, children: [new d.TextRun({ text: it.n, bold: true, size: 32, color: BLUE })] })],
          }),
          new d.TableCell({
            width: { size: CONTENT - 760, type: d.WidthType.DXA }, borders: { top: NONE, left: NONE, right: NONE, bottom: k === b.items.length - 1 ? NONE : { style: d.BorderStyle.SINGLE, size: 4, color: 'E5E7EB' } },
            margins: { top: 70, bottom: 70, left: 120, right: 60 },
            children: [new d.Paragraph({ spacing: { after: 0 }, children: runs(it.text) })],
          }),
        ],
      }));
      out.push(new d.Table({ width: { size: CONTENT, type: d.WidthType.DXA }, columnWidths: [760, CONTENT - 760], borders: noBorders, rows }));
      tableGap();
    } else if (b.type === 'callout') {
      const c = CALLOUTS[b.kind];
      out.push(new d.Paragraph({ keepLines: true, spacing: { before: 80, after: 140 },
        children: [new d.TextRun({ text: c.label + '. ', bold: true }), ...runs(b.text)] }));
    } else if (b.type === 'table') {
      const widths = columnWidths(b.rows, CONTENT);
      const border = { style: d.BorderStyle.SINGLE, size: 4, color: LINE };
      out.push(new d.Table({
        width: { size: CONTENT, type: d.WidthType.DXA }, columnWidths: widths,
        borders: { top: border, bottom: border, left: border, right: border, insideHorizontal: border, insideVertical: border },
        rows: b.rows.map((r, ri) => new d.TableRow({
          tableHeader: ri === 0, cantSplit: true,
          children: widths.map((w, ci) => new d.TableCell({
            width: { size: w, type: d.WidthType.DXA },
            shading: ri === 0 ? { type: d.ShadingType.CLEAR, color: 'auto', fill: HEAD } : undefined,
            margins: { top: 70, bottom: 70, left: 110, right: 110 },
            children: [new d.Paragraph({ spacing: { after: 0 }, children: runs(r[ci] || '', ri === 0 ? { bold: true } : {}) })],
          })),
        })),
      }));
      tableGap();
    } else if (b.type === 'code') {
      out.push(new d.Table({
        width: { size: CONTENT, type: d.WidthType.DXA }, columnWidths: [CONTENT],
        rows: [new d.TableRow({ cantSplit: true, children: [new d.TableCell({
          width: { size: CONTENT, type: d.WidthType.DXA },
          shading: { type: d.ShadingType.CLEAR, color: 'auto', fill: 'F3F4F6' },
          borders: { top: NONE, bottom: NONE, right: NONE, left: { style: d.BorderStyle.SINGLE, size: 12, color: LINE } },
          margins: { top: 80, bottom: 80, left: 160, right: 120 },
          children: b.lines.map(l => new d.Paragraph({ spacing: { after: 0, line: 240 }, children: [new d.TextRun({ text: l || ' ', font: 'Liberation Mono', size: 17 })] })),
        })] })],
      }));
      tableGap();
    } else if (b.type === 'fig') {
      const f = meta.figures[b.id];
      const img = imageInfo(path.join(base, f.file));
      const wpx = Math.round(f.width / 2.54 * 96), hpx = Math.round(wpx * img.h / img.w);
      state.figure++;
      out.push(new d.Paragraph({ alignment: d.AlignmentType.CENTER, keepNext: true, spacing: { before: 120, after: 60 },
        children: [new d.ImageRun({ type: img.type, data: img.data, transformation: { width: wpx, height: hpx }, altText: { title: `Figure ${state.figure}`, description: b.caption, name: b.id } })] }));
      out.push(new d.Paragraph({ alignment: d.AlignmentType.CENTER, spacing: { after: 200 },
        children: [new d.TextRun({ text: `Figure ${state.figure}. `, bold: true, size: 17, color: MUTED }), new d.TextRun({ text: b.caption, size: 17, color: MUTED })] }));
    }
  }
  return out;
}

function buildDocx(meta, blocks, base, outFile) {
  const chapters = blocks.filter(b => b.type === 'h1').map(b => b.text);
  const cover = [];
  cover.push(new d.Paragraph({ spacing: { before: 600, after: 0 }, children: [new d.TextRun({ text: meta.product, size: 30, bold: true, color: '000000' })] }));
  cover.push(new d.Paragraph({ style: 'Title', spacing: { after: 120 }, children: [new d.TextRun({ text: meta.title, size: 64, bold: true, color: '000000' })] }));
  cover.push(new d.Paragraph({ spacing: { after: 360 },
    children: [new d.TextRun({ text: meta.subtitle, size: 28, color: '000000' })] }));
  if (meta.cover) {
    const f = meta.figures[meta.cover];
    const img = imageInfo(path.join(base, f.file));
    const wpx = Math.round(coverCm(img) / 2.54 * 96), hpx = Math.round(wpx * img.h / img.w);
    cover.push(new d.Paragraph({ alignment: d.AlignmentType.CENTER, spacing: { after: 360 }, children: [new d.ImageRun({ type: img.type, data: img.data, transformation: { width: wpx, height: hpx }, altText: { title: meta.product, description: meta.coverCaption || '', name: 'couverture' } })] }));
  }
  const info = meta.info || [];
  for (const [key, value] of info) {
    cover.push(new d.Paragraph({ spacing: { before: 120, after: 40 },
      children: [new d.TextRun({ text: key, bold: true, color: '000000', size: 21 })] }));
    cover.push(new d.Paragraph({ spacing: { after: 180 }, children: runs(value) }));
  }
  const toc = [new d.Paragraph({ heading: d.HeadingLevel.HEADING_1, pageBreakBefore: true, children: [new d.TextRun('Sommaire')] })];
  if (meta.tocIntro) toc.push(new d.Paragraph({ children: runs(meta.tocIntro, { color: MUTED }) }));
  chapters.forEach((t, i) => toc.push(new d.Paragraph({ spacing: { after: 90 }, border: { bottom: { style: d.BorderStyle.SINGLE, size: 2, color: 'E5E7EB', space: 4 } },
    children: [new d.InternalHyperlink({ anchor: chapterAnchor(i), children: [new d.TextRun({ text: t, color: BLUE, size: 23 })] })] })));
  const state = { figure: 0 };
  const body = docxBlocks(blocks, meta, base, state);
  const label = `${meta.product} · ${meta.title}`;
  const doc = new d.Document({
    creator: meta.author || meta.product, title: `${meta.product} – ${meta.title}`, description: meta.subtitle, language: 'fr-FR',
    styles: {
      default: { document: { run: { font: 'Arial', size: 21, color: INK }, paragraph: { spacing: { after: 120, line: 276 } } } },
      paragraphStyles: [
        { id: 'Title', name: 'Title', basedOn: 'Normal', next: 'Normal', quickFormat: true,
          run: { font: 'Arial', size: 64, bold: true, color: '000000' } },
        { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true,
          run: { font: 'Arial', size: 38, bold: true, color: '000000' },
          paragraph: { spacing: { before: 0, after: 240 }, outlineLevel: 0 } },
        { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true,
          run: { font: 'Arial', size: 26, bold: true, color: '000000' },
          paragraph: { spacing: { before: 300, after: 120 }, outlineLevel: 1 } },
      ],
      characterStyles: [{ id: 'Hyperlink', name: 'Hyperlink', basedOn: 'DefaultParagraphFont', run: { color: BLUE } }],
    },
    numbering: { config: [{ reference: 'puces', levels: [{ level: 0, format: d.LevelFormat.BULLET, text: '•', alignment: d.AlignmentType.LEFT,
      style: { paragraph: { indent: { left: 400, hanging: 260 } }, run: { color: BLUE } } }] }] },
    sections: [{
      properties: { page: { size: { width: PAGE_W, height: 16838 }, margin: { top: MARGIN, bottom: MARGIN, left: MARGIN, right: MARGIN, header: 567, footer: 567 } }, titlePage: true },
      headers: {
        first: new d.Header({ children: [new d.Paragraph('')] }),
        default: new d.Header({ children: [new d.Paragraph({ alignment: d.AlignmentType.RIGHT, children: [new d.TextRun({ text: label, size: 16, color: '000000' })] })] }),
      },
      footers: {
        first: new d.Footer({ children: [new d.Paragraph({ children: [new d.TextRun({ text: meta.edition, size: 16, color: MUTED })] })] }),
        default: new d.Footer({ children: [new d.Paragraph({ alignment: d.AlignmentType.CENTER, children: [new d.TextRun({ children: ['Page ', d.PageNumber.CURRENT, ' sur ', d.PageNumber.TOTAL_PAGES], size: 16, color: MUTED })] })] }),
      },
      children: [...cover, ...toc, ...body],
    }],
  });
  return d.Packer.toBuffer(doc).then(buf => { fs.writeFileSync(outFile, buf); return state.figure; });
}

// ---------- HTML puis PDF ----------
const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
// Chaîne CSS (en-tête du PDF) : les entités HTML n'y sont pas décodées.
const cssStr = s => s.replace(/\\/g, '\\5c ').replace(/"/g, '\\22 ').replace(/</g, '\\3c ').replace(/[\r\n]+/g, ' ');
const inline = t => spans(t).map(s => s.b ? `<strong>${esc(s.t)}</strong>` : esc(s.t)).join('');

function buildHtml(meta, blocks, base) {
  let chapter = -1, figure = 0;
  const html = [];
  for (const b of blocks) {
    if (b.type === 'h1') { chapter++; html.push(`<h1 id="${chapterAnchor(chapter)}">${esc(b.text)}</h1>`); }
    else if (b.type === 'h2') html.push(`<h2>${esc(b.text)}</h2>`);
    else if (b.type === 'p') html.push(`<p>${inline(b.text)}</p>`);
    else if (b.type === 'bullets') html.push(`<ul>${b.items.map(t => `<li>${inline(t)}</li>`).join('')}</ul>`);
    else if (b.type === 'steps') html.push(`<ol class="steps">${b.items.map(it => `<li><span class="n">${esc(it.n)}</span><div>${inline(it.text)}</div></li>`).join('')}</ol>`);
    else if (b.type === 'callout') { const c = CALLOUTS[b.kind]; html.push(`<div class="callout" style="--bar:#${c.bar};--fill:#${c.fill}"><strong class="lbl">${c.label}.</strong> ${inline(b.text)}</div>`); }
    else if (b.type === 'table') {
      const widths = columnWidths(b.rows, 100);
      html.push(`<table><colgroup>${widths.map(w => `<col style="width:${w}%">`).join('')}</colgroup><thead><tr>${b.rows[0].map(c => `<th>${inline(c)}</th>`).join('')}</tr></thead><tbody>${b.rows.slice(1).map(r => `<tr>${widths.map((_, i) => `<td>${inline(r[i] || '')}</td>`).join('')}</tr>`).join('')}</tbody></table>`);
    } else if (b.type === 'code') html.push(`<pre>${esc(b.lines.join('\n'))}</pre>`);
    else if (b.type === 'fig') {
      figure++;
      const f = meta.figures[b.id];
      html.push(`<figure><img src="${pathToFileURL(path.join(base, f.file)).href}" style="width:${f.width}cm" alt="${esc(b.caption)}"><figcaption><strong>Figure ${figure}.</strong> ${esc(b.caption)}</figcaption></figure>`);
    }
  }
  const chapters = blocks.filter(b => b.type === 'h1').map(b => b.text);
  const label = cssStr(`${meta.product} · ${meta.title}`);
  const coverFile = meta.cover ? path.join(base, meta.figures[meta.cover].file) : null;
  const cover = coverFile ? `<img class="cover-img" src="${pathToFileURL(coverFile).href}" style="width:${coverCm(imageInfo(coverFile)).toFixed(2)}cm" alt="${esc(meta.coverCaption || '')}">` : '';
  return `<!doctype html><html lang="fr"><head><meta charset="utf-8"><title>${esc(meta.product + ' – ' + meta.title)}</title><style>
@page { size: A4; margin: 2cm 2cm 2cm 2cm;
  @top-right { content: "${label}"; font: 8pt "Segoe UI", "Avenir Next", "Helvetica Neue", Arial, sans-serif; color: #${MUTED}; }
  @bottom-center { content: "Page " counter(page) " sur " counter(pages); font: 8pt "Segoe UI", "Avenir Next", "Helvetica Neue", Arial, sans-serif; color: #${MUTED}; } }
@page :first { @top-right { content: none; } @bottom-center { content: none; } }
html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
body { font: 10.5pt/1.45 "Segoe UI", "Avenir Next", "Helvetica Neue", Arial, sans-serif; color: #${INK}; margin: 0; background: #fff; }
.cover { page-break-after: always; padding-top: 1.2cm; }
.cover .product { color: #000000; font-weight: 700; font-size: 15pt; }
.cover .title { font-size: 32pt; font-weight: 700; margin: 2pt 0 6pt; }
.cover .subtitle { font-size: 14pt; color: #000000; padding-bottom: 8pt; margin-bottom: 18pt; }
.cover-img { max-width: 100%; box-sizing: border-box; display: block; margin-left: auto; margin-right: auto; border: 0.75pt solid #${LINE}; border-radius: 4pt; margin-bottom: 18pt; }
.cover dl { display: grid; grid-template-columns: 4.6cm 1fr; gap: 3pt 0; font-size: 9.5pt; margin: 0; }
.cover dt { font-weight: 700; color: #${MUTED}; } .cover dd { margin: 0; }
.cover .edition { margin-top: 14pt; color: #${MUTED}; font-size: 8pt; }
h1 { break-before: page; color: #000000; font-size: 19pt; margin: 0 0 12pt; padding-bottom: 5pt; }
h2 { font-size: 13pt; margin: 16pt 0 6pt; break-after: avoid; }
p { margin: 0 0 6pt; } ul { margin: 0 0 8pt; padding-left: 16pt; } li { margin-bottom: 3pt; } ul li::marker { color: #${BLUE}; }
ol.steps { list-style: none; padding: 0; margin: 0 0 10pt; }
ol.steps li { display: flex; gap: 10pt; padding: 4pt 0; border-bottom: 0.5pt solid #E5E7EB; break-inside: avoid; }
ol.steps li:last-child { border-bottom: none; }
ol.steps .n { flex: 0 0 22pt; text-align: center; font-weight: 700; font-size: 16pt; line-height: 1.1; color: #${BLUE}; }
.callout { padding: 4pt 0; margin: 4pt 0 10pt; break-inside: avoid; }
.callout .lbl { color: #000000; }
table { width: 100%; border-collapse: collapse; margin: 4pt 0 10pt; font-size: 9.5pt; }
th, td { border: 0.5pt solid #${LINE}; padding: 4pt 6pt; text-align: left; vertical-align: top; }
th { background: #${HEAD}; } tr { break-inside: avoid; } thead { display: table-header-group; }
figure { margin: 8pt 0 12pt; text-align: center; break-inside: avoid; }
figure img { max-width: 100%; box-sizing: border-box; border: 0.75pt solid #${LINE}; border-radius: 3pt; }
figcaption { font-size: 8.5pt; color: #${MUTED}; margin-top: 4pt; }
pre { background: #F3F4F6; border-left: 2pt solid #${LINE}; padding: 6pt 9pt; margin: 4pt 0 10pt; font: 8.5pt/1.35 "Liberation Mono", Consolas, Menlo, monospace; white-space: pre-wrap; break-inside: avoid; }
.toc a { display: block; color: #${BLUE}; text-decoration: none; font-size: 11.5pt; padding: 4pt 0; border-bottom: 0.5pt solid #E5E7EB; }
</style></head><body>
<section class="cover"><div class="product">${esc(meta.product)}</div><div class="title">${esc(meta.title)}</div><div class="subtitle">${esc(meta.subtitle)}</div>${cover}
<dl>${(meta.info || []).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${inline(v)}</dd>`).join('')}</dl><div class="edition">${esc(meta.edition)}</div></section>
<h1 id="sommaire">Sommaire</h1>${meta.tocIntro ? `<p style="color:#${MUTED}">${inline(meta.tocIntro)}</p>` : ''}<nav class="toc">${chapters.map((t, i) => `<a href="#${chapterAnchor(i)}">${esc(t)}</a>`).join('')}</nav>
${html.join('\n')}
</body></html>`;
}

async function main() {
  const args = process.argv.slice(2);
  const metaFile = args.find(a => !a.startsWith('--'));
  if (!metaFile) { console.error('Usage : node build_guide.js <guide.json> [--no-pdf]'); process.exit(2); }
  const base = path.dirname(path.resolve(metaFile));
  let meta;
  try { meta = JSON.parse(readText(metaFile)); } catch (e) { console.error(`erreur : fiche ${metaFile} : JSON invalide (${e.message})`); process.exit(1); }
  const missing = ['source', 'output', 'product', 'title', 'subtitle', 'edition', 'figures'].filter(k => meta[k] === undefined);
  if (missing.length) { console.error(`erreur : fiche ${metaFile} incomplète, champs manquants : ${missing.join(', ')}`); process.exit(1); }
  if (meta.cover && !meta.figures[meta.cover]) { console.error(`erreur : figure de couverture inconnue : ${meta.cover}`); process.exit(1); }
  for (const [id, f] of Object.entries(meta.figures)) {
    if (!f || !f.file || !(f.width > 0)) { console.error(`erreur : figure ${id} : file et width (cm, > 0) obligatoires`); process.exit(1); }
    try { imageInfo(path.join(base, f.file)); } catch (e) { console.error('erreur : ' + e.message); process.exit(1); }
  }
  // figures.json (crop_figures.js) fixe les dimensions attendues des figures recadrées depuis les captures de la CI.
  const expectedFile = path.join(__dirname, 'figures.json');
  if (fs.existsSync(expectedFile)) {
    const expected = JSON.parse(readText(expectedFile));
    for (const [id, f] of Object.entries(meta.figures)) {
      const spec = expected[path.basename(f.file, path.extname(f.file))];
      if (!spec || !spec.pixels) continue;
      const img = imageInfo(path.join(base, f.file));
      if (img.w !== spec.pixels[0] || img.h !== spec.pixels[1]) { console.error(`erreur : figure ${id} : ${img.w}×${img.h}, attendu ${spec.pixels[0]}×${spec.pixels[1]} (figures.json ; recadrer avec crop_figures.js)`); process.exit(1); }
    }
  }
  const { blocks, problems } = parse(readText(path.join(base, meta.source)));
  const used = blocks.filter(b => b.type === 'fig').map(b => b.id);
  const figs = blocks.filter(b => b.type === 'fig');
  figs.filter(b => !meta.figures[b.id]).forEach(b => problems.push(`ligne ${b.line} : figure inconnue : ${b.id}`));
  figs.filter((b, i) => used.indexOf(b.id) !== i).forEach(b => problems.push(`ligne ${b.line} : figure déjà utilisée : ${b.id}`));
  Object.keys(meta.figures).filter(id => id !== meta.cover && !used.includes(id)).forEach(id => console.warn(`avertissement : figure non utilisée : ${id}`));
  if (problems.length) { problems.forEach(p => console.error('erreur : ' + p)); process.exit(1); }
  const outDir = path.join(base, 'build');
  fs.mkdirSync(outDir, { recursive: true });
  const docxFile = path.join(outDir, meta.output + '.docx');
  const figures = await buildDocx(meta, blocks, base, docxFile);
  const htmlFile = path.join(outDir, meta.output + '.html');
  fs.writeFileSync(htmlFile, buildHtml(meta, blocks, base));
  const counts = blocks.reduce((a, b) => (a[b.type] = (a[b.type] || 0) + 1, a), {});
  console.log(`${docxFile}\n  chapitres ${counts.h1 || 0}, sections ${counts.h2 || 0}, figures ${figures}, tableaux ${counts.table || 0}, encadrés ${counts.callout || 0}`);
  if (!args.includes('--no-pdf')) {
    const pdfFile = path.join(outDir, meta.output + '.pdf');
    const chrome = findChrome();
    if (!chrome) { console.error(`erreur : ${process.env.CHROME ? 'CHROME=' + process.env.CHROME + ' introuvable' : 'Chrome, Chromium ou Edge introuvable'} pour le PDF ; indiquer le chemin du navigateur dans la variable CHROME, ou relancer avec --no-pdf`); process.exit(3); }
    if (fs.existsSync(pdfFile)) fs.unlinkSync(pdfFile);
    try {
      // Sous Linux en root (conteneurs), Chrome refuse de démarrer sans --no-sandbox.
      const sandbox = process.platform === 'linux' && process.getuid && process.getuid() === 0 ? ['--no-sandbox'] : [];
      execFileSync(chrome, ['--headless=new', '--disable-gpu', '--no-pdf-header-footer', '--allow-file-access-from-files', ...sandbox,
        `--print-to-pdf=${pdfFile}`, pathToFileURL(htmlFile).href], { stdio: ['ignore', 'ignore', 'pipe'], timeout: 180000 });
    } catch (e) {
      const detail = (e.stderr ? e.stderr.toString() : '').trim().split('\n').slice(-3).join(' | ');
      console.error(`erreur : impression PDF impossible avec ${chrome} : ${e.message.split('\n')[0]}${detail ? ' ; ' + detail : ''}`); process.exit(3);
    }
    if (!fs.existsSync(pdfFile) || fs.statSync(pdfFile).size === 0) { console.error(`erreur : PDF non produit par ${chrome}`); process.exit(3); }
    console.log(pdfFile);
  }
}
main().catch(e => { console.error(e); process.exit(1); });
