import { renderReference } from './terrain-reference.js';
import { renderReview } from './terrain-review.js';
import { renderTours } from './terrain-tours.js';
import { renderUpdates } from './terrain-update.js';

export const pages = [
  { id: 'terrain-reference', title: 'Référentiel géographique', group: 'Terrain', icon: 'map', render: renderReference },
  { id: 'terrain-review', title: 'Localisations à revoir', group: 'Terrain', icon: 'search', render: renderReview },
  { id: 'tours', title: 'Tournées et calendrier', group: 'Terrain', icon: 'route', render: renderTours },
  { id: 'updates', title: 'Mise à jour et retour arrière', group: 'Configuration', icon: 'download', render: renderUpdates }
];
