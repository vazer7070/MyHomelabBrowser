using MyHomelabBrowser.classes;
using System.Windows.Controls;

enum OmniboxItemType
{
    Command,
    History,
    Favorite,
    Url,
    Search,
    Tab
}

class OmniboxItem
{
    public OmniboxItemType Type { get; init; }

    public string Primary { get; init; } = "";
    public string? Secondary { get; init; }

    // Glyphe Segoe Fluent Icons affiché à gauche de la suggestion.
    public string Icon => Type switch
    {
        OmniboxItemType.Command => "",
        OmniboxItemType.History => "",
        OmniboxItemType.Favorite => "",
        OmniboxItemType.Url => "",
        OmniboxItemType.Tab => "",
        _ => ""
    };

    public string? Url { get; init; }
    public CommandSetting? Command { get; init; }
    public TabItem? Tab { get; init; }
}
