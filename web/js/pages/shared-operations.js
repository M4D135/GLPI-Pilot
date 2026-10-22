import { el, badge, toolbar, table, dialog } from '../ui.js';

export const sharedOperationKinds = {
  'personal-note': 'Note de ticket partagée',
  tour: 'Tournée partagée',
  dispatch: 'Répartition partagée',
  technicians: 'Compétences et capacités partagées'
};

const call = (ctx, operation, args = {}) => ctx.api.call(operation, args, { signal: ctx.signal });

/** Publish only the already saved business document selected by the user. */
export async function publishSharedOperation(ctx, operation, args = {}) {
  const contribution = await call(ctx, operation, args);
  ctx.notify('Contribution enregistrée dans l’espace d’équipe. Elle sera transmise au central à la prochaine synchronisation.', 'success');
  return contribution;
}

/** The runtime binds this review to the source document and the exact local baseline. */
export async function reviewSharedOperation(ctx, id) {
  const review = await call(ctx, 'workspace.operation.preview', { id });
  if (ctx.signal?.aborted) return;
  const content = el('div', { class: 'library-detail' },
    toolbar(badge(sharedOperationKinds[review.kind] || 'Travail partagé'), badge(review.destinationLabel || 'Copie locale')),
    el('p', { text: review.summary || '' }),
    (review.changes || []).length ? table(review.changes, [
      { key: 'label', label: 'Élément' },
      { key: 'current', label: 'Sur ce poste', render: row => el('pre', { class: 'library-detail-text', text: row.current || '—' }) },
      { key: 'incoming', label: 'Version partagée', render: row => el('pre', { class: 'library-detail-text', text: row.incoming || '—' }) }
    ], { maxRows: 100 }) : null,
    ...(review.warnings || []).map(text => el('p', { class: 'help', text })));
  dialog(review.title || 'Examiner le travail partagé', content, [
    { label: 'Fermer', action: close => close() },
    { label: review.destinationLabel || 'Reprendre sur ce poste', primary: true, disabled: !review.canApply, action: async close => {
      const applied = await call(ctx, 'workspace.operation.apply', { token: review.token });
      close(); ctx.notify(applied.message || 'Le travail partagé est disponible sur ce poste.', 'success');
      if (applied.route) await ctx.navigate(applied.route, applied.params || {});
    } }
  ]);
}
