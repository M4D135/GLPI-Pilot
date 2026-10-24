import { renderTickets } from './workplace-tickets.js';
import { renderDay, renderEnrichment } from './workplace-day.js';
import { renderTechnicians, renderPlanning } from './workplace-technicians.js';
import { simulationNotice } from '../ui.js';

export const pages = [
  { id: 'day', title: 'Ma journée', group: 'Travailler', icon: 'sun', render: renderDay },
  { id: 'tickets', title: 'Tickets', group: 'Travailler', icon: 'ticket', render: renderTickets },
  { id: 'enrichment', title: 'Lectures ciblées', group: 'Travailler', icon: 'list', render: renderEnrichment },
  { id: 'technicians', title: 'Équipe', group: 'Organiser', icon: 'users', render: renderTechnicians },
  { id: 'planning', title: 'Répartition', group: 'Organiser', icon: 'calendar', render: renderPlanning }
].map(page=>({...page,render:async ctx=>{
  const cleanup=await page.render(ctx);
  if(!ctx.signal.aborted){const notice=simulationNotice(ctx.state.session,'Les dossiers, notes, rappels et actions de cet espace appartiennent à la simulation. Vos essais peuvent modifier les tickets simulés et leurs indicateurs.');if(notice)ctx.root.prepend(notice);}
  return cleanup;
}}));
