namespace PommeBrowser.SupportServer;

/// <summary>
/// Modules et catégories connus du navigateur (clés envoyées par ReportIssueView).
/// Les libellés sont fixés ici : le message Discord reste en français quelle que soit
/// la langue de l'utilisateur.
/// </summary>
public static class ReportCatalog
{
    public static readonly IReadOnlyList<string> Categories =
        new[] { "bug", "missing_feature", "feature_request", "ui_ux", "performance", "other" };

    public static string NormalizeModule(string? module)
        => module == "adblock" ? "adblock" : "browser";

    public static string NormalizeCategory(string? category)
        => category != null && Categories.Contains(category) ? category : "other";

    public static string ModuleLabel(string module) => module switch
    {
        "adblock" => "Bloqueur de publicités",
        _ => "PommeBrowser"
    };

    public static string ModuleIcon(string module) => module switch
    {
        "adblock" => "🛡️",
        _ => "🌐"
    };

    public static int ModuleColor(string module) => module switch
    {
        "adblock" => 0x3973C6,
        _ => 0xB03030
    };

    public static string CategoryLabel(string category) => category switch
    {
        "bug" => "Bug ou dysfonctionnement",
        "missing_feature" => "Fonctionnalité absente",
        "feature_request" => "Demande d’ajout",
        "ui_ux" => "Interface ou ergonomie",
        "performance" => "Performance",
        _ => "Autre"
    };
}
