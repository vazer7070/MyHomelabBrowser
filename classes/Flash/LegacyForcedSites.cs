public static class LegacyForcedSites
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var s = input.Trim().ToLowerInvariant();

        // retire protocol + slash
        s = s.Replace("https://", "").Replace("http://", "");
        s = s.Split('/')[0];

        // retire un wildcard si l'user le met
        if (s.StartsWith("*."))
            s = s.Substring(2);

        return s;
    }

    public static bool IsValidDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        if (domain.Contains(" ")) return false;
        if (!domain.Contains(".")) return false;
        if (domain.StartsWith(".")) return false;
        if (domain.EndsWith(".")) return false;
        return true;
    }

    public static bool MatchesHost(string host, string forcedDomain)
    {
        host = Normalize(host);
        forcedDomain = Normalize(forcedDomain);

        if (host == forcedDomain) return true;
        if (host.EndsWith("." + forcedDomain)) return true;

        return false;
    }
}
