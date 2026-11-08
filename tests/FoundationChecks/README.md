# Foundation checks

Run `dotnet run --project tests/FoundationChecks`.

Uses synthetic local data only: compressed immutable receipt integrity, conservative historical comparison, unique GLPI identifiers, retention, interpretation fingerprints, quality reports, Excel inline/shared values and cached formulas, cancellation without destination loss, versioned settings recovery, and selective v2 configuration transfer.

These checks do not validate the enterprise GLPI DOM, a real SSO account, or Windows rendering. Those boundaries remain separate from the core tests.

## Legacy settings fixture

`Fixtures/settings-v2.0.0.json` was serialized using the unchanged C# settings models from GLPI-Pilot tag `v2.0.0`, commit `49e1ec21c5ce2f92d5c3211f21f946b7f73786d4`. A temporary portable console used the v2 FoundationChecks project, assigned synthetic GLPI URLs, selectors and preferences, kept one default query and one widget in each dashboard, then called `Normalize()` and `JsonSerializer.SerializeToUtf8Bytes` with `WriteIndented = true` and `MaxDepth = 32`. The WPF executable was not run and no user profile was read.

The fixture contains two old sources, which have no `Acquisition` property. Its SHA-256 is `e4906ae3cd8c9c83816640b08ca93ac4490e63a033fbcba3a7d6f7b0f3eacb34` (7,164 bytes). The checks cover preserving the original file at load, retaining the active acquisition during reselect/rename, saving the migration once, keeping other sources independent, refusing ambiguous or different search URLs, and respecting explicitly empty or partial modern recipes. This covers the settings transition from the published v2 format; it does not claim to run a historical v2 update helper (v2 had none).
