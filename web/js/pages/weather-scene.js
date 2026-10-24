import { svg } from './analytics-ui.js';

let sceneSequence = 0;

// The scene illustrates the state supplied by the engine. It never derives a
// forecast, severity or score from the value shown beside it.
export function weatherScene(value) {
  const state = ['sun', 'watch', 'rain', 'storm', 'partial', 'unknown'].includes(value) ? value : 'unknown';
  const id = `pilot-weather-${++sceneSequence}`;
  const drawing = svg('svg', { viewBox: '0 0 220 146', class: 'pilot-weather-scene analytics-volume-weather-icon',
    'data-weather-scene': state, 'aria-hidden': 'true', focusable: 'false' });
  drawing.append(svg('defs', {},
    svg('radialGradient', { id: `${id}-halo` }, svg('stop', { offset: '0', 'stop-color': '#ffd775', 'stop-opacity': '.6' }),
      svg('stop', { offset: '1', 'stop-color': '#ffbd55', 'stop-opacity': '0' })),
    svg('linearGradient', { id: `${id}-sun`, x1: '0', y1: '0', x2: '0', y2: '1' },
      svg('stop', { offset: '0', 'stop-color': '#ffe69a' }), svg('stop', { offset: '1', 'stop-color': '#ffad39' })),
    svg('linearGradient', { id: `${id}-cloud`, x1: '0', y1: '0', x2: '.2', y2: '1' },
      svg('stop', { offset: '0', 'stop-color': 'var(--weather-cloud-light)' }),
      svg('stop', { offset: '1', 'stop-color': 'var(--weather-cloud-deep)' })),
    svg('clipPath', { id: `${id}-rain` }, svg('rect', { x: 20, y: 90, width: 180, height: 54 }))));

  if (state === 'sun' || state === 'watch') {
    const sun = svg('g', { class: 'pilot-weather-sun' });
    sun.append(svg('circle', { class: 'pilot-weather-halo', cx: 91, cy: 64, r: 62, fill: `url(#${id}-halo)` }));
    const rays = svg('g', { class: 'pilot-weather-rays', fill: 'none', stroke: '#edab28', 'stroke-width': 3.8, 'stroke-linecap': 'round' });
    for (let index = 0; index < 12; index++) {
      const angle = index * Math.PI / 6;
      rays.append(svg('line', { x1: 91 + Math.cos(angle) * 35, y1: 64 + Math.sin(angle) * 35,
        x2: 91 + Math.cos(angle) * (index % 2 ? 44 : 50), y2: 64 + Math.sin(angle) * (index % 2 ? 44 : 50) }));
    }
    sun.append(rays, svg('circle', { class: 'pilot-weather-sun-disc', cx: 91, cy: 64, r: 27, fill: `url(#${id}-sun)` }),
      svg('path', { d: 'M77 49c5-5 13-7 20-5', fill: 'none', stroke: '#fff5cc', 'stroke-width': 4, 'stroke-linecap': 'round', opacity: '.8' }));
    drawing.append(sun);
  }
  if (state !== 'sun') {
    drawing.append(svg('g', { class: 'pilot-weather-cloud-far' },
      svg('path', { d: 'M72 69c-14 0-17-18-4-23 0-18 23-24 33-11 12-8 27-1 28 12 18-2 21 24 4 25H72Z',
        fill: 'var(--weather-cloud-far)', opacity: '.65' })));
    drawing.append(svg('g', { class: 'pilot-weather-cloud-near' },
      svg('path', { d: 'M67 101c-23 0-29-29-8-38-2-28 34-42 53-19 20-14 43-1 44 21 27-1 33 37 6 39H67Z',
        fill: `url(#${id}-cloud)`, stroke: 'var(--weather-cloud-edge)', 'stroke-width': '1.3' }),
      svg('path', { d: 'M76 62c2-12 17-19 28-11', fill: 'none', stroke: 'var(--weather-cloud-highlight)', 'stroke-width': 4, 'stroke-linecap': 'round', opacity: '.65' })));
  } else {
    drawing.append(svg('g', { class: 'pilot-weather-cloud-far pilot-weather-fair-cloud' },
      svg('path', { d: 'M137 111c-12 0-16-15-5-21 1-15 20-20 29-10 11-7 23 1 23 12 15 0 17 21 2 21H137Z',
        fill: 'var(--weather-cloud-light)', opacity: '.6' })));
  }
  if (state === 'rain' || state === 'storm') {
    const drops = svg('g', { 'clip-path': `url(#${id}-rain)`, class: 'pilot-weather-rain', fill: 'none', stroke: 'var(--weather-rain)', 'stroke-width': 3.7, 'stroke-linecap': 'round' });
    for (let index = 0; index < (state === 'storm' ? 7 : 5); index++) {
      const drop = svg('path', { class: 'pilot-weather-rain-particle', d: `M${63 + index * 17} 101l-6 12` });
      drop.style.setProperty('--weather-drop-delay', `${-(index * .29 + .15)}s`); drops.append(drop);
    }
    drawing.append(drops);
    if (state === 'storm') drawing.append(svg('path', { class: 'pilot-weather-lightning', d: 'M115 87l-14 25h13l-6 22 25-32h-15l9-15Z',
      fill: '#ffc55a', stroke: '#f3ac37', 'stroke-width': 1 }));
  }
  if (state === 'partial') drawing.append(svg('g', { class: 'pilot-weather-neutral' },
    svg('path', { d: 'M64 119h68M81 129h70', stroke: 'var(--weather-neutral)', 'stroke-width': 4, 'stroke-linecap': 'round', 'stroke-dasharray': '9 8', fill: 'none' }),
    svg('circle', { cx: 159, cy: 49, r: 16, fill: 'var(--surface)', stroke: 'var(--weather-neutral)', 'stroke-width': 2 }),
    svg('path', { d: 'M159 33a16 16 0 0 1 0 32Z', fill: 'var(--weather-neutral)', opacity: '.55' })));
  if (state === 'unknown') drawing.append(svg('g', { class: 'pilot-weather-neutral' },
    svg('circle', { cx: 115, cy: 78, r: 17, fill: 'var(--surface)', 'fill-opacity': '.75' }),
    svg('text', { x: 115, y: 85, 'text-anchor': 'middle', fill: 'var(--weather-neutral)', 'font-size': 24, 'font-weight': 700 }, '?')));
  return drawing;
}
