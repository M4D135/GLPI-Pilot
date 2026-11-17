import hashlib,json,pathlib,subprocess
BASE='2ce599f2e15216fca1ebefcb2b02aa665d5680e1'
subprocess.run(['git','merge-base','--is-ancestor',BASE,'HEAD'],check=True)
out=pathlib.Path('artifacts/reader-protection');out.mkdir(parents=True,exist_ok=True)
def blob(raw):return hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()
def read(path,expected):
 raw=subprocess.check_output(['git','show','HEAD:'+path]);assert blob(raw)==expected,(path,blob(raw));return raw.decode('utf-8')
def change(old,new):
 global text
 assert text.count(old)==1,(old,text.count(old));text=text.replace(old,new)
files=[]
def store(path,expected=None):
 raw=text.encode('utf-8');sha=blob(raw)
 if expected:assert sha==expected,(path,sha)
 pathlib.Path(path).write_bytes(raw)
 (out/pathlib.Path(path).name).write_bytes(raw)
 files.append({'path':path,'sha':sha})
path='web/js/pages/workplace-tickets.js'
text=read(path,'c7baed7522a9c283d7d2c3327c78c66d197b699e')
change("  let readStatus = 'idle', readError = null, readIndicator = null;", "  let readStatus = 'idle', readError = null, readIndicator = null;\n  let readSuspended = false, openingTicket = false, localLookup = null;")
change("  const eligibleForLiveRead = ticket => !simulation && !!ticket?.canRead && !ctx.state.session?.cachedSource && !tool;", """  const eligibleForLiveRead = ticket => !simulation && !!ticket?.canRead && !ctx.state.session?.cachedSource;
  const editing = () => reader.contains(document.activeElement)
    && document.activeElement?.matches('input,textarea,select,[contenteditable=true]');
  function resumeLiveRead() {
    if (disposed || readSuspended || openingTicket || !current || ctx.signal?.aborted) return;
    if (tool || localLookup || editing() || document.querySelector('dialog[open]')) {
      liveRead.cancel(); showReadStatus('paused'); return;
    }
    liveRead.schedule(current.personal.key, eligibleForLiveRead(current));
  }
  function pauseForEditor(event) {
    if (disposed || !current) return;
    if (event.type === 'pilot:editor' ? event.detail : editing()) {
      liveRead.cancel(); showReadStatus('paused');
    } else queueMicrotask(resumeLiveRead);
  }
  // A read must not replace an editor, an open dialog, or the button waiting for
  // a local cache lookup. Pauses end only in the still-visible, current reader.
  window.addEventListener('pilot:editor', pauseForEditor, { signal: ctx.signal });
  reader.addEventListener('focusin', pauseForEditor);
  reader.addEventListener('focusout', () => queueMicrotask(resumeLiveRead));
  function cancelLocalLookup() {
    const previous = localLookup; localLookup = null;
    if (previous) { previous.controller.abort(); previous.modal?.close(); }
  }
  async function showPrecedents(event) {
    if (localLookup || !current || disposed) return;
    const ticket = current, generation = version;
    const lookup = { controller: new AbortController(), modal: null };
    localLookup = lookup; liveRead.cancel(); showReadStatus('paused');
    const release = () => {
      if (localLookup !== lookup) return;
      localLookup = null;
      if (version === generation && current?.personal.key === ticket.personal.key) resumeLiveRead();
    };
    try {
      const signal = ctx.signal ? AbortSignal.any([ctx.signal, lookup.controller.signal]) : lookup.controller.signal;
      const modal = await openLibraryPrecedents({ api: ctx.api, signal, state: ctx.state, navigate: ctx.navigate },
        { id: ticket.personal.id, url: ticket.personal.url }, { returnFocus: event.currentTarget });
      lookup.modal = modal;
      if (signal.aborted || disposed || version !== generation || localLookup !== lookup) { modal?.close(); release(); return; }
      if (modal?.element.isConnected) modal.element.addEventListener('pilot:closed', release, { once: true });
      else release();
    } catch (error) { release(); throw error; }
  }""")
change("    const generation = ++version;\n    liveRead.cancel(); showReadStatus('idle');", "    const generation = ++version; openingTicket = true; readSuspended = false;\n    cancelLocalLookup(); liveRead.cancel(); showReadStatus('idle');")
change("    if (disposed || generation !== version) return;\n    onTicketOpened?.(current, args);", "    if (disposed || generation !== version) return;\n    openingTicket = false;\n    onTicketOpened?.(current, args);")
change("    if (!disposed && generation === version) liveRead.schedule(current.personal.key, eligibleForLiveRead(current));", "    if (!disposed && generation === version) resumeLiveRead();")
change("      button('Chercher des solutions similaires', run(ctx, event => openLibraryPrecedents({ api: ctx.api, signal: ctx.signal, state: ctx.state, navigate: ctx.navigate }, { id: ticket.personal.id, url: ticket.personal.url }, { returnFocus: event.currentTarget })), { icon: 'search' }),", "      button('Chercher des solutions similaires', run(ctx, showPrecedents), { icon: 'search' }),")
change("              liveRead.schedule(current.personal.key, eligibleForLiveRead(current));", "              resumeLiveRead();")
change("  const cleanup = async () => { liveRead.dispose(); await closeTool(); await personal?.dispose(); personal = null; mapController?.dispose?.(); mapController = null; disposed = true; version++; };", "  const cleanup = async () => { readSuspended = true; cancelLocalLookup(); liveRead.dispose(); window.removeEventListener('pilot:editor', pauseForEditor); await closeTool(); await personal?.dispose(); personal = null; mapController?.dispose?.(); mapController = null; disposed = true; version++; };")
change("  cleanup.pauseLiveRead = () => { liveRead.cancel(); showReadStatus('paused'); };\n  cleanup.resumeLiveRead = () => {\n    if (current && !disposed) liveRead.schedule(current.personal.key, eligibleForLiveRead(current));\n  };", "  cleanup.pauseLiveRead = () => { readSuspended = true; cancelLocalLookup(); liveRead.cancel(); showReadStatus('paused'); };\n  cleanup.resumeLiveRead = () => { readSuspended = false; resumeLiveRead(); };")
store(path,'8c9f95d2a04c8bbf83e8c085e475b5c7da189928')
path='tests/WebViewChecks/Program.Resolution.cs'
text=read(path,'885e3afd81a27f344de6b2429e1d392b3ec054bd')
change('''            await page.SetViewportSizeAsync(1440, 1000);
            await ClickNavigationAsync(page, "records");''','''            // This cache-only workflow inspects the reader before automatic refresh.
            // Held timers still exercise production scheduling and cancellation.
            await page.EvaluateAsync("""
                () => {
                  const schedule=window.setTimeout.bind(window),cancel=window.clearTimeout.bind(window);
                  let serial=0;window.resolutionReadTimers=new Map();
                  window.setTimeout=(callback,delay,...args)=>{
                    if(delay!==500)return schedule(callback,delay,...args);
                    const id=--serial;resolutionReadTimers.set(id,()=>callback(...args));return id;
                  };
                  window.clearTimeout=id=>{if(!resolutionReadTimers.delete(id))cancel(id);};
                  window.flushResolutionReads=()=>{
                    const callbacks=[...resolutionReadTimers.values()];resolutionReadTimers.clear();
                    callbacks.forEach(callback=>callback());
                  };
                }
                """);
            await page.SetViewportSizeAsync(1440, 1000);
            await ClickNavigationAsync(page, "records");''')
change('''            await notes.FillAsync(unsavedNote); var noteNode = await notes.ElementHandleAsync();''','''            await page.WaitForFunctionAsync("()=>resolutionReadTimers.size===1");
            await notes.FillAsync(unsavedNote); var noteNode = await notes.ElementHandleAsync();
            Require(await page.EvaluateAsync<int>("()=>resolutionReadTimers.size") == 0,
                "Typing in the personal editor cancels the delayed refresh without replacing the editor.");''')
change('''            await workButton.EvaluateAsync("node => node.blur()");
            Require(await workButton.IsDisabledAsync()''','''            await workButton.EvaluateAsync("node => node.blur()");
            await page.EvaluateAsync("()=>flushResolutionReads()");
            Require(await page.EvaluateAsync<int>("()=>resolutionReadTimers.size") == 0
                && !calls.Contains("workplace.ticket.read"),
                "The asynchronous local lookup keeps refresh paused even before its dialog exists and after the button loses focus.");
            Require(await workButton.IsDisabledAsync()''')
change('''            referenceRelease.TrySetResult(); await Settled();
            Require(await cards.CountAsync()''','''            referenceRelease.TrySetResult(); await Settled();
            Require(await page.EvaluateAsync<int>("()=>resolutionReadTimers.size") == 0,
                "An open solutions dialog cannot be replaced by delayed ticket refresh.");
            Require(await cards.CountAsync()''')
change('''            await Capture("resolution-work-ticket"); await Button("Fermer").ClickAsync();''','''            await Capture("resolution-work-ticket"); await Button("Fermer").ClickAsync();
            await page.WaitForFunctionAsync("()=>resolutionReadTimers.size===1");
            Require(true, "Closing the solutions dialog schedules one fresh delay for the unchanged visible ticket.");''')
change('''            await missingDialog.WaitForAsync();
            var missingChecks''','''            await missingDialog.WaitForAsync();
            await page.EvaluateAsync("()=>flushResolutionReads()");
            Require(await page.EvaluateAsync<int>("()=>resolutionReadTimers.size") == 0,
                "The absent-cache dialog protects its note editor and cancels delayed refresh too.");
            var missingChecks''')
change('''                "Escape from the absent-cache message restores its exact initiating button and preserves the same unsaved editor after focus loss.");''','''                "Escape from the absent-cache message restores its exact initiating button and preserves the same unsaved editor after focus loss.");
            await page.WaitForFunctionAsync("()=>resolutionReadTimers.size===1");
            await page.GetByRole(AriaRole.Button, new() { Name = "Retour à la liste", Exact = true }).ClickAsync();
            await page.EvaluateAsync("()=>flushResolutionReads()");
            Require(await page.EvaluateAsync<int>("()=>resolutionReadTimers.size") == 0
                && !calls.Contains("workplace.ticket.read") && await Modal().CountAsync() == 0,
                "Returning to the list cancels the resumed delay and cannot reopen a library dialog.");''')
store(path)
(out/'files.json').write_text(json.dumps(files,indent=2))
subprocess.run(['git','diff','--check'],check=True)
assert set(subprocess.check_output(['git','diff','--name-only'],text=True).splitlines())==set(f['path'] for f in files)
print(json.dumps(files,indent=2))
