import { svg } from './analytics-ui.js';

let sceneSequence = 0;

// The scene illustrates the state supplied by the engine. It never derives a
// forecast, severity or score from the value shown beside it.
export function weatherScene(value) {
  const state = ['sun', 'watch', 'rain', 'storm', 'partial', 'unknown'].includes(value) ? value : 'unknown';
  const id = `pilot-weather-${++sceneSequence}`;
  const drawing = svg('svg', { viewBox: '0 0 220 146', class: 'pilot-weather-scene analytics-volume-weather-icon',
    'data-weather-scene': state, 'aria-hidden': 'true', focusable: 'false' });
  // The phase is decorative, independent of any ticket count or threshold.
  // Staggering scenes also avoids a wall of clouds moving in lockstep.
  drawing.style.setProperty('--weather-phase', `${-(sceneSequence % 7) * .43}s`);
  drawing.append(svg('defs', {},
    svg('radialGradient', { id: `${id}-halo` }, svg('stop', { offset: '0', 'stop-color': '#ffd775', 'stop-opacity': '.75' }),
      svg('stop', { offset: '1', 'stop-color': '#ffbd55', 'stop-opacity': '0' })),
    svg('linearGradient', { id: `${id}-sun`, x1: '0', y1: '0', x2: '0', y2: '1' },
      svg('stop', { offset: '0', 'stop-color': '#fff0ad' }), svg('stop', { offset: '1', 'stop-color': '#ffad31' })),
    svg('linearGradient', { id: `${id}-cloud`, x1: '0', y1: '0', x2: '.2', y2: '1' },
      svg('stop', { offset: '0', 'stop-color': 'var(--weather-cloud-light)' }),
      svg('stop', { offset: '1', 'stop-color': 'var(--weather-cloud-deep)' })),
    svg('radialGradient', { id: `${id}-air` }, svg('stop', { offset: '0', 'stop-color': 'var(--weather-haze)', 'stop-opacity': '.45' }),
      svg('stop', { offset: '1', 'stop-color': 'var(--weather-haze)', 'stop-opacity': '0' })),
    svg('clipPath', { id: `${id}-rain` }, svg('rect', { x: 20, y: 86, width: 184, height: 59 }))));
  drawing.append(svg('ellipse', { class: 'pilot-weather-atmosphere', cx: 112, cy: 78, rx: 92, ry: 62, fill: `url(#${id}-air)` }));

  if (state === 'sun' || state === 'watch') {
    const x = state === 'watch' ? 63 : 96, y = state === 'watch' ? 47 : 62, radius = state === 'watch' ? 24 : 29;
    drawing.style.setProperty('--weather-sun-x', `${x}px`); drawing.style.setProperty('--weather-sun-y', `${y}px`);
    const sun = svg('g', { class: 'pilot-weather-sun' });
    sun.append(svg('circle', { class: 'pilot-weather-halo', cx: x, cy: y, r: radius + 35, fill: `url(#${id}-halo)` }));
    const rays = svg('g', { class: 'pilot-weather-rays', fill: 'none', stroke: 'var(--weather-sun-ray)', 'stroke-width': 4.7, 'stroke-linecap': 'round' });
    for (let index = 0; index < 12; index++) {
      const angle = index * Math.PI / 6;
      rays.append(svg('line', { x1: x + Math.cos(angle) * (radius + 10), y1: y + Math.sin(angle) * (radius + 10),
        x2: x + Math.cos(angle) * (radius + (index % 2 ? 20 : 27)), y2: y + Math.sin(angle) * (radius + (index % 2 ? 20 : 27)) }));
    }
    sun.append(rays, svg('circle', { class: 'pilot-weather-sun-disc', cx: x, cy: y, r: radius, fill: `url(#${id}-sun)`, stroke: '#ffd266', 'stroke-width': 1.2 }),
      svg('path', { d: `M${x - 16} ${y - 13}c5-7 13-10 22-8`, fill: 'none', stroke: '#fff9df', 'stroke-width': 4, 'stroke-linecap': 'round', opacity: '.9' }));
    drawing.append(sun);
    if (state === 'sun') for (const [index, x, y] of [[0, 166, 25], [1, 43, 111]]) {
      const sparkle = svg('path', { class: 'pilot-weather-sparkle', d: `M${x - 5} ${y}h10M${x} ${y - 5}v10`,
        stroke: 'var(--weather-sun-ray)', 'stroke-width': 2.4, 'stroke-linecap': 'round', fill: 'none' });
      sparkle.style.setProperty('--weather-sparkle-delay', `${-index * 1.6}s`); drawing.append(sparkle);
    }
  }
  let nearCloud;
  if (state !== 'sun') {
    drawing.append(svg('g', { class: 'pilot-weather-cloud-far' },
      svg('path', { d: 'M84 68c-16 0-21-21-6-28 0-21 27-29 40-13 14-10 32-1 34 14 20-2 25 27 4 29H84Z',
        fill: 'var(--weather-cloud-far)', opacity: '.7' })));
    nearCloud = svg('g', { class: 'pilot-weather-cloud-near' },
      svg('path', { d: 'M65 98c-26 0-32-32-9-43-1-29 36-43 56-20 23-15 49 1 49 26 29 0 34 39 5 40H65Z',
        fill: `url(#${id}-cloud)`, stroke: 'var(--weather-cloud-edge)', 'stroke-width': 1.5 }),
      svg('path', { d: 'M74 58c3-13 19-20 31-11', fill: 'none', stroke: 'var(--weather-cloud-highlight)', 'stroke-width': 4.4, 'stroke-linecap': 'round', opacity: '.8' }));
  } else {
    drawing.append(svg('g', { class: 'pilot-weather-cloud-far pilot-weather-fair-cloud' },
      svg('path', { d: 'M137 121c-15 0-19-18-6-25 1-18 23-24 34-12 13-8 27 1 28 14 18 0 20 25 2 25H137Z',
        fill: `url(#${id}-cloud)`, stroke: 'var(--weather-cloud-edge)', 'stroke-width': 1, opacity: '.9' })));
  }
  if (state === 'rain' || state === 'storm') {
    drawing.append(svg('ellipse', { class: 'pilot-weather-puddle', cx: 110, cy: 136, rx: 60, ry: 6, fill: 'var(--weather-rain)', opacity: '.1' }));
    const drops = svg('g', { 'clip-path': `url(#${id}-rain)`, class: 'pilot-weather-rain', fill: 'none', stroke: 'var(--weather-rain)', 'stroke-width': 4.6, 'stroke-linecap': 'round' });
    for (let index = 0; index < (state === 'storm' ? 10 : 8); index++) {
      const distant = index % 3 === 2;
      const drop = svg('path', { class: `pilot-weather-rain-particle${distant ? ' pilot-weather-rain-distant' : ''}`, d: `M${59 + index * 13} ${100 + index % 2 * 3}l-7 ${distant ? 11 : 17}` });
      drop.style.setProperty('--weather-drop-delay', `${-(index * .19 + .23)}s`);
      drop.style.setProperty('--weather-drop-duration', `${distant ? 1.45 : 1.05 + index % 3 * .11}s`); drops.append(drop);
    }
    drawing.append(drops);
    for (let index = 0; index < 3; index++) {
      const ripple = svg('ellipse', { class: 'pilot-weather-ripple', cx: 66 + index * 43, cy: 136 + index % 2 * 3, rx: 9, ry: 2.5,
        fill: 'none', stroke: 'var(--weather-rain)', 'stroke-width': 1.6 });
      ripple.style.setProperty('--weather-ripple-delay', `${-index * .47}s`); drawing.append(ripple);
    }
  }
  // Drops start behind the cloud instead of appearing over its front face.
  if (nearCloud) drawing.append(nearCloud);
  if (state === 'storm') drawing.append(svg('path', { class: 'pilot-weather-lightning', d: 'M119 84l-18 28h15l-7 24 29-36h-17l10-16Z',
    fill: '#ffd875', stroke: '#eeb53f', 'stroke-width': 1.1 }));
  if (state === 'partial') drawing.append(svg('g', { class: 'pilot-weather-neutral' },
    svg('path', { class: 'pilot-weather-mist', d: 'M58 119h77M80 131h80', stroke: 'var(--weather-neutral)', 'stroke-width': 4, 'stroke-linecap': 'round', 'stroke-dasharray': '10 9', fill: 'none' }),
    svg('circle', { cx: 159, cy: 49, r: 16, fill: 'var(--surface)', stroke: 'var(--weather-neutral)', 'stroke-width': 2 }),
    svg('path', { d: 'M159 33a16 16 0 0 1 0 32Z', fill: 'var(--weather-neutral)', opacity: '.55' })));
  if (state === 'unknown') drawing.append(svg('g', { class: 'pilot-weather-neutral' },
    svg('circle', { cx: 115, cy: 78, r: 17, fill: 'var(--surface)', 'fill-opacity': '.75' }),
    svg('text', { x: 115, y: 85, 'text-anchor': 'middle', fill: 'var(--weather-neutral)', 'font-size': 24, 'font-weight': 700 }, '?')));
  return drawing;
}
