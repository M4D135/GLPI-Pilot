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
    active?.abort();
    active = null;
  }
  async function execute(ticketKey, token, manual) {
    if (disposed || token !== generation) return;
    const controller = new AbortController();
    active = controller;
    publish('reading');
    try {
      const result = await read(ticketKey, controller.signal);
      if (disposed || token !== generation || controller.signal.aborted) return;
      fresh.set(ticketKey, Date.now());
      publish('fresh');
      return result;
    } catch (error) {
      if (disposed || token !== generation || controller.signal.aborted || error?.name === 'AbortError') return;
      publish('error', error);
      if (manual) throw error;
    } finally {
      if (active === controller) active = null;
    }
  }
  function schedule(ticketKey, enabled = true) {
    cancel();
    if (disposed) return;
    if (!ticketKey || !enabled) { publish('unavailable'); return; }
    if (Date.now() - (fresh.get(ticketKey) ?? 0) < freshnessMs) { publish('fresh'); return; }
    publish('waiting');
    const token = generation;
    timer = setTimeout(() => {
      timer = null;
      void execute(ticketKey, token, false);
    }, delayMs);
  }
  function refresh(ticketKey) {
    cancel();
    if (disposed || !ticketKey) return Promise.resolve();
    return execute(ticketKey, generation, true);
  }
  function dispose() { cancel(); disposed = true; fresh.clear(); }
  return { schedule, refresh, cancel, dispose };
}
