import { renderDashboard } from './analytics-dashboard.js';
import { renderQueries } from './analytics-queries.js';
import { renderHistory, renderPopulations } from './analytics-history.js';
import { renderAssistant } from './analytics-assistant.js';

export const pages = [
  { id: 'dashboard', title: 'Dashboard', group: 'Analyser', icon: 'chart', render: renderDashboard },
  { id: 'queries', title: 'Atelier de requêtes', group: 'Analyser', icon: 'filter', render: renderQueries },
  { id: 'history', title: 'Historique et analyses', group: 'Analyser', icon: 'history', render: renderHistory },
  { id: 'populations', title: 'Effectifs datés', group: 'Analyser', icon: 'map', render: renderPopulations },
  { id: 'assistant', title: 'Assistant de langage', group: 'Analyser', icon: 'spark', render: renderAssistant }
];
