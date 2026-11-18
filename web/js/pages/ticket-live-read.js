// Keep the ticket immediately usable from its local observation while a
// deliberately delayed, cancellable GLPI read checks for fresher information.
// No hover, list rendering or preselection is allowed to initiate a read.
export const TICKET_LIVE_READ_DELAY_MS = 500;

export function createTicketLiveRead({ read, onState = () => {}, delayMs = TICKET_LIVE_READ_DELAY_MS, freshnessMs = 30000 }) {
  let timer = null, active = null, generation = 0, disposed = false;
  const fresh = new Map();
  const publish = (state, error) => { if (!disposed) onState(state, error); };

  function cancel() {
    ++generation;
    if (timer !== null) clearTimeout(timer);
    timer = null;
    active?.controller.abort();
    active = null;
  }
  async function execute(ticketKey, token, manual, sourceToken) {
    if (disposed || token !== generation) return;
    const controller = new AbortController();
    active = { ticketKey, sourceToken, controller };
    publish('reading');
    try {
      const result = await read(ticketKey, controller.signal, { automatic: !manual });
      if (disposed || token !== generation || controller.signal.aborted) return;
      fresh.set(ticketKey, { sourceToken, readAt: Date.now() });
      publish('fresh');
      return result;
    } catch (error) {
      if (disposed || token !== generation || controller.signal.aborted || error?.name === 'AbortError') return;
      publish('error', error);
      if (manual) throw error;
    } finally {
      if (active?.controller === controller) active = null;
    }
  }
  function schedule(ticketKey, enabled = true, sourceToken = '') {
    // Focus changes while the successful response is being drawn can resume
    // the same ticket before read() completes. Keep that pending result alive.
    if (enabled && ticketKey && active?.ticketKey === ticketKey && active.sourceToken === sourceToken) return;
    cancel();
    if (disposed) return;
    if (!ticketKey || !enabled) { publish('unavailable'); return; }
    const recent = fresh.get(ticketKey);
    if (recent?.sourceToken === sourceToken && Date.now() - recent.readAt < freshnessMs) { publish('fresh'); return; }
    publish('waiting');
    const token = generation;
    timer = setTimeout(() => {
      timer = null;
      void execute(ticketKey, token, false, sourceToken);
    }, delayMs);
  }
  function refresh(ticketKey, sourceToken = '') {
    cancel();
    if (disposed || !ticketKey) return Promise.resolve();
    return execute(ticketKey, generation, true, sourceToken);
  }
  function dispose() { cancel(); disposed = true; fresh.clear(); }
  return { schedule, refresh, cancel, dispose };
}
