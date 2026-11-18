using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GlpiPilot.Core;
using Microsoft.Playwright;

internal static class NativeExportTimingBrowserChecks
{
    private const string Origin = "http://glpi.test";
    private const string Target = Origin + "/glpi/front/ticket.php?criteria%5B0%5D%5Bfield%5D=12&criteria%5B0%5D%5Bvalue%5D=1";
    private const string ForeignDownload = Origin + "/glpi/front/unrelated-export.csv";
    private const string Csv = "\uFEFFID;Nom\r\n7;Exemple synthétique\r\n";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(IBrowser browser)
    {
        yield return ("Attente export GLPI : sélection native, contrôle retiré et fichier immédiat importé", () => Check(browser, "selection"));
        yield return ("Attente export GLPI : sélection conservée, soumission différée sans second clic", () => Check(browser, "selection-kept"));
        yield return ("Attente export GLPI : sélection native, réponse après 12 s et un seul export", () => Check(browser, "selection-slow"));
        yield return ("Attente export GLPI : clic natif, réponse après 9 s et un seul export", () => Check(browser, "click-slow"));
        yield return ("Attente export GLPI : fenêtre native target blank, réponse lente et import unique", () => Check(browser, "popup-slow"));
        yield return ("Attente export GLPI : annulation pendant la réponse sans fichier ni faux succès", () => Check(browser, "cancel"));
        yield return ("Attente export GLPI : choix refusé sans requête, aucun téléchargement déclaré", () => Check(browser, "refused"));
        yield return ("Attente export GLPI : annulation puis relance identique, ancien fichier isolé", () => CheckCancelThenRetry(browser));
        yield return ("Attente export GLPI : refus HTTP403 natif, erreur explicite sans nouvel envoi", () => Check(browser, "http403"));
        yield return ("Attente export GLPI : téléchargement étranger ignoré avant le bon fichier natif", () => Check(browser, "foreign"));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static AppSettings Settings(string folder) => new()
    {
        PortalUrl = Target, SelectorOrigin = Origin, AutoMode = "native", ColumnPreparationMode = "current",
        ExportFolder = folder, AutoSaveXlsx = false, ResumePagination = false, Cities = new() { Enabled = false },
        TimeoutSeconds = 10, ListTimeoutSeconds = 5, DownloadTimeoutSeconds = 20,
        ExpectedTotal = 1, MaxRows = 20, MaxPages = 5
    };

    private static string Fixture(string scenario)
    {
        var change = scenario == "refused" ? "this.value = '4';"
            : scenario == "click-slow" ? ""
            : scenario == "selection-kept" ? "const exportForm=this.form;setTimeout(()=>exportForm.submit(),100);"
            : "this.form.submit(); this.remove();";
        if (scenario == "foreign") change = "const unrelated=document.createElement('iframe');unrelated.hidden=true;document.body.append(unrelated);unrelated.src='/glpi/front/unrelated-export.csv';" + change;
        var html = """
            <!doctype html><html lang="fr"><meta charset="utf-8"><title>GLPI — Tickets synthétiques</title>
            <style>body{font:14px sans-serif}td,th{padding:6px}button,select{min-height:28px}</style><body>
            <a href="/glpi/logout.php">Déconnexion</a>
            <section id="ticket-results">
              <form id="massformTicket1" data-search-itemtype="Ticket" data-count="1" data-start="0" data-limit="20">
                <table class="tab_cadre_fixehov" id="ticket-table"><thead><tr><th>ID</th><th>Nom</th></tr></thead>
                  <tbody><tr><td>7</td><td>Exemple synthétique</td></tr></tbody></table>
              </form>
              <div class="pager"><table class="table align-middle"><tr><td>
                <form id="native-export" method="GET" action="/glpi/front/report.dynamic.php">
                  <input type="hidden" name="item_type" value="Ticket">
                  <input type="hidden" name="start" value="0">
                  <input type="hidden" name="criteria[0][field]" value="12">
                  <input type="hidden" name="criteria[0][value]" value="1">
                  <select name="display_type" id="native-format">
                    <option value="4">Page courante en CSV</option>
                    <option value="-4">Toutes les pages en CSV</option>
                  </select>
                  <button type="submit" name="export" id="native-submit" title="Exporter">Exporter</button>
                </form>
              </td><td>De 1 à 1 sur 1</td></tr></table></div>
            </section>
            <script>
            window.selectionChanges = 0; window.submitClicks = 0;
            document.querySelector('#native-submit').addEventListener('click', () => window.submitClicks++);
            document.querySelector('#native-format').addEventListener('change', function () {
              window.selectionChanges++;
            """ + change + """
            });
            </script></body></html>
            """;
        return scenario == "popup-slow"
            ? html.Replace("id=\"native-export\" method=\"GET\"", "id=\"native-export\" method=\"GET\" target=\"_blank\"", StringComparison.Ordinal)
            : html;
    }

    private static async Task Check(IBrowser browser, string scenario)
    {
        var traceClock = Stopwatch.StartNew();
        void Trace(string step)
        {
            if (scenario == "http403")
                Console.Error.WriteLine($"TRACE native HTTP403 +{traceClock.Elapsed.TotalMilliseconds:F0} ms: {step}");
        }
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-export-timing-" + Guid.NewGuid().ToString("N"));
        Trace("NewContext begin");
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        Trace("NewContext complete");
        var service = new BrowserService();
        var requests = new ConcurrentQueue<(string Method, Uri Url)>();
        var responseDurations = new ConcurrentQueue<TimeSpan>();
        var routeTasks = new ConcurrentBag<Task>();
        var downloadChecks = new ConcurrentBag<Task>();
        var logs = new ConcurrentQueue<string>();
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreignDownloadObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopResponses = new CancellationTokenSource();
        using var cancellation = new CancellationTokenSource();
        var responseDelay = scenario == "selection-slow" ? 12000 : scenario is "click-slow" or "popup-slow" ? 9000 : 0;
        var saved = 0; var ready = 0; var foreignRequests = 0; var foreignDownloads = 0;
        var foreignVerified = false; var foreignFailure = "";
        Task<AutomaticRunResult>? pendingRun = null;
        service.Log += line => { logs.Enqueue(line); Trace("service: " + line); };
        service.AttachForTesting(context, Target); // Includes the production 10 s default locator timeout.
        try
        {
            async Task Handle(IRoute route)
            {
                try
                {
                    var uri = new Uri(route.Request.Url);
                    if (uri.GetLeftPart(UriPartial.Authority) != Origin) { await route.AbortAsync(); return; }
                    if (uri.AbsolutePath == "/glpi/front/report.dynamic.php")
                    {
                        requests.Enqueue((route.Request.Method, uri));
                        Trace("native route received");
                        firstRequest.TrySetResult();
                        var responseClock = Stopwatch.StartNew();
                        if (scenario == "cancel") await Task.Delay(Timeout.InfiniteTimeSpan, stopResponses.Token);
                        // Windows timer resolution can finish one Task.Delay
                        // slightly before the requested Stopwatch duration.
                        while (responseClock.Elapsed < TimeSpan.FromMilliseconds(responseDelay))
                            await Task.Delay(Math.Max(1, responseDelay - (int)responseClock.Elapsed.TotalMilliseconds), stopResponses.Token);
                        if (scenario == "foreign")
                        {
                            try { await foreignDownloadObserved.Task.WaitAsync(stopResponses.Token); }
                            catch when (foreignDownloadObserved.Task.IsFaulted) { await route.AbortAsync(); return; }
                        }
                        responseDurations.Enqueue(responseClock.Elapsed);
                        if (scenario == "http403")
                        {
                            Trace("Fulfill HTTP403 begin");
                            await route.FulfillAsync(new() { Status = 403, ContentType = "text/html; charset=utf-8", Body = "<!doctype html><title>Export refusé</title><p>Refus synthétique de la commande native.</p>" });
                            Trace("Fulfill HTTP403 complete");
                            return;
                        }
                        await route.FulfillAsync(new()
                        {
                            ContentType = "text/csv; charset=utf-8",
                            Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"glpi-synthetic.csv\"" },
                            Body = Csv
                        });
                    }
                    else if (route.Request.Url == ForeignDownload)
                    {
                        Interlocked.Increment(ref foreignRequests);
                        await route.FulfillAsync(new()
                        {
                            ContentType = "text/csv; charset=utf-8",
                            Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"unrelated.csv\"" },
                            Body = Csv.Replace("7;", "99;", StringComparison.Ordinal)
                        });
                    }
                    else if (uri.AbsolutePath == "/glpi/front/ticket.php")
                        await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Fixture(scenario) });
                    else await route.AbortAsync();
                }
                catch (OperationCanceledException) when (stopResponses.IsCancellationRequested)
                { try { await route.AbortAsync(); } catch (PlaywrightException) { } }
                catch (PlaywrightException) when (stopResponses.IsCancellationRequested) { }
            }
            await context.RouteAsync("**/*", route =>
            {
                var task = Handle(route); routeTasks.Add(task); return task;
            });
            Trace("NewPage begin");
            var page = await context.NewPageAsync();
            Trace("NewPage complete");
            async Task VerifyForeignDownloadAsync(IDownload download)
            {
                try
                {
                    foreignFailure = await download.FailureAsync() ?? "";
                    Require(foreignFailure.Length == 0, "The unrelated fixture attachment failed: " + foreignFailure);
                    await using var stream = await download.CreateReadStreamAsync();
                    Require(stream is not null, "No stream was available for the unrelated fixture attachment.");
                    using var bytes = new MemoryStream();
                    await stream!.CopyToAsync(bytes, stopResponses.Token);
                    foreignVerified = Encoding.UTF8.GetString(bytes.ToArray()) == Csv.Replace("7;", "99;", StringComparison.Ordinal);
                    Require(foreignVerified, "The unrelated attachment did not contain the exact synthetic ID99 CSV.");
                    foreignDownloadObserved.TrySetResult();
                }
                catch (Exception error)
                {
                    foreignFailure = error.GetType().Name + ": " + error.Message;
                    foreignDownloadObserved.TrySetException(error);
                }
            }
            page.Download += (_, download) =>
            {
                if (download.Url == ForeignDownload)
                {
                    Interlocked.Increment(ref foreignDownloads);
                    downloadChecks.Add(VerifyForeignDownloadAsync(download));
                }
            };
            Trace("initial navigation begin");
            await page.GotoAsync(Target);
            Trace("initial navigation complete");
            var settings = Settings(folder);
            Trace("table selection begin");
            var original = await service.ChooseAutoTableAsync(page, settings, CancellationToken.None);
            Trace("table selection complete; export inspection begin");
            var inspection = await service.InspectNativeExportAsync(original, CancellationToken.None);
            Trace("export inspection complete");
            Require(inspection.Supported && inspection.Plans.Count == 1
                && inspection.Plans[0] is { Kind: "form", Format: "csv", AllPages: true, FormatValue: "-4" },
                "The fixture must expose one reviewed native GET form with the All pages CSV option before testing timing.");
            if (scenario == "popup-slow") Require(await page.Locator("#native-export").GetAttributeAsync("target") == "_blank",
                "The delayed popup fixture must use the observed native form target rather than a synthetic download event.");
            var automatic = new AutomaticExtractionService(service);
            automatic.NativeFileSaved += _ => { Interlocked.Increment(ref saved); Trace("NativeFileSaved"); };
            automatic.PhaseChanged += phase => { if (phase == AutomaticRunPhase.Ready) Interlocked.Increment(ref ready); Trace("phase: " + phase); };
            Trace("RunAsync invoke");
            var runTask = pendingRun = automatic.RunAsync(settings, null, cancellation.Token);
            Trace("RunAsync task returned: " + runTask.Status);
            if (scenario == "cancel")
            {
                var first = await Task.WhenAny(firstRequest.Task, runTask).WaitAsync(TimeSpan.FromSeconds(10));
                if (first == runTask) await runTask;
                Require(firstRequest.Task.IsCompletedSuccessfully, "Cancellation must occur after the real native GET, while its response is still pending.");
                await Task.Delay(150);
                var cancelClock = Stopwatch.StartNew();
                cancellation.Cancel();
                Exception? failure = null;
                try { await runTask.WaitAsync(TimeSpan.FromSeconds(7)); }
                catch (Exception error) { failure = error; }
                Require(failure is OperationCanceledException && cancelClock.Elapsed < TimeSpan.FromSeconds(7),
                    "Cancellation during the server response must propagate promptly instead of becoming a timeout or successful result.");
                Require(requests.Count == 1 && saved == 0 && ready == 0 && !Directory.EnumerateFiles(folder).Any(),
                    "Cancellation must not retry the export, publish Ready, or save an incomplete file.");
            }
            else if (scenario == "refused")
            {
                Exception? failure = null;
                try { await runTask; }
                catch (Exception error) { failure = error; }
                Require(failure is InvalidOperationException && requests.IsEmpty && saved == 0 && ready == 0,
                    "A choice rejected by the native change handler must fail without an export request or a successful download receipt.");
                Require(await page.EvaluateAsync<int>("() => window.selectionChanges") == 1
                    && await page.Locator("#native-format").InputValueAsync() == "4"
                    && await page.EvaluateAsync<int>("() => window.submitClicks") == 0
                    && !Directory.EnumerateFiles(folder).Any(),
                    "The refused selection must be observed before the separate submit can be clicked.");
            }
            else if (scenario == "http403")
            {
                Exception? failure = null;
                Trace("RunAsync await begin");
                try { await runTask; }
                catch (Exception error) { failure = error; Trace("RunAsync caught " + error.GetType().Name + ": " + error.Message); }
                Trace($"RunAsync await complete; assert begin: requests={requests.Count}, saved={saved}, ready={ready}, failure={failure?.GetType().Name ?? "none"}");
                Require(failure is InvalidOperationException && failure.Message.Contains("403", StringComparison.Ordinal)
                    && requests.Count == 1 && saved == 0 && ready == 0 && !Directory.EnumerateFiles(folder).Any(),
                    "A native HTTP403 must be reported explicitly without retry, saved file, successful receipt or silent fallback.");
                Trace("HTTP403 assertion complete");
            }
            else
            {
                if (scenario == "foreign")
                {
                    await Task.WhenAny(runTask, foreignDownloadObserved.Task);
                    if (foreignDownloadObserved.Task.IsFaulted) await foreignDownloadObserved.Task;
                }
                var run = await runTask;
                Require(run.Data.Rows.Count == 1 && run.Data.Rows[0].SequenceEqual(new[] { "7", "Exemple synthétique" })
                    && run.Data.Complete && run.Data.Provenance.Method == "native-csv" && run.Data.Provenance.ScopeVerified,
                    "The actual automatic pipeline must import and verify the one-row CSV from the same native search: " + string.Join(" | ", run.Data.Warnings));
                Require(run.NativePath is not null && File.Exists(run.NativePath) && File.Exists(run.PrimaryPath)
                    && Encoding.UTF8.GetString(await File.ReadAllBytesAsync(run.NativePath)) == Csv && saved == 1 && ready == 1,
                    "The completed native file must be preserved and imported before a single Ready result is published.");
                Require(requests.Count == 1 && requests.Single().Method == "GET", "A slow native export must be sent exactly once, never retried after a locator deadline.");
                var query = requests.Single().Url.Query.TrimStart('?').Split('&')
                    .Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
                    .Select(pair => (Name: Uri.UnescapeDataString(pair[0]), Value: Uri.UnescapeDataString(pair[1]))).ToArray();
                Require(query.Count(pair => pair.Name == "display_type" && pair.Value == "-4") == 1
                    && query.Any(pair => pair.Name == "item_type" && pair.Value == "Ticket")
                    && query.Any(pair => pair.Name == "criteria[0][field]" && pair.Value == "12")
                    && query.Any(pair => pair.Name == "criteria[0][value]" && pair.Value == "1"),
                    "The browser must submit the observed All pages CSV form with its original Ticket filters.");
                Require(await page.EvaluateAsync<int>("() => window.selectionChanges") == 1
                    && await page.EvaluateAsync<int>("() => window.submitClicks") == (scenario == "click-slow" ? 1 : 0),
                    "An export already triggered by change must not be followed by a second submit click.");
                if (scenario is not ("click-slow" or "selection-kept")) Require(await page.Locator("#native-format").CountAsync() == 0,
                    "The successful download must survive the actual native select being removed after submission.");
                if (scenario == "selection-kept") Require(await page.Locator("#native-format").InputValueAsync() == "-4",
                    "A retained native selection must not trigger a second submit before its deferred export request is observed.");
                if (responseDelay > 0) Require(responseDurations.Single() >= TimeSpan.FromMilliseconds(responseDelay),
                    $"The delayed fixture must cross the former locator/action deadline before sending the download headers: expected {responseDelay} ms, observed {responseDurations.Single().TotalMilliseconds:F2} ms.");
                if (scenario == "popup-slow") Require(!page.IsClosed && page.Url == Target,
                    "The native popup export must leave the source search open and unchanged.");
                if (scenario == "foreign") Require(foreignRequests == 1 && foreignDownloads == 1 && foreignVerified
                    && foreignDownloadObserved.Task.IsCompletedSuccessfully && Directory.EnumerateFiles(folder).Count() == 1,
                    $"The unrelated attachment from the same page must be received first and ignored; only the matching native CSV may be retained. Foreign requests={foreignRequests}, downloads={foreignDownloads}, verified={foreignVerified}, failure={foreignFailure}.");
            }
        }
        catch
        {
            Console.Error.WriteLine($"TRACE synthetic native export timing {scenario}: requests={requests.Count}, saved={saved}, ready={ready}");
            Console.Error.WriteLine($"TRACE synthetic native export timing: response milliseconds=[{string.Join(",", responseDurations.Select(value => value.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}]; foreign requests={foreignRequests}, downloads={foreignDownloads}, verified={foreignVerified}, failure={foreignFailure}");
            Console.Error.WriteLine("TRACE synthetic native export timing files: " + (Directory.Exists(folder) ? string.Join(",", Directory.EnumerateFiles(folder).Select(Path.GetFileName)) : "no export directory"));
            foreach (var line in logs.TakeLast(30)) Console.Error.WriteLine("TRACE synthetic native export timing: " + line);
            throw;
        }
        finally
        {
            Trace("finally entered; stopResponses.Cancel begin");
            stopResponses.Cancel();
            Trace("stopResponses.Cancel complete; cancellation.Cancel begin");
            cancellation.Cancel();
            Trace("cancellation.Cancel complete; service.DisposeAsync begin");
            await service.DisposeAsync();
            Trace($"service.DisposeAsync complete; route/download drain begin: routes={routeTasks.Count}, completed={routeTasks.Count(task => task.IsCompleted)}, checks={downloadChecks.Count}");
            await Task.WhenAll(routeTasks.Concat(downloadChecks)).WaitAsync(TimeSpan.FromSeconds(5));
            Trace("route/download drain complete");
            if (pendingRun is not null)
            {
                // A failed foreign fixture proof can end the check before the
                // production run. Closing its context must also drain that run.
                Trace("pendingRun drain begin: " + pendingRun.Status);
                try { await pendingRun; } catch (Exception) { }
                Trace("pendingRun drain complete");
            }
            Trace("directory cleanup begin");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Trace("directory cleanup complete; check complete");
        }
    }

    private static async Task CheckCancelThenRetry(IBrowser browser)
    {
        const string sentinelUrl = Origin + "/glpi/front/central.php?synthetic=sentinel";
        var folder = Path.Combine(Path.GetTempPath(), "GLPI-export-retry-" + Guid.NewGuid().ToString("N"));
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        var service = new BrowserService();
        var requests = new ConcurrentQueue<(string Method, string Url)>();
        var routeTasks = new ConcurrentBag<Task>();
        var logs = new ConcurrentQueue<string>();
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResponseFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopResponses = new CancellationTokenSource();
        using var cancellation = new CancellationTokenSource();
        var requestCount = 0; var saved = 0; var ready = 0;
        IPage? source = null;
        service.Log += logs.Enqueue;
        service.AttachForTesting(context, Target);
        try
        {
            async Task Handle(IRoute route)
            {
                var attempt = 0;
                try
                {
                    var uri = new Uri(route.Request.Url);
                    if (uri.GetLeftPart(UriPartial.Authority) != Origin) { await route.AbortAsync(); return; }
                    if (uri.AbsolutePath == "/glpi/front/report.dynamic.php")
                    {
                        attempt = Interlocked.Increment(ref requestCount);
                        requests.Enqueue((route.Request.Method, route.Request.Url));
                        if (attempt == 1)
                        {
                            firstRequest.TrySetResult();
                            await releaseFirst.Task.WaitAsync(stopResponses.Token);
                        }
                        else if (attempt == 2)
                        {
                            secondRequest.TrySetResult();
                            await releaseSecond.Task.WaitAsync(stopResponses.Token);
                        }
                        await route.FulfillAsync(new()
                        {
                            ContentType = "text/csv; charset=utf-8",
                            Headers = new Dictionary<string, string> { ["Content-Disposition"] = $"attachment; filename=\"glpi-attempt-{attempt}.csv\"" },
                            Body = attempt == 1 ? Csv.Replace("7;", "99;", StringComparison.Ordinal) : Csv
                        });
                    }
                    else if (route.Request.Url == sentinelUrl)
                        await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = "<!doctype html><title>Onglet indépendant</title><p id='sentinel'>Conserver cet onglet</p>" });
                    else if (uri.AbsolutePath == "/glpi/front/ticket.php")
                        await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = Fixture("selection") });
                    else await route.AbortAsync();
                }
                catch (OperationCanceledException) when (stopResponses.IsCancellationRequested)
                { try { await route.AbortAsync(); } catch (PlaywrightException) { } }
                catch (PlaywrightException) when (stopResponses.IsCancellationRequested || attempt == 1 && source?.IsClosed == true)
                { /* Closing the first source may reject its deliberately late response. */ }
                finally { if (attempt == 1) firstResponseFinished.TrySetResult(); }
            }
            await context.RouteAsync("**/*", route =>
            {
                var task = Handle(route); routeTasks.Add(task); return task;
            });
            source = await context.NewPageAsync();
            await source.GotoAsync(Target);
            var sentinel = await context.NewPageAsync();
            await sentinel.GotoAsync(sentinelUrl);
            var settings = Settings(folder);
            var automatic = new AutomaticExtractionService(service);
            automatic.NativeFileSaved += _ => Interlocked.Increment(ref saved);
            automatic.PhaseChanged += phase => { if (phase == AutomaticRunPhase.Ready) Interlocked.Increment(ref ready); };
            var firstRun = automatic.RunAsync(settings, null, cancellation.Token);
            var first = await Task.WhenAny(firstRequest.Task, firstRun).WaitAsync(TimeSpan.FromSeconds(10));
            if (first == firstRun) await firstRun;
            Require(firstRequest.Task.IsCompletedSuccessfully, "The first export must really be pending before cancellation.");
            cancellation.Cancel();
            Exception? failure = null;
            try { await firstRun.WaitAsync(TimeSpan.FromSeconds(7)); }
            catch (Exception error) { failure = error; }
            Require(failure is OperationCanceledException && source.IsClosed,
                "Cancellation after the GET must close the source of its not-yet-detected attachment before allowing another attempt.");
            Require(!sentinel.IsClosed && sentinel.Url == sentinelUrl && saved == 0 && ready == 0 && !Directory.EnumerateFiles(folder).Any(),
                "Cancellation must preserve the independent non-detail tab and publish no file or Ready result.");

            var secondRun = automatic.RunAsync(settings, null, CancellationToken.None);
            var second = await Task.WhenAny(secondRequest.Task, secondRun).WaitAsync(TimeSpan.FromSeconds(10));
            if (second == secondRun) await secondRun;
            Require(secondRequest.Task.IsCompletedSuccessfully, "The second run must send its own native export before either held response is released.");
            var retryPage = service.ActivePage;
            Require(retryPage is not null && !ReferenceEquals(retryPage, source) && !ReferenceEquals(retryPage, sentinel)
                && !retryPage.IsClosed && retryPage.Url == Target && !sentinel.IsClosed && sentinel.Url == sentinelUrl,
                "The retry must create a fresh source page without navigating the unrelated remaining tab.");
            releaseFirst.TrySetResult();
            await firstResponseFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseSecond.TrySetResult();
            var run = await secondRun;
            Require(run.Data.Rows.Count == 1 && run.Data.Rows[0][0] == "7" && run.Data.Complete
                && run.NativePath is not null && Encoding.UTF8.GetString(await File.ReadAllBytesAsync(run.NativePath)) == Csv,
                "The retried collection must import ID7 from its own response, never the cancelled attempt's late ID99 attachment.");
            Require(saved == 1 && ready == 1 && Directory.EnumerateFiles(folder).Count() == 1
                && requests.Count == 2 && requests.All(request => request.Method == "GET")
                && requests.Select(request => request.Url).Distinct(StringComparer.Ordinal).Count() == 1,
                "Two identical native GETs across cancellation and retry must produce exactly one retained file and one successful result.");
            Require(!sentinel.IsClosed && sentinel.Url == sentinelUrl
                && await sentinel.Locator("#sentinel").InnerTextAsync() == "Conserver cet onglet",
                "The independent non-detail tab must remain intact through cancellation, retry and the late first response.");
        }
        catch
        {
            Console.Error.WriteLine($"TRACE synthetic native export retry: requests={requests.Count}, saved={saved}, ready={ready}");
            foreach (var line in logs.TakeLast(40)) Console.Error.WriteLine("TRACE synthetic native export retry: " + line);
            throw;
        }
        finally
        {
            stopResponses.Cancel();
            await service.DisposeAsync();
            await Task.WhenAll(routeTasks).WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
