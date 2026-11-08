using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal sealed partial class WindowsWebViewFixture
{
    private async Task CheckPresentationPdfAsync(IPage page, Process process)
    {
        var source = Path.Combine(_sandbox, "presentation-source.csv");
        await File.WriteAllTextAsync(source, "ID;Titre;Statut;Attribué à - Technicien;Attribué à - Groupe de techniciens\n"
            + "7901;Incident site fictif Nord;Nouveau;Alex fictif;Support fictif\n"
            + "7902;Incident site fictif Nord;En cours (Attribué);Alex fictif;Support fictif\n"
            + "7903;Incident site fictif Nord;En attente;Alex fictif;Support fictif\n"
            + "7904;Incident site fictif Nord;Clos;Alex fictif;Support fictif\n", new UTF8Encoding(true), _deadline.Token);
        var import = page.EvaluateAsync<JsonElement>("async()=>(await import('/js/bridge.js')).api.call('records.import')");
        await CompleteFileDialogAsync(process, source); await import.WaitAsync(TimeSpan.FromSeconds(25), _deadline.Token);
        await NavigateAsync(page, "pilot");
        await WaitForPageAsync(page, "!document.querySelector('#content').hasAttribute('aria-busy')");
        var prepared = await page.EvaluateAsync<JsonElement>("""
            async()=>{
              const {api}=await import('/js/bridge.js');
              const {presentationPdfDocument}=await import('/js/pages/pilot-presentation.js');
              const session=(await api.call('app.bootstrap')).session;
              const model=await api.call('dashboard.get',{businessView:'workspace'});
              const widgets=model.widgets.filter(widget=>widget.available&&widget.settings.kind==='metric').slice(0,2);
              if(widgets.length!==2)throw new Error('The imported source did not produce two real metrics.');
              const ids=widgets.map(widget=>widget.id),deck={version:1,slides:[
                {id:'native-overview',title:'Synthèse native',items:[
                  {id:'first',widgetId:ids[0],x:0,y:0,width:490,height:1000},
                  {id:'second',widgetId:ids[1],x:510,y:0,width:490,height:1000}]},
                {id:'native-detail',title:'Détail natif',items:[
                  {id:'repeated',widgetId:ids[0],x:0,y:0,width:1000,height:1000}]}]};
              const args={runId:model.runId,revision:model.revision,selectedWidgetIds:ids,presentation:deck,includeOverview:false};
              const preview=await api.call('reports.preview',args);
              const sources=new Map([...document.querySelectorAll('.analytics-widget[data-widget-id]')].map(node=>[node.dataset.widgetId,node]));
              window.__nativePresentationCleanup=presentationPdfDocument(preview,ids,sources);
              window.__nativePresentationExport={...args,selectionToken:preview.selectionToken,format:'pdf'};
              await document.fonts.ready;await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
              return {fileSource:session.fileSource,cachedSource:session.cachedSource,rows:session.rows,
                pages:document.querySelectorAll('[data-presentation-export] .presentation-export-page').length,
                layout:document.querySelector('[data-presentation-export]')?.dataset.presentationLayout,
                values:preview.widgets.map(widget=>widget.metricValue)};
            }
            """);
        Check(prepared.GetProperty("fileSource").GetBoolean() && !prepared.GetProperty("cachedSource").GetBoolean()
            && prepared.GetProperty("rows").GetInt32() == 4 && prepared.GetProperty("pages").GetInt32() == 2
            && prepared.GetProperty("layout").GetString() == "slides",
            "The native Windows host imports a real synthetic CSV and prepares two composed 16:9 pages through the production C# report and presentation renderer.");
        var output = Path.Combine(_output, "presentation-native-16x9.pdf");
        try
        {
            var exporting = page.EvaluateAsync<JsonElement>("async()=>(await import('/js/bridge.js')).api.call('reports.export',window.__nativePresentationExport)");
            // reports.export supplies this exact filename shape to the real
            // SaveFileDialog; the shell can hide the selected .pdf extension.
            await CompleteFileDialogAsync(process, output, new Regex(@"\AGLPI-dashboard-[0-9]{8}-[0-9]{6}(?:\.pdf)?\z",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)));
            var exported = await exporting.WaitAsync(TimeSpan.FromSeconds(30), _deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(_output, "presentation-native-export.json"),
                exported.GetRawText(), new UTF8Encoding(false), _deadline.Token);
            Log("Native presentation export result: " + exported.GetRawText());
            Check(exported.ValueKind == JsonValueKind.Object
                && (!exported.TryGetProperty("cancelled", out var cancelled) || cancelled.ValueKind == JsonValueKind.False)
                && exported.TryGetProperty("format", out var format) && format.GetString() == "pdf"
                && exported.TryGetProperty("path", out var chosenPath) && chosenPath.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(chosenPath.GetString()),
                "The native Save dialog confirms a completed PDF export instead of cancelling it.");
            var actualPath = Path.GetFullPath(exported.GetProperty("path").GetString()!);
            Check(string.Equals(actualPath, Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase),
                "The native Save dialog retains the exact requested fixture output path.");
            Check(File.Exists(actualPath) && new FileInfo(actualPath).Length > 0,
                "The completed native PDF export exists and is nonempty at the confirmed output path.");
            var bytes = await File.ReadAllBytesAsync(output, _deadline.Token);
            var pdf = Encoding.Latin1.GetString(bytes);
            var pages = Regex.Matches(pdf, @"/Type\s*/Page\b").Count;
            var boxes = Regex.Matches(pdf, @"/MediaBox\s*\[\s*0(?:\.0+)?\s+0(?:\.0+)?\s+([0-9.]+)\s+([0-9.]+)\s*\]");
            Check(bytes.AsSpan(0, Math.Min(5, bytes.Length)).SequenceEqual("%PDF-"u8) && pages == 2 && boxes.Count >= 2,
                "The packaged Windows WebView2 print host writes exactly two genuine PDF pages for the two composed slides.");
            Check(boxes.All(box => Math.Abs(double.Parse(box.Groups[1].Value, CultureInfo.InvariantCulture) - 320d / 25.4 * 72) < 2
                && Math.Abs(double.Parse(box.Groups[2].Value, CultureInfo.InvariantCulture) - 180d / 25.4 * 72) < 2),
                "Every native PDF MediaBox is 320 by 180 mm in landscape, without A4 resizing or extra continuation pages.");
        }
        finally
        {
            await page.EvaluateAsync("()=>{window.__nativePresentationCleanup?.();delete window.__nativePresentationCleanup;delete window.__nativePresentationExport;}");
        }
    }

    // Only the isolated fixture process is eligible. This automates the real native
    // Open/Save dialog; the application has no test path or production dialog bypass.
    // Windows refuse SetForegroundWindow a un processus qui ne detient pas deja le premier plan. Sans
    // cette aide, la dialogue d'enregistrement reste derriere la fenetre principale : SendInput frapperait
    // a cote d'elle, et la boucle attendait 12 secondes un premier plan qui ne venait jamais. Attacher notre
    // fil de sortie au fil qui tient le premier plan est la voie documentee pour le deplacer.
    private static void TakeForeground(IntPtr dialog)
    {
        var dialogThread = DialogNative.GetWindowThreadProcessId(dialog, out _);
        var foregroundThread = DialogNative.GetWindowThreadProcessId(DialogNative.GetForegroundWindow(), out _);
        var ownThread = DialogNative.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ownThread
            && DialogNative.AttachThreadInput(foregroundThread, ownThread, true);
        try
        {
            DialogNative.BringWindowToTop(dialog);
            DialogNative.SetForegroundWindow(dialog);
            // Le premier plan se decide sur le fil de la dialogue : le notre peut le demander une fois attache.
            if (attached && dialogThread != ownThread) DialogNative.AttachThreadInput(dialogThread, ownThread, true);
            DialogNative.SetForegroundWindow(dialog);
        }
        finally
        {
            if (attached) DialogNative.AttachThreadInput(foregroundThread, ownThread, false);
            if (attached && dialogThread != ownThread) DialogNative.AttachThreadInput(dialogThread, ownThread, false);
        }
    }

    private async Task CompleteFileDialogAsync(Process process, string path, Regex? suggestedFileName = null)
    {
        if (!_owned.Contains(process) || process.HasExited)
            throw new InvalidOperationException("Native file input requires the running isolated fixture process.");
        if (!Path.IsPathFullyQualified(path) || path.Length > 32767 || path.Any(char.IsControl))
            throw new InvalidOperationException("Native file input requires a bounded absolute path without control characters.");
        IntPtr dialog = IntPtr.Zero;
        await Until(() =>
        {
            DialogNative.EnumWindows((window, _) =>
            {
                DialogNative.GetWindowThreadProcessId(window, out var owner);
                if (owner == process.Id && DialogNative.IsWindowVisible(window) && DialogNative.Class(window) == "#32770")
                { dialog = window; return false; }
                return true;
            }, IntPtr.Zero);
            return dialog != IntPtr.Zero;
        });
        LogNativeFileDialogEdits(process, dialog, "before filename lookup");
        var edit = IntPtr.Zero; var candidateCount = 0;
        try
        {
            await Until(() =>
            {
                var thread = DialogNative.GetWindowThreadProcessId(dialog, out _);
                var info = new DialogNative.GuiThreadInfo { Size = Marshal.SizeOf<DialogNative.GuiThreadInfo>() };
                var focused = DialogNative.GetGUIThreadInfo(thread, ref info) && DialogNative.IsChild(dialog, info.Focus)
                    && DialogNative.Class(info.Focus) == "Edit" ? info.Focus : IntPtr.Zero;
                var edits = new List<IntPtr>();
                DialogNative.EnumChildWindows(dialog, (window, _) =>
                {
                    DialogNative.GetWindowThreadProcessId(window, out var owner);
                    if (owner == process.Id && DialogNative.Class(window) == "Edit" && DialogNative.IsWindowVisible(window)) edits.Add(window);
                    return true;
                }, IntPtr.Zero);
                var fileNames = edits.Where(window =>
                {
                    if (suggestedFileName is not null)
                    {
                        // A semantic Save selector is independent of shell control
                        // IDs. It must match the complete known initial filename.
                        try { return suggestedFileName.IsMatch(NativeFileName(window, 80)); }
                        catch (InvalidOperationException) { return false; } // The control may still be initializing.
                    }
                    if (DialogNative.GetDlgCtrlID(window) is 1148 or 1152) return true; // cmb13 / edt1
                    for (var parent = DialogNative.GetParent(window); parent != IntPtr.Zero && parent != dialog; parent = DialogNative.GetParent(parent))
                        if (DialogNative.GetDlgCtrlID(parent) == 1148) return true;
                    return false;
                }).ToArray();
                candidateCount = fileNames.Length;
                edit = suggestedFileName is not null ? fileNames.Length == 1 ? fileNames[0] : IntPtr.Zero
                    : fileNames.Contains(focused) ? focused : fileNames.Length == 1 ? fileNames[0] : IntPtr.Zero;
                return edit != IntPtr.Zero;
            });
        }
        catch
        {
            Log($"Native filename lookup failed: pid={process.Id}; candidates={candidateCount}; suggestedPattern={suggestedFileName?.ToString() ?? "control IDs"}.");
            LogNativeFileDialogEdits(process, dialog, "failed filename lookup");
            throw;
        }
        Log($"Native filename selected: hwnd={edit.ToInt64():X}; candidates={candidateCount}; selector={(suggestedFileName is null ? "control IDs" : "unique suggested filename")}.");
        var confirm = IntPtr.Zero;
        await Until(() =>
        {
            DialogNative.EnumChildWindows(dialog, (window, _) =>
            {
                if (DialogNative.GetDlgCtrlID(window) == 1 && DialogNative.Class(window) == "Button"
                    && DialogNative.IsWindowVisible(window) && DialogNative.IsWindowEnabled(window))
                { confirm = window; return false; }
                return true;
            }, IntPtr.Zero);
            return confirm != IntPtr.Zero;
        });
        var dialogThread = DialogNative.GetWindowThreadProcessId(dialog, out _);
        var beforeClick = new DialogNative.GuiThreadInfo { Size = Marshal.SizeOf<DialogNative.GuiThreadInfo>() };
        var hasThreadState = DialogNative.GetGUIThreadInfo(dialogThread, ref beforeClick);
        Log($"Native file dialog: pid={process.Id}; dialog={dialog.ToInt64():X}; filename={edit.ToInt64():X}; "
            + $"button={confirm.ToInt64():X}; buttonParent={DialogNative.GetParent(confirm).ToInt64():X}; "
            + $"active={(hasThreadState ? beforeClick.Active.ToInt64().ToString("X") : "unknown")}; "
            + $"foreground={DialogNative.GetForegroundWindow().ToInt64():X}; path={path}");
        await Until(() =>
        {
            DialogNative.GetWindowThreadProcessId(dialog, out var owner);
            if (owner != process.Id || process.HasExited)
                throw new InvalidOperationException("The isolated native file dialog disappeared before activation.");
            var state = new DialogNative.GuiThreadInfo { Size = Marshal.SizeOf<DialogNative.GuiThreadInfo>() };
            if (!DialogNative.GetGUIThreadInfo(dialogThread, ref state)) return false;
            if (state.Active == dialog && DialogNative.GetForegroundWindow() == dialog) return true;
            TakeForeground(dialog);
            return false;
        });
        // WM_NEXTDLGCTL targets the actual Edit HWND and performs normal dialog
        // focus management. Post it, then observe the owning thread's focus.
        if (!DialogNative.PostMessage(dialog, 0x0028, edit, new IntPtr(1)))
            throw new InvalidOperationException("The native file dialog could not focus its file-name field.");
        await Until(() =>
        {
            var state = new DialogNative.GuiThreadInfo { Size = Marshal.SizeOf<DialogNative.GuiThreadInfo>() };
            if (DialogNative.GetGUIThreadInfo(dialogThread, ref state) && state.Focus == edit
                && state.Active == dialog && DialogNative.GetForegroundWindow() == dialog) return true;
            // Une autre fenetre a pu reprendre le premier plan entre les deux etapes : SendInput frapperait
            // alors cette fenetre au lieu du champ de nom de fichier de la dialogue.
            TakeForeground(dialog);
            return false;
        });
        RequireNativeFileInputTarget(process, dialog, edit);
        var originalLength = NativeDialogMessage(edit, 0x000E, IntPtr.Zero, IntPtr.Zero).ToUInt64(); // WM_GETTEXTLENGTH
        if (originalLength > 32767) throw new InvalidOperationException("The initial native file-name value is unexpectedly long.");
        NativeDialogMessage(edit, 0x00B1, IntPtr.Zero, new IntPtr(-1)); // EM_SETSEL: select all, without modifier keys.
        var selection = NativeDialogMessage(edit, 0x00B0, IntPtr.Zero, IntPtr.Zero).ToUInt64(); // EM_GETSEL, no cross-process pointers.
        if ((selection & 0xffff) != 0 || ((selection >> 16) & 0xffff) != originalLength)
            throw new InvalidOperationException("The native file-name field did not select its complete initial value.");

        // WM_SETTEXT changes the Edit's display but the modern Save dialog can
        // retain its suggested filename. Use the genuine Unicode keyboard stream
        // so the shell receives the same input notifications as a user's typing.
        var inputSize = Marshal.SizeOf<DialogNative.Input>();
        var unionOffset = Marshal.OffsetOf<DialogNative.Input>(nameof(DialogNative.Input.Data)).ToInt32();
        if (IntPtr.Size != 8 || inputSize != 40 || unionOffset != 8)
            throw new InvalidOperationException($"Invalid x64 INPUT layout: size={inputSize}, union offset={unionOffset}.");
        var inputs = new DialogNative.Input[checked(path.Length * 2)];
        for (var i = 0; i < path.Length; i++)
        {
            inputs[2 * i] = DialogNative.UnicodeInput(path[i], false);
            inputs[2 * i + 1] = DialogNative.UnicodeInput(path[i], true);
        }
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) // Shift, Control, Alt, both Windows keys.
            if ((DialogNative.GetAsyncKeyState(key) & 0x8000) != 0)
                throw new InvalidOperationException($"Native file input refused while modifier key 0x{key:X} is held.");
        _deadline.Token.ThrowIfCancellationRequested();
        RequireNativeFileInputTarget(process, dialog, edit); // No await between this guard and the single input batch.
        var inserted = DialogNative.SendInput((uint)inputs.Length, inputs, inputSize);
        var inputError = Marshal.GetLastWin32Error();
        if (inserted != (uint)inputs.Length)
            throw new InvalidOperationException($"Native Unicode input inserted {inserted}/{inputs.Length} events (Win32 {inputError}).");
        Log($"Native filename input: pid={process.Id}; focus={edit.ToInt64():X}; INPUT={inputSize}; unionOffset={unionOffset}; events={inserted}.");

        // SendInput queues the events; wait for the real Edit to process them.
        // One extra character in the read buffer prevents accepting a matching
        // prefix when an unwanted suffix remains after the requested filename.
        var observed = "";
        try
        {
            await Until(() =>
            {
                try { RequireNativeFileInputTarget(process, dialog, edit); }
                catch (InvalidOperationException)
                {
                    // Cette boucle ne fait que relire le champ : une perte de premier plan pendant la relecture
                    // se repare. Elle ne doit pas faire echouer une frappe deja acceptee par la dialogue.
                    TakeForeground(dialog); return false;
                }
                observed = NativeFileName(edit, path.Length);
                return string.Equals(observed, path, StringComparison.Ordinal);
            });
        }
        catch (Exception error) when (error is OperationCanceledException)
        { throw new InvalidOperationException("The native file-name input did not settle on the exact path. Last value: " + JsonSerializer.Serialize(observed), error); }
        Log("Native filename readback: " + JsonSerializer.Serialize(observed));
        RequireNativeFileInputTarget(process, dialog, edit);
        DialogNative.GetWindowThreadProcessId(confirm, out var buttonOwner);
        if (buttonOwner != process.Id || !DialogNative.IsChild(dialog, confirm)
            || !DialogNative.IsWindowVisible(confirm) || !DialogNative.IsWindowEnabled(confirm)
            || !string.Equals(NativeFileName(edit, path.Length), path, StringComparison.Ordinal))
            throw new InvalidOperationException("The native file dialog changed before confirming the verified filename.");
        // Let the actual button route BN_CLICKED to its own parent. The shell's
        // button can be nested below the outer #32770 dialog; sending WM_COMMAND
        // straight to that outer dialog is not equivalent to clicking Save.
        if (!DialogNative.PostMessage(confirm, 0x00F5, IntPtr.Zero, IntPtr.Zero)) // BM_CLICK
            throw new InvalidOperationException("The native file dialog could not click its confirmation button.");
        await Until(() => !DialogNative.IsWindowVisible(dialog));
    }

    private void LogNativeFileDialogEdits(Process process, IntPtr dialog, string stage)
    {
        // Enumerate every visible Edit owned by this fixture PID, including any
        // alternative native dialog. Keep text bounded and diagnostics read-only.
        var edits = new HashSet<IntPtr>();
        void AddEdit(IntPtr window)
        {
            DialogNative.GetWindowThreadProcessId(window, out var owner);
            if (owner == process.Id && DialogNative.IsWindowVisible(window) && DialogNative.Class(window) == "Edit") edits.Add(window);
        }
        DialogNative.EnumWindows((window, _) =>
        {
            DialogNative.GetWindowThreadProcessId(window, out var owner);
            if (owner == process.Id)
            {
                AddEdit(window);
                DialogNative.EnumChildWindows(window, (child, _) => { AddEdit(child); return true; }, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        Log($"Native visible Edit inventory: stage={stage}; pid={process.Id}; dialog={dialog.ToInt64():X}; count={edits.Count}.");
        foreach (var edit in edits.OrderBy(window => window.ToInt64()))
        {
            var thread = DialogNative.GetWindowThreadProcessId(edit, out var owner);
            var state = new DialogNative.GuiThreadInfo { Size = Marshal.SizeOf<DialogNative.GuiThreadInfo>() };
            var hasState = DialogNative.GetGUIThreadInfo(thread, ref state);
            var parents = new List<object>(); var seen = new HashSet<IntPtr>();
            for (var parent = DialogNative.GetParent(edit); parent != IntPtr.Zero && parents.Count < 16 && seen.Add(parent); parent = DialogNative.GetParent(parent))
            {
                DialogNative.GetWindowThreadProcessId(parent, out var parentOwner);
                parents.Add(new { hwnd = parent.ToInt64().ToString("X"), id = DialogNative.GetDlgCtrlID(parent), className = DialogNative.Class(parent), pid = parentOwner });
            }
            var text = ""; string? readError = null; ulong? textLength = null;
            try { textLength = NativeDialogMessage(edit, 0x000E, IntPtr.Zero, IntPtr.Zero).ToUInt64(); text = NativeFileName(edit, 255); }
            catch (InvalidOperationException error) { readError = error.Message; }
            Log("Native visible Edit: " + JsonSerializer.Serialize(new
            {
                stage, pid = owner, hwnd = edit.ToInt64().ToString("X"), id = DialogNative.GetDlgCtrlID(edit), className = DialogNative.Class(edit),
                inSelectedDialog = DialogNative.IsChild(dialog, edit), enabled = DialogNative.IsWindowEnabled(edit),
                active = hasState ? state.Active.ToInt64().ToString("X") : "unknown", focus = hasState ? state.Focus.ToInt64().ToString("X") : "unknown",
                parents, text, textLength, textTruncated = textLength > (ulong)text.Length, readError
            }));
        }
    }

    private static void RequireNativeFileInputTarget(Process process, IntPtr dialog, IntPtr edit)
    {
        var thread = DialogNative.GetWindowThreadProcessId(dialog, out var dialogOwner);
        var editThread = DialogNative.GetWindowThreadProcessId(edit, out var editOwner);
        var foreground = DialogNative.GetForegroundWindow();
        DialogNative.GetWindowThreadProcessId(foreground, out var foregroundOwner);
        var state = new DialogNative.GuiThreadInfo { Size = Marshal.SizeOf<DialogNative.GuiThreadInfo>() };
        var hasState = thread != 0 && DialogNative.GetGUIThreadInfo(thread, ref state);
        if (process.HasExited || dialogOwner != process.Id || editOwner != process.Id || foregroundOwner != process.Id
            || editThread != thread || foreground != dialog || !hasState || state.Active != dialog || state.Focus != edit
            || !DialogNative.IsChild(dialog, edit) || DialogNative.Class(edit) != "Edit"
            || !DialogNative.IsWindowVisible(dialog) || !DialogNative.IsWindowVisible(edit) || !DialogNative.IsWindowEnabled(edit))
            throw new InvalidOperationException($"Native input target lost: expected pid={process.Id}, dialog={dialog.ToInt64():X}, edit={edit.ToInt64():X}; "
                + $"owners={dialogOwner}/{editOwner}/{foregroundOwner}, foreground={foreground.ToInt64():X}, active={state.Active.ToInt64():X}, focus={state.Focus.ToInt64():X}.");
    }

    private static UIntPtr NativeDialogMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (DialogNative.Message(window, message, wParam, lParam, 0x0022, 2000, out var value) == IntPtr.Zero)
            throw new InvalidOperationException($"The native file dialog did not process message 0x{message:X}.");
        return value;
    }

    private static string NativeFileName(IntPtr edit, int expectedLength)
    {
        // GetWindowText only reads captions across processes. WM_GETTEXT reads
        // the actual Edit text through bounded system-provided marshalling.
        var received = new StringBuilder(checked(expectedLength + 2));
        if (DialogNative.ReadText(edit, 0x000D, (UIntPtr)received.Capacity, received, 0x0022, 2000, out var copied) == IntPtr.Zero
            || copied.ToUInt64() != (ulong)received.Length)
            throw new InvalidOperationException("The native file-name field could not be read reliably.");
        return received.ToString();
    }

    private static class DialogNative
    {
        internal delegate bool Enumerate(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
        {
            public int Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int Left, Top, Right, Bottom;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Input
        {
            public uint Type;
            public InputUnion Data;
        }
        // Include the full native union: MOUSEINPUT, not KEYBDINPUT, determines
        // its x64 size (32 bytes) and therefore INPUT's required size (40 bytes).
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public HardwareInput Hardware;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
        {
            public ushort VirtualKey, Scan;
            public uint Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct HardwareInput
        {
            public uint Message;
            public ushort ParameterLow, ParameterHigh;
        }
        internal static Input UnicodeInput(char character, bool released) => new()
        {
            Type = 1, // INPUT_KEYBOARD; wVk remains zero for KEYEVENTF_UNICODE.
            Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = released ? 0x0006u : 0x0004u } }
        };
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(Enumerate callback, IntPtr parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(IntPtr parent, Enumerate callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(IntPtr parent, IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        internal static extern IntPtr Message(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr ReadText(IntPtr window, uint message, UIntPtr size, [Out] StringBuilder value, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int size);
        internal static string Class(IntPtr window) { var name = new StringBuilder(256); GetClassName(window, name, name.Capacity); return name.ToString(); }
    }
}
