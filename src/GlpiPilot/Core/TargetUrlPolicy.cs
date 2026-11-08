namespace GlpiPilot.Core;

public static class TargetUrlPolicy
{
    private static readonly HashSet<string> AuthenticationParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "state", "session_state", "access_token", "id_token", "refresh_token",
        "SAMLResponse", "SAMLRequest", "RelayState", "wresult", "wctx", "client_secret"
    };

    public static Uri Validate(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Renseigne l’adresse http:// ou https:// de ta recherche GLPI, sans identifiant dans l’adresse.");
        return uri;
    }

    public static string PersistedTarget(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var uri = Validate(value);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !AuthenticationParameters.Contains(ParameterName(part))).ToArray();
        var fragment = uri.Fragment;
        if (fragment.TrimStart('#').Split('&').Any(part => AuthenticationParameters.Contains(ParameterName(part)))) fragment = "";
        return uri.GetLeftPart(UriPartial.Path) + (query.Length == 0 ? "" : "?" + string.Join("&", query)) + fragment;
    }

    private static string ParameterName(string value)
    {
        try { return Uri.UnescapeDataString(value.Split('=', 2)[0].Replace('+', ' ')); }
        catch (UriFormatException) { return value.Split('=', 2)[0]; }
    }

    public static bool SamePage(string a, string b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var one) || !Uri.TryCreate(b, UriKind.Absolute, out var two)) return false;
        return one.Scheme.Equals(two.Scheme, StringComparison.OrdinalIgnoreCase) &&
               one.Authority.Equals(two.Authority, StringComparison.OrdinalIgnoreCase) &&
               one.AbsolutePath.TrimEnd('/').Equals(two.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    public static bool IncludesSearch(string actual, string requested)
    {
        if (!SamePage(actual, requested)) return false;
        var wanted = new Uri(requested).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var got = new Uri(actual).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        return wanted.All(part => got.Contains(part, StringComparer.Ordinal));
    }

    public static string FirstPage(string target)
    {
        var uri = Validate(target);
        // GLPI's rendered searches use `start` as their offset. Keep every filter intact.
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !ParameterName(part).Equals("start", StringComparison.OrdinalIgnoreCase)).ToList();
        query.Add("start=0");
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", query) + uri.Fragment;
    }
}
