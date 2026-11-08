using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal sealed partial class WindowsWebViewFixture
{
    private async Task CheckFieldPreparationNativeAsync(IPage page, Process process, string edition)
    {
        await CheckCollectionNativeAsync(page, edition);
        var seeded = await page.EvaluateAsync<JsonElement>("""
            async edition=>{
              const {api}=await import('/js/bridge.js');
              const title='Qualification terrain native '+edition;
              const saved=await api.call('calendar.save',{intervention:{id:crypto.randomUUID().replaceAll('-',''),title,
                kind:'intervention',state:'planned',startLocal:'2026-11-02T09:00',endLocal:'2026-11-02T10:00',timeZoneId:'Europe/Paris',participantKeys:[],tickets:[]}});
              if(!saved.saved)throw new Error('The native field appointment was not saved.');
              const entry=saved.item.intervention;
              const view=await api.call('calendar.get',{from:'2026-11-02',to:'2026-11-03',timeZoneId:'Europe/Paris'});
              const occurrence=view.occurrences.find(row=>row.interventionId===entry.id);
              const session=(await api.call('app.bootstrap')).session;
              const opened=await api.call('field.open',{occurrenceKey:occurrence.key,revision:session.revision});
              await api.call('field.save',{token:opened.token,plan:opened.document.plan});
              const reference=opened.catalogue.references.find(row=>!row.archived&&row.unit==='piece'&&opened.catalogue.categories.some(c=>c.id===row.categoryId&&!c.archived));
              if(!reference)throw new Error('No active generic piece reference exists.');
              return {id:opened.document.plan.id,entry,reference,title};
            }
            """, edition);
        var planId = seeded.GetProperty("id").GetString()!;
        // JsonElement is a read result here, not an argument for Playwright's
        // CLR-object serializer. The cancellation needs only the scalar ID.
        var interventionId = seeded.GetProperty("entry").GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(interventionId))
            throw new InvalidOperationException("The native field appointment identifier is missing.");
        var referenceId = seeded.GetProperty("reference").GetProperty("id").GetString()!;
        var referenceLabel = seeded.GetProperty("reference").GetProperty("label").GetString()!;
        await NavigateAsync(page, "interventions");
        await page.Locator("[data-field-nav='interventions']").ClickAsync();
        var row = page.Locator("[data-field-plan='" + planId + "']"); await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "Ouvrir", Exact = true }).ClickAsync();
        await page.GetByLabel("Référence à ajouter", new() { Exact = true }).SelectOptionAsync(referenceId);
        await page.GetByLabel("Quantité à ajouter", new() { Exact = true }).FillAsync("2");
        await page.Locator("[data-field-action='add-line']").ClickAsync();
        Check(!await page.Locator(".field-line-details").EvaluateAsync<bool>("node=>node.open"),
            "Native " + edition + " compact material rows keep details collapsed without hiding quantity controls.");
        await page.Locator("[data-field-ready]").ClickAsync();
        await page.GetByLabel("Consignes de préparation", new() { Exact = true }).FillAsync("Consigne fictive saisie dans le vrai WebView2 Windows " + edition);
        await page.Locator("[data-field-action='save-plan']").ClickAsync();
        await page.Locator("dialog.field-modal").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        var stored = await page.EvaluateAsync<JsonElement>("async id=>(await import('/js/bridge.js')).api.call('workspace.get',{id})", planId);
        var plan = stored.GetProperty("payload").GetProperty("plan");
        Check(plan.GetProperty("lines")[0].GetProperty("quantity").GetDecimal() == 2
            && plan.GetProperty("lines")[0].GetProperty("preparedQuantity").GetDecimal() == 2
            && plan.GetProperty("preparationNote").GetString()!.Contains("vrai WebView2", StringComparison.Ordinal),
            "Native " + edition + " terrain controls persist material and notes through the packaged C# bridge, not a browser API fixture.");
        await row.Locator("[data-field-select]").CheckAsync();
        await page.Locator("[data-field-action='preview']").ClickAsync(); await page.Locator(".field-report").WaitForAsync();
        Check((await page.Locator(".field-report").InnerTextAsync()).Contains(referenceLabel, StringComparison.Ordinal),
            "Native " + edition + " preview contains the actual selected preparation and its saved material.");
        var output = Path.Combine(_output, "field-native-" + edition + ".html");
        await page.Locator("dialog.field-modal").GetByRole(AriaRole.Button, new() { Name = "Exporter en HTML", Exact = true }).ClickAsync();
        await CompleteFileDialogAsync(process, output, new Regex(@"\AGLPI-Pilot-preparation-[0-9]{8}-[0-9]{6}(?:\.html)?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)));
        await WaitForPageAsync(page, "async()=>!(await import('/js/bridge.js')).api.pending");
        Check(File.Exists(output) && (await File.ReadAllTextAsync(output, _deadline.Token)).Contains("Liste de départ", StringComparison.Ordinal),
            "Native " + edition + " export uses the genuine Windows Save dialog and writes the selected HTML sheets.");
        await page.ScreenshotAsync(new() { Path = Path.Combine(_output, "field-native-" + edition + ".png"), FullPage = true });
        await page.Locator(".field-modal-heading").GetByRole(AriaRole.Button, new() { Name = "Fermer le panneau", Exact = true }).ClickAsync();
        Check(await page.EvaluateAsync<bool>("""
            async id=>{
              if(typeof id!=='string'||!id)throw new Error('A scalar intervention identifier is required.');
              const {api}=await import('/js/bridge.js');
              const fresh=await api.call('calendar.item',{id});
              if(fresh.intervention.id!==id)throw new Error('The native field appointment identity changed.');
              fresh.intervention.state='cancelled';
              const saved=await api.call('calendar.save',{intervention:fresh.intervention,expectedRevision:fresh.revision});
              if(!saved.saved)return false;
              const cancelled=await api.call('calendar.item',{id});
              return cancelled.intervention.id===id&&cancelled.intervention.state==='cancelled';
            }
            """, interventionId),
            "Native " + edition + " cancellation passes a scalar identifier through Playwright and persists the exact intervention state.");
        await page.Locator(".field-advanced-filters > summary").ClickAsync();
        await page.GetByLabel("Inclure les fiches terminées ou annulées", new() { Exact = true }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Appliquer", Exact = true }).ClickAsync();
        await row.GetByText("Annulée", new() { Exact = true }).WaitForAsync();
        Check(await row.IsVisibleAsync(), "Native " + edition + " preparation refresh follows a real calendar cancellation without deleting the recorded sheet.");
        await NavigateAsync(page, "home");
    }
}
