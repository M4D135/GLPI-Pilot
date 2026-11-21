const qualityCounters = ['failed', 'partial', 'conflicts', 'unconfirmed', 'retryCount'];
const validCount = value => Number.isSafeInteger(value) && value >= 0;

// A finished command is not proof that its contents were confirmed. Old reports
// may lack the computed verdict; accept them only when every required count and
// the final scope proof are present. Never infer confirmation from read totals.
export function collectionQuality(report, completed = report?.status === 'completed', readiness = null) {
  const readinessFields = ['tickets', 'eligibleTickets', 'incomplete', 'missingContent', 'unconfirmed', 'conflicts'];
  const ready = readiness == null || readiness.available === true && readiness.isReady === true
    && readinessFields.every(key => validCount(readiness[key])) && readiness.tickets > 0
    && readiness.eligibleTickets === readiness.tickets
    && ['incomplete', 'missingContent', 'unconfirmed', 'conflicts'].every(key => readiness[key] === 0);
  const knownIssue = qualityCounters.some(key => validCount(report?.[key]) && report[key] > 0)
    || report?.finalScopeVerified === false
    || validCount(report?.targeted) && validCount(report?.processed) && report.processed !== report.targeted
    || readiness?.available === true && readiness.isReady === false || report?.collectionState === 'attention';
  const verifiedCounts = validCount(report?.targeted) && report.targeted > 0
    && validCount(report?.processed) && report.processed === report.targeted
    && qualityCounters.every(key => validCount(report?.[key]) && report[key] === 0)
    && report?.finalScopeVerified === true;
  const successful = completed && (report?.status == null || report.status === 'completed') && report?.isSuccessful !== false
    && (report?.collectionState == null || report.collectionState === 'completed')
    && verifiedCounts && report?.needsAttention !== true && ready;
  return {
    successful,
    needsAttention: completed && !successful,
    state: successful ? 'complete' : completed && (knownIssue || report?.isSuccessful === false || report?.needsAttention === true) ? 'incomplete' : 'unknown'
  };
}

export function collectionResultLabel(report, completed = report?.status === 'completed', readiness = null) {
  const quality = collectionQuality(report, completed, readiness);
  if (!completed) return report?.statusLabel || 'Bilan conservé';
  return quality.successful ? 'Passage validé' : quality.state === 'incomplete' ? 'Passage à compléter' : 'Qualité du passage à vérifier';
}

export function collectionQualityCounts(report) {
  return [['unconfirmed', 'Versions non confirmées'], ['partial', 'Lectures partielles'],
    ['failed', 'Lectures en échec'], ['conflicts', 'Conflits conservés']]
    .map(([key, label]) => ({ key, label, value: validCount(report?.[key]) ? report[key] : null }));
}
