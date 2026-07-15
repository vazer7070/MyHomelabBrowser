namespace MyHomelabBrowser.classes.AdBlock.Services
{
    public static class AdBlockModuleHost
    {
        private static readonly object Gate = new();
        private static AdBlockModuleService? _current;

        public static AdBlockModuleService Current
        {
            get
            {
                lock (Gate)
                    return _current ??= new AdBlockModuleService();
            }
        }
    }
}
