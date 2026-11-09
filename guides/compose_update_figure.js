#!/usr/bin/env node
// Assemble deux extraits intacts de l'interface, sans redimensionnement ni retouche du texte.
// node guides/compose_update_figure.js <dossier des deux captures> <figure.png>
'use strict';
const fs = require('fs');
const path = require('path');
const { decode, encode } = require('./crop_figures.js');

const [source, destination] = process.argv.slice(2);
if (!source || !destination) throw new Error('usage : compose_update_figure.js <captures> <figure.png>');
const panels = ['shared-update-network.png', 'shared-update-local.png']
  .map(name => decode(fs.readFileSync(path.join(source, name))));
const { channels, color } = panels[0];
for (const panel of panels) {
  if (panel.width !== 600 || panel.height > 490 || panel.channels !== channels || panel.color !== color)
    throw new Error(`Extrait ${panel.width}×${panel.height} (couleur ${panel.color}) : attendu 600 pixels de large, au plus 490 de haut, avec le même format PNG.`);
}
const figure = { width: 1220, height: 490, channels, color, pixels: Buffer.alloc(1220 * 490 * channels, 255) };
const top = Math.floor((figure.height - Math.max(...panels.map(panel => panel.height))) / 2);
panels.forEach((panel, index) => {
  for (let y = 0; y < panel.height; y++)
    panel.pixels.copy(figure.pixels, ((top + y) * figure.width + index * 620) * channels,
      y * panel.width * channels, (y + 1) * panel.width * channels);
});
fs.mkdirSync(path.dirname(destination), { recursive: true });
fs.writeFileSync(destination, encode(figure, 0, 0, figure.width, figure.height));
console.log(`${destination} : 1220×490, deux extraits sans redimensionnement`);
