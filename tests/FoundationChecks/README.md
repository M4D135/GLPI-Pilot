# Foundation checks

Run `dotnet run --project tests/FoundationChecks`.

Uses synthetic local data only: compressed immutable receipt integrity, conservative historical comparison, unique GLPI identifiers, retention, interpretation fingerprints, quality reports, Excel inline/shared values and cached formulas, cancellation without destination loss, versioned settings recovery, and selective v2 configuration transfer.

These checks do not validate the enterprise GLPI DOM, a real SSO account, or Windows rendering. Those boundaries remain separate from the core tests.
