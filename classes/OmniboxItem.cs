using MyHomelabBrowser.classes;

enum OmniboxItemType
{
    Command,
    History
}

class OmniboxItem
{
    public OmniboxItemType Type { get; init; }

    // commun
    public string Primary { get; init; } = "";
    public string? Secondary { get; init; }

    // payload
    public CommandSetting? Command { get; init; }
    public HistoryEntry? History { get; init; }
}
