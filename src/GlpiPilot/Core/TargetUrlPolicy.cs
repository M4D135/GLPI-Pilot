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

    public static string DeploymentBase(string value)
    {
        var uri = Validate(value);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && segments[^1].EndsWith(".php", StringComparison.OrdinalIgnoreCase)) segments.RemoveAt(segments.Count - 1);
        if (segments.Count > 0 && segments[^1].Equals("front", StringComparison.OrdinalIgnoreCase)) segments.RemoveAt(segments.Count - 1);
        return "/" + string.Join("/", segments) + (segments.Count == 0 ? "" : "/");
    }

    public static bool IsPortalRoot(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        var deployment = DeploymentBase(value).TrimEnd('/');
        if (path.Equals(deployment, StringComparison.OrdinalIgnoreCase)) return true;
        var last = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return last.Equals("index.php", StringComparison.OrdinalIgnoreCase) ||
               last.Equals("public.php", StringComparison.OrdinalIgnoreCase) ||
               last.Equals("helpdesk.public.php", StringComparison.OrdinalIgnoreCase) ||
               last.Equals("central.php", StringComparison.OrdinalIgnoreCase) ||
               last.Equals("helpdesk.php", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTicketList(string value, string portal)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var expected = Validate(portal);
        return uri.Scheme == expected.Scheme && uri.Authority == expected.Authority && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath.Equals(DeploymentBase(portal) + "front/ticket.php", StringComparison.Ordinal);
    }

    public static string MergeQuery(string menuUrl, string targetUrl)
    {
        // Preserve repeated criteria values and array parameters. Requested keys
        // override menu defaults as a group, without discarding their duplicates.
        var menu = Validate(PersistedTarget(menuUrl));
        var target = Validate(PersistedTarget(targetUrl));
        var requested = target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var keys = requested.Select(ParameterName).ToHashSet(StringComparer.Ordinal);
        var defaults = menu.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !keys.Contains(ParameterName(part)));
        var merged = requested.Concat(defaults).ToArray();
        return menu.GetLeftPart(UriPartial.Path) + (merged.Length == 0 ? "" : "?" + string.Join("&", merged)) + target.Fragment;
    }
}
