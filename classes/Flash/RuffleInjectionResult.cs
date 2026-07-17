namespace MyHomelabBrowser.classes.Flash
{
    public sealed record RuffleInjectionResult(
        bool Success,
        string Status,
        bool UsedLocalAssets,
        string? Error)
    {
        public static RuffleInjectionResult Ok(string status, bool local) =>
            new(true, status, local, null);

        public static RuffleInjectionResult Failed(string status, bool local, string? error = null) =>
            new(false, status, local, error);
    }
}
