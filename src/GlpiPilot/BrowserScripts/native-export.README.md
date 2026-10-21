# Native GLPI CSV export discovery — adapter 1.1.1

`native-export.js` is a read-only DOM adapter. Evaluating it installs
`window.__glpiPilotNative`. The adapter does not click, submit, navigate, fetch,
build export URLs, inspect cookies, read authentication storage, or copy hidden
form values. It describes the native controls already rendered by GLPI.

## Contract

```js
window.__glpiPilotNative.inspect(optionalSavedTableSelector)
// {
//   supported, reason, origin, tableSelector, containerSelector,
//   expectedTotal: number | null,
//   rangeStart: number | null, rangeEnd: number | null,
//   plans: [] | [plan],
//   displayPlan: null | displayPlan,
//   diagnostics: string[]
// }
```

`supported` describes native CSV export support. A separately proven
`displayPlan` can be available even when `supported` is false and `plans` is
empty. Unknown counts and ranges remain `null`; absence does not mean zero.

Every native export plan has these fields:

| Field | Meaning |
| --- | --- |
| `kind` | `menu`, `direct`, or `form`. |
| `menuSelector` | Unique selector of an existing export dropdown toggle, when relevant. |
| `directSelector` | Existing native all-pages CSV link. A hidden link is not yet actionable. |
| `formatSelector`, `formatValue` | Legacy `display_type` select and its actual all-pages CSV option value. |
| `scopeSelector`, `scopeValue` | Empty for the verified GLPI 10/11 implementations; no invented scope control. |
| `submitSelector`, `formSelector` | Legacy native export submit control and its GET form. |
| `label`, `format` | User-facing action label and `csv`. |
| `allPages` | Whether the currently rendered DOM proves all-pages CSV. |
| `needsOpen` | A menu must be opened through Playwright, followed by reinspection. |
| `formatHidden` | The native select is hidden behind a recognized visible Select2 widget. |
| `proof` | Short internal explanation of the DOM evidence. |

The host should follow this sequence:

1. Use the selected frame and revalidate its GLPI origin.
2. Call `inspect(savedTableSelector)`, with a unique saved selector where available.
3. For `menu`, click the visible toggle using Playwright and inspect again. Limit
   opening retries. An initially empty recognized menu may have `allPages:false`;
   this authorizes inspection of the opened menu, not an export.
4. Require a unique final `direct` or `form` plan with `allPages:true` and
   `format:"csv"`. Revalidate the current controls and origin before action.
5. Arm the download listener **before** selecting a legacy format or clicking
   the existing link/submit control. For a recognized hidden Select2 native
   select, Playwright can use `SelectOptionAsync` with `Force=true`.
6. Capture and parse the actual download. Compare parsed data rows against
   `expectedTotal` when known. A download event alone does not establish
   completeness or even prove that its body is CSV.

Only browser interaction performs the export. The host must not construct a URL
from `display_type` or submit the surrounding massive-action form.

### Display-all fallback contract

For a legacy pager, an existing list-limit control can supply this independent
plan. It changes the number of results displayed, rather than exporting CSV:

```js
{
  kind: 'select-change',
  selector: '#existing-limit-select',
  value: '9999999', // illustrative: copied from a sufficient existing option
  expectedTotal: 828,
  formSelector: '#existing-display-form',
  formMethod: 'post', // actual GET or POST method, lowercase
  hidden: false, // true only for a recognized visible Select2 replacement
  startsAtZero: true,
  proof: 'native-pager-list-limit-submit'
}
```

The control must be `select[name=glpilist_limit]` inside a dedicated form in a
pager associated with the chosen result table. Its existing action must have
the current page's origin and path. Only the native simple `submit()` onchange
forms are accepted, with no business-action field, query parameter, or custom
onsubmit handler. The smallest enabled existing numeric option at least as
large as the expected total is selected; an absent option is never fabricated.

`startsAtZero` requires an associated pager range beginning at zero or one and
no nonzero effective `start` offset. Missing URL offsets alone are insufficient
proof. If false, the host returns to the first list page, then reinspects the
current DOM before interacting with the control.

The host first enforces its configured maximum row count, then uses the
browser's normal select change. It waits for the rendered table to stabilize
and verifies its exact row count against the same pager's full range
`1..total / total`. Changing the control or seeing a successful POST alone does
not prove a complete extraction. This fallback is used only when no native
export plan is available.

### Structural diagnostics

`diagnostics` contains short allowlisted status codes and counts, such as
`dom_tables=6;data_tables=1`, `associated_legacy_pagers=2`, and
`legacy_form_1=verified-get-image-submit`. Entries are under 250 characters.
They never include result-cell contents, selector strings, labels, URLs, query
values, hidden form values, authentication storage, or tokens. The host should
log them only on meaningful changes or completion, rather than each poll.

## Verified native structures

The source structures were inspected on 21 October 2026 in the official
`glpi-project/glpi` repository.

### GLPI 10

Source: [templates/components/search/controls.html.twig, branch 10.0/bugfixes](https://github.com/glpi-project/glpi/blob/10.0/bugfixes/templates/components/search/controls.html.twig).

The `#dropdown-export` button opens a menu associated by `aria-labelledby`.
Links distinguish `Current page in CSV` from `All pages in CSV`. The latter
already contains a negative `display_type` value in its existing export URL.
The code does not hardcode the CSV numeric constant.

### GLPI 11

Source: [templates/components/search/controls.html.twig, branch 11.0/bugfixes](https://github.com/glpi-project/glpi/blob/11.0/bugfixes/templates/components/search/controls.html.twig).

`#dropdown-export-{rand}` opens an associated menu with separate `Current page`
and `All pages` headings. Both sections contain a link labelled only `CSV`.
The adapter therefore requires both the all-pages heading and the negative
`display_type` value. The menu may be moved to `document.body`; its preserved
ARIA association connects it to the original table controls.

### Table scope and result total

Sources:

- [GLPI 10 display_data.html.twig](https://github.com/glpi-project/glpi/blob/10.0/bugfixes/templates/components/search/display_data.html.twig)
- [GLPI 11 display_data.html.twig](https://github.com/glpi-project/glpi/blob/11.0/bugfixes/templates/components/search/display_data.html.twig)
- [GLPI 10 table.html.twig](https://github.com/glpi-project/glpi/blob/10.0/bugfixes/templates/components/search/table.html.twig)
- [GLPI 11 table.html.twig](https://github.com/glpi-project/glpi/blob/11.0/bugfixes/templates/components/search/table.html.twig)

The adapter prefers a unique visible `table.search-results`. Its closest
`.search-card` scopes the native controls. In GLPI 11 those controls can be
outside the massive-action form, so scoping only to that form would miss them.
The table's enclosing `form[data-search-itemtype][data-count]` provides the
expected result total. Unknown totals remain `null`.

On the legacy UI, official result classes are `tab_cadrehov` and
`tab_cadre_fixehov`. A `table.tab_cadre_pager` is a control table even though
it contains navigation `th` cells. Search criteria and other layout tables
must also be excluded when determining the nearest unambiguous result region.
That region can include controls outside the massive-action form.

Where no authoritative `data-count` exists, the adapter parses French
`De 1 à 20 sur 828` or English `From 1 to 20 of 828` only inside the associated
legacy pager. Grouped numbers are accepted. Conflicting pager totals produce
an unknown total, and conflicting ranges cannot prove a complete display.
Pagers belonging to a separate result list cannot change the chosen total.

### Legacy GET form

Sources:

- [GLPI 9.5.13 Html, `Html::printPager` and `Html::printPagerForm`](https://github.com/glpi-project/glpi/blob/9.5.13/inc/html.class.php)
- [GLPI 9.5.13 Dropdown, `Dropdown::showOutputFormat` and `Dropdown::showListLimit`](https://github.com/glpi-project/glpi/blob/9.5.13/inc/dropdown.class.php)
- [GLPI 9.5.13 Search, `Search::displayData` and output constants](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php)
- [GLPI 9.1.7 Dropdown, image submit control](https://github.com/glpi-project/glpi/blob/9.1.7/inc/dropdown.class.php)
- [GLPI 0.90.5 Dropdown, image submit control](https://github.com/glpi-project/glpi/blob/0.90.5/inc/dropdown.class.php)
- [GLPI 10 Html.php, `Html::printPager`](https://github.com/glpi-project/glpi/blob/10.0/bugfixes/src/Html.php)
- [GLPI 10 Dropdown.php, `Dropdown::showOutputFormat`](https://github.com/glpi-project/glpi/blob/10.0/bugfixes/src/Dropdown.php)

The native form uses GET to the already-rendered `/front/report.dynamic.php`
action, contains `select[name=display_type]`, and provides an export submit
button or image input. GLPI 9.5 uses a button; GLPI 9.1.7 and 0.90.5 expose an
`input[type=image][name=export]`. The adapter selects only an explicit
all-pages CSV option with its negative value. GLPI 9.5's CSV constant is 3,
so its all-pages value is -3; numeric codes are read from the DOM instead of
being inferred from a version. It never copies hidden inputs; normal browser
form submission preserves existing search filters.

The neighboring POST form in the legacy pager is the display-limit form,
not the native CSV export form. `Html::printPagerForm` and
`Dropdown::showListLimit` render its native `glpilist_limit` select with
`onchange="submit()"` and an existing 9999999 option among the available sizes.
This source evidence permits only the separately constrained display plan;
native report-export plans remain GET-only.

GLPI 9.5's `Search::displayData` supplies the item type to the top pager, so it
contains the export form. It supplies an empty output item type to the bottom
pager, which omits that form. Multiple distinct or unproven duplicate export
forms remain ambiguous; the adapter does not assume their hidden filters match.

## Deliberate limits

- This is source-based compatibility plus synthetic browser validation, not a
  claim that a live user deployment has been tested or that its exact GLPI
  version is known from a screenshot.
- English and French scope labels are supported, as well as common Spanish
  all-pages/current-page labels. Other translations or customized themes can
  require an adapter update or the ordinary DOM extraction fallback.
- All-pages CSV must already be exposed to the user's GLPI profile. The adapter
  does not work around an absent permission or an unavailable export button.
  The independent display-all fallback uses only an existing list-size option
  exposed in the same result pager.
- Cross-origin destinations, unrecognized endpoints, conflicting exports,
  custom inline click scripts, business form actions, and unproven all-pages
  options produce no plan. The host can explain the reason and use its DOM
  fallback when appropriate.
- Identical links in the same result region collapse to one plan, preferring
  a visible control. Distinct destinations are ambiguous. Multiple result
  tables require an explicit unambiguous table selection.
- Counts and downloaded data can change while GLPI prepares an export. A
  mismatch must be reported instead of silently claiming full extraction.

Reproducible browser checks are in `tests/NativeDomChecks`.
