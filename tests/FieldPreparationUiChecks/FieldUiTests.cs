using Microsoft.Playwright;

public static class FieldUiTests
{
 public static void Check(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
 private static async Task Open(IPage p) { await p.Locator("[data-field-open]").First.ClickAsync(); await p.Locator("[data-field-action='save-plan']").WaitForAsync(); }
 private static async Task AddLine(IPage p, string quantity = "0.125")
 {
  await p.GetByLabel("Référence à ajouter", new() { Exact = true }).SelectOptionAsync("cable");
  await p.GetByLabel("Quantité à ajouter", new() { Exact = true }).FillAsync(quantity);
  await p.Locator("[data-field-action='add-line']").ClickAsync(); await p.Locator("[data-field-line]").WaitForAsync();
 }
 public static List<(string Name, Func<IPage, Task> Run)> Cases(string evidence)
 {
  var checks = new List<(string Name, Func<IPage, Task> Run)>();
  void Add(string name, Func<IPage, Task> test) => checks.Add((name, test));
  Add("Actual workspace modules and both existing page exports remain importable", async p =>
  {
   Check(await p.EvaluateAsync<bool>("window.test.importsReady"));
   await p.EvaluateAsync("window.test.mount('interventions')"); Check(await p.Locator("[data-field-nav='interventions']").CountAsync() == 1);
   await p.EvaluateAsync("window.test.mount('pilot')"); Check(await p.Locator("[data-field-nav='pilot']").CountAsync() == 1);
  });
  Add("An existing preparation opens in a themed modal without mutating data", async p =>
  {
   await Open(p); Check(await p.Locator("dialog.field-modal[open]").CountAsync() == 1);
   Check(await p.EvaluateAsync<bool>("!window.test.calls.some(c=>c.operation==='field.save')"));
   Check((await p.Locator("dialog.field-modal").InnerTextAsync()).Contains("Contexte enregistré", StringComparison.Ordinal));
  });
  Add("Adding a fractional metre line preserves its unit and exact quantity", async p =>
  {
   await Open(p); await AddLine(p);
   await p.GetByLabel("Quantité à ajouter", new() { Exact = true }).FillAsync("-1");
   await p.Locator("[data-field-action='save-plan']").ClickAsync();
   await p.Locator("dialog.field-modal").WaitForAsync(new() { State = WaitForSelectorState.Detached });
   Check(await p.EvaluateAsync<bool>("window.test.lastSaved.plan.lines[0].unit==='metre' && window.test.lastSaved.plan.lines[0].quantity===0.125"));
   Check((await p.Locator(".field-material-summary").InnerTextAsync()).Contains("0,125", StringComparison.Ordinal));
  });
  Add("Selecting immediately after a saved preparation previews its new revision", async p =>
  {
   await Open(p); await AddLine(p); await p.EvaluateAsync("window.test.holdNextList=true");
   await p.Locator("[data-field-action='save-plan']").ClickAsync();
   await p.Locator("body[data-test-list-held=true]").WaitForAsync();
   var box = p.Locator("[data-field-select]").First;
   // If the stale row accepts the click, complete it before the delayed list arrives.
   var acceptsClick = await box.EvaluateAsync<bool>("node=>!node.disabled && !node.closest('[inert]')");
   var selection = box.CheckAsync();
   try { if (acceptsClick) await selection; }
   finally { await p.EvaluateAsync("window.test.releaseList()"); }
   await selection; await p.Locator(".field-workspace:not([aria-busy])").WaitForAsync();
   Check(await box.IsCheckedAsync(), "The refresh discarded the user's post-save selection.");
   await p.Locator("[data-field-action='preview']").ClickAsync();
   await p.Locator(".field-report").WaitForAsync();
   Check(await p.EvaluateAsync<bool>("window.test.lastReport?.selections?.[0]?.expectedRevision===2"),
      "The preview did not use the newly stored revision.");
  });
  Add("A failed post-save refresh retains the change and permits a safe list retry", async p =>
  {
   await Open(p); await AddLine(p);
   await p.EvaluateAsync("window.test.failNextList=true");
   await p.Locator("[data-field-action='save-plan']").ClickAsync();
   await p.GetByText("La préparation est enregistrée.", new() { Exact = false }).WaitForAsync();
   Check(await p.Locator("[data-field-select]").First.EvaluateAsync<bool>("n=>n.disabled||!!n.closest('[inert]')"),
      "The stale row was still interactive after its revision changed.");
   await p.GetByRole(AriaRole.Button, new() { Name = "Actualiser", Exact = true }).ClickAsync();
   await p.Locator("[data-field-select]").First.CheckAsync();
   await p.Locator("[data-field-action='preview']").ClickAsync();
   await p.Locator(".field-report").WaitForAsync();
   Check(await p.EvaluateAsync<bool>("window.test.lastReport?.selections?.[0]?.expectedRevision===2 && window.test.calls.filter(c=>c.operation==='field.save').length===1"));
  });
  Add("A rejected save keeps material quantities and notes visible", async p =>
  {
   await Open(p); await AddLine(p, "2"); await p.GetByLabel("Consignes de préparation", new() { Exact = true }).FillAsync("Ne pas perdre ma saisie");
   await p.EvaluateAsync("window.test.failSave=true"); await p.Locator("[data-field-action='save-plan']").ClickAsync();
   Check(await p.Locator("dialog.field-modal[open]").CountAsync() == 1);
   Check(await p.GetByLabel("Consignes de préparation", new() { Exact = true }).InputValueAsync() == "Ne pas perdre ma saisie");
   Check(await p.GetByLabel("Quantité prévue : Câble de test", new() { Exact = true }).InputValueAsync() == "2");
   Check((await p.Locator("dialog.field-modal .field-errors").InnerTextAsync()).Contains("conservée", StringComparison.Ordinal));
  });
  Add("Cancelling the no-material confirmation preserves existing lines", async p =>
  {
   await Open(p); await AddLine(p, "2"); await p.Locator("[data-field-none]").CheckAsync();
   await p.Locator("dialog.modal").GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
   Check(await p.Locator("[data-field-line]").CountAsync() == 1 && !await p.Locator("[data-field-none]").IsCheckedAsync());
  });
  Add("Explicitly choosing no material removes draft lines after confirmation", async p =>
  {
   await Open(p); await AddLine(p); await p.Locator("[data-field-none]").CheckAsync();
   await p.Locator("dialog.modal").GetByRole(AriaRole.Button, new() { Name = "Retirer les lignes", Exact = true }).ClickAsync();
   Check(await p.Locator("[data-field-line]").CountAsync() == 0);
   await p.Locator("[data-field-action='save-plan']").ClickAsync();
   Check(await p.EvaluateAsync<bool>("window.test.lastSaved.plan.noMaterialsRequired && window.test.lastSaved.plan.lines.length===0"));
  });
  Add("Prepared quantity greater than planned is blocked before a save call", async p =>
  {
   await Open(p); await AddLine(p, "2"); await p.GetByLabel("Quantité préparée : Câble de test", new() { Exact = true }).FillAsync("3");
   await p.Locator("[data-field-action='save-plan']").ClickAsync();
   Check(await p.EvaluateAsync<bool>("!window.test.calls.some(c=>c.operation==='field.save')"));
  });
  Add("Cancelling dirty-dialog closure returns to the exact draft", async p =>
  {
   await Open(p); await p.GetByLabel("Consignes de préparation", new() { Exact = true }).FillAsync("Saisie à conserver");
   await p.Locator(".field-modal-heading").GetByRole(AriaRole.Button, new() { Name = "Fermer le panneau", Exact = true }).ClickAsync();
   await p.Locator("dialog.modal").GetByRole(AriaRole.Button, new() { Name = "Annuler", Exact = true }).ClickAsync();
   Check(await p.GetByLabel("Consignes de préparation", new() { Exact = true }).InputValueAsync() == "Saisie à conserver");
  });
  Add("Catalogue save retains IDs and uses its expected revision", async p =>
  {
   await p.Locator("[data-field-action='catalogue']").ClickAsync();
   await p.GetByLabel("Libellé de référence : Câble de test", new() { Exact = true }).FillAsync("Câble renommé");
   await p.Locator("[data-field-action='save-catalogue']").ClickAsync();
   Check(await p.EvaluateAsync<bool>("window.test.lastCatalogue.expectedRevision===0 && window.test.lastCatalogue.catalogue.references[0].id==='cable' && window.test.lastCatalogue.catalogue.references[0].label==='Câble renommé'"));
  });
  Add("A conflicting catalogue save preserves the edited name", async p =>
  {
   await p.Locator("[data-field-action='catalogue']").ClickAsync();
   await p.GetByLabel("Libellé de référence : Câble de test", new() { Exact = true }).FillAsync("Saisie concurrente");
   await p.EvaluateAsync("window.test.failCatalogue=true"); await p.Locator("[data-field-action='save-catalogue']").ClickAsync();
   Check(await p.GetByLabel("Libellé de référence : Câble de test", new() { Exact = true }).InputValueAsync() == "Saisie concurrente");
  });
  Add("Ticket preparation passes stable identity and revision rather than row index", async p =>
  {
   await p.Locator("[data-field-action='ticket']").ClickAsync(); await p.Locator("[data-field-source='stable-ticket-42']").ClickAsync();
   Check(await p.EvaluateAsync<bool>("window.test.calls.some(c=>c.operation==='field.open' && c.args.ticketKey==='stable-ticket-42' && c.args.revision===7 && !('rowIndex' in c.args))"));
  });
  Add("Visit preparation passes the original stable occurrence key", async p =>
  {
   await p.Locator("[data-field-action='visit']").ClickAsync(); await p.Locator("[data-field-source='stable-series:ordinal-5']").ClickAsync();
   Check(await p.EvaluateAsync<bool>("window.test.calls.some(c=>c.operation==='field.open' && c.args.occurrenceKey==='stable-series:ordinal-5' && c.args.revision===7)"));
  });
  Add("A print request contains only selected persisted IDs and revisions", async p =>
  {
   await p.Locator("[data-field-select]").First.CheckAsync(); await p.Locator("[data-field-action='preview']").ClickAsync();
   await p.Locator(".field-report").WaitForAsync();
   Check(await p.EvaluateAsync<bool>("window.test.lastReport.selections.length===1 && window.test.lastReport.selections[0].expectedRevision===1"));
  });
  Add("Report sanitation preserves encoded text and removes active markup", async p =>
  {
   Check(await p.EvaluateAsync<bool>("() => { const n=window.test.safeFieldReport('<h1>Fiche</h1><script>window.infected=1</script><img src=evil onerror=alert(1)><p onclick=alert(1)>&lt;script&gt;texte&lt;/script&gt;</p><a href=javascript:alert(1)>texte</a>'); return !n.querySelector('script,img,a,[onclick],[onerror]') && n.textContent.includes('<script>texte</script>') && !window.infected; }"));
  });
  Add("Printing creates a selected-only portal that is removed after printing", async p =>
  {
   Check(await p.EvaluateAsync<bool>("() => { window.print=()=>{ window.printCalled=true; }; const r=window.test.safeFieldReport('<h1>Fiche sélectionnée</h1><p>Matériel</p>'); window.test.printFieldReport(r); return window.printCalled && document.body.classList.contains('field-printing') && document.querySelector('[data-field-print]').textContent.includes('Fiche sélectionnée'); }"));
   await p.EmulateMediaAsync(new() { Media = Media.Print });
   Check(await p.Locator("[data-field-print]").IsVisibleAsync() && !await p.Locator("#app").IsVisibleAsync());
   await p.EvaluateAsync("window.dispatchEvent(new Event('afterprint'))"); Check(await p.Locator("[data-field-print]").CountAsync() == 0);
  });
  Add("Aborting a page removes its modal and printable data", async p =>
  {
   await Open(p); await p.EvaluateAsync("window.test.abort()");
   Check(await p.Locator("dialog.field-modal").CountAsync() == 0 && await p.Locator("[data-field-print]").CountAsync() == 0);
  });
  Add("Long labels fit dark and light layouts at narrow and desktop widths", async p =>
  {
   foreach (var theme in new[] { "dark", "light" })
   foreach (var width in new[] { 390, 680, 1024, 1600 })
   {
    await p.SetViewportSizeAsync(width, 950); await p.EvaluateAsync("theme=>document.documentElement.dataset.theme=theme", theme);
    await p.EvaluateAsync("window.test.longLabels()"); await p.EvaluateAsync("window.test.mount()");
    Check(await p.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth+1"), $"Page overflow {theme}/{width}");
    await Open(p);
    Check(await p.EvaluateAsync<bool>("() => { const d=document.querySelector('dialog.field-modal'),r=d.getBoundingClientRect(); return r.left>=0 && r.right<=innerWidth+1 && r.height<=innerHeight && d.scrollWidth<=d.clientWidth+1; }"), $"Modal overflow {theme}/{width}");
    await p.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"editor-{theme}-{width}.png"), FullPage = true });
    await p.Locator(".field-modal-heading").GetByRole(AriaRole.Button, new() { Name = "Fermer le panneau", Exact = true }).ClickAsync();
   }
  });
  Add("Reduced-motion preference leaves the displayed counters stable", async p =>
  {
   await p.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
   Check(await p.EvaluateAsync<bool>("() => [...document.querySelectorAll('.field-metric strong')].every(n=>getComputedStyle(n).animationName==='none' && getComputedStyle(n).transitionDuration.split(',').every(v=>parseFloat(v)===0))"));
  });
  return checks;
 }
}
