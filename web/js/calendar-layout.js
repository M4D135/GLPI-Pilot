// Calendar geometry uses wall-clock components, never the browser's implicit
// time zone. The engine remains the authority for actual UTC reservations.
const pad = value => String(value).padStart(2, '0');
export const localDay = value => String(value || '').slice(0, 10);
export const localTime = value => String(value || '').slice(11, 16);
export function dayDate(value) {
 const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(localDay(value));
 if (!match) return null;
 const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]), 12));
 return date.toISOString().slice(0, 10) === match[0] ? date : null;
}
export function addDays(value, delta) { const date = dayDate(value); if (!date) return ''; date.setUTCDate(date.getUTCDate() + delta); return date.toISOString().slice(0, 10); }
export function addMonths(value, delta) { const date = dayDate(value); if (!date) return ''; const target = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + delta + 1, 0, 12)); date.setUTCDate(Math.min(date.getUTCDate(), target.getUTCDate())); date.setUTCFullYear(target.getUTCFullYear(), target.getUTCMonth()); return date.toISOString().slice(0, 10); }
export function weekStart(value) { const date = dayDate(value); return date ? addDays(value, -((date.getUTCDay() + 6) % 7)) : ''; }
export function dateLabel(value, options = { weekday: 'short', day: 'numeric', month: 'short' }) { const date = dayDate(value); return date ? new Intl.DateTimeFormat('fr-FR', { ...options, timeZone: 'UTC' }).format(date) : String(value || ''); }
export function zoneLocal(value, timeZone) {
 if (!value) return '';
 try { const parts = Object.fromEntries(new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(new Date(value)).map(part => [part.type, part.value])); return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`; }
 catch { return String(value).slice(0, 16); }
}
export function today(timeZone) { return localDay(zoneLocal(new Date().toISOString(), timeZone)); }
export function wallMinutes(value) { const day = dayDate(value); if (!day) return NaN; return Date.UTC(day.getUTCFullYear(), day.getUTCMonth(), day.getUTCDate(), Number(String(value).slice(11, 13) || 0), Number(String(value).slice(14, 16) || 0)) / 60000; }
export function wallAdd(value, minutes) { const date = new Date((wallMinutes(value) + minutes) * 60000); return Number.isFinite(date.getTime()) ? date.toISOString().slice(0, 16) : ''; }
export function atMinute(day, minutes) { return wallAdd(`${day}T00:00`, minutes); }
export function minuteOf(value) { return Number(String(value).slice(11, 13) || 0) * 60 + Number(String(value).slice(14, 16) || 0); }
export function minuteLabel(value) { return `${pad(Math.floor(value / 60))}:${pad(value % 60)}`; }
export function calendarRange(date, view) {
 if (view === 'day') return { from: date, to: addDays(date, 1), days: [date] };
 if (view === 'month') { const first = date.slice(0, 7) + '-01', from = weekStart(first); return { from, to: addDays(from, 42), days: Array.from({ length: 42 }, (_, i) => addDays(from, i)) }; }
 const from = weekStart(date), length = view === 'agenda' ? 14 : 7;
 return { from, to: addDays(from, length), days: Array.from({ length }, (_, i) => addDays(from, i)) };
}
export function displayOccurrence(value, timeZone) { return { ...value, displayStart: value.startUtc ? zoneLocal(value.startUtc, timeZone) : value.startLocal, displayEnd: value.endUtc ? zoneLocal(value.endUtc, timeZone) : value.endLocal }; }
export function intersectsDay(occurrence, day) { return occurrence.displayStart < `${addDays(day, 1)}T00:00` && occurrence.displayEnd > `${day}T00:00`; }
export function daySegment(occurrence, day) {
 return { occurrence, start: occurrence.displayStart < `${day}T00:00` ? 0 : minuteOf(occurrence.displayStart), end: occurrence.displayEnd >= `${addDays(day, 1)}T00:00` ? 1440 : minuteOf(occurrence.displayEnd) };
}
// Adjacent reservations share a lane; simultaneous reservations receive equal
// widths within their collision cluster. No ticket is hidden behind another.
export function arrangeOverlaps(segments) {
 const sorted = segments.map(value => ({ ...value })).sort((a, b) => a.start - b.start || b.end - a.end || String(a.occurrence.key).localeCompare(String(b.occurrence.key)));
 let cluster = [], lanes = [], clusterEnd = -1;
 const finish = () => { for (const item of cluster) item.columns = lanes.length; cluster = []; lanes = []; };
 for (const item of sorted) {
  if (cluster.length && item.start >= clusterEnd) finish();
  if (!cluster.length) clusterEnd = item.end; else clusterEnd = Math.max(clusterEnd, item.end);
  let column = lanes.findIndex(end => end <= item.start); if (column < 0) column = lanes.length;
  lanes[column] = item.end; item.column = column; cluster.push(item);
 }
 finish(); return sorted;
}
export function summaryFor(occurrences) {
 const unique = [...new Map(occurrences.map(item => [item.key, item])).values()], active = unique.filter(item => item.state !== 'cancelled'), interventions = active.filter(item => item.kind !== 'unavailability');
 const duration = item => Math.max(0, (Date.parse(item.endUtc) - Date.parse(item.startUtc)) / 3600000 || (wallMinutes(item.endLocal) - wallMinutes(item.startLocal)) / 60);
 return { interventions: interventions.length, unavailabilities: active.length - interventions.length, completed: interventions.filter(item => item.state === 'done').length, cancelled: unique.length - active.length, unassigned: interventions.filter(item => !(item.participantKeys || []).length).length, linkedTickets: new Set(interventions.flatMap(item => (item.tickets || []).map(ticket => ticket.key))).size, interventionHours: interventions.reduce((sum, item) => sum + duration(item), 0), personHours: interventions.reduce((sum, item) => sum + duration(item) * new Set(item.participantKeys || []).size, 0) };
}
