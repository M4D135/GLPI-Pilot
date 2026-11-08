// Ordinary terrain actions still refuse re-entry. Only explicit lifecycle actions
// (such as closing a panel) opt into waiting for the current activity to finish.
export function createTerrainActivity(signal) {
  let current = null;
  return {
    begin() {
      if (signal?.aborted || current) return null;
      let resolve;
      const entry = { done: new Promise(done => { resolve = done; }) };
      current = entry;
      let released = false;
      return () => {
        if (released) return;
        released = true;
        if (current === entry) current = null;
        resolve();
      };
    },
    async afterCurrent(action) {
      // Recheck after each await: another queued action may have acquired the
      // activity before this continuation resumes. Never drop the close request.
      while (current) {
        if (signal?.aborted) return;
        await current.done;
      }
      if (!signal?.aborted) return action();
    }
  };
}

// The dialog can be visible while its module is importing or its renderer awaits
// data. Remember one close request until the cleanup and dirty guard are known.
export function createReadyAction(action) {
  let resolveReady;
  const ready = new Promise(resolve => { resolveReady = resolve; });
  let pending = null;
  return {
    settled() { resolveReady(); },
    request() {
      if (pending) return pending;
      const attempt = ready.then(action).finally(() => {
        if (pending === attempt) pending = null;
      });
      pending = attempt;
      return attempt;
    }
  };
}
