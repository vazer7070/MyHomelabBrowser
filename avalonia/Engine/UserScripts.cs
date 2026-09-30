namespace PommeBrowser.Engine
{
    /// <summary>
    /// Scripts injetés par les moteurs qui ne savent pas eux-mêmes limiter un script au cadre
    /// principal ou l'exécuter à la fin du chargement (WebView2, WKWebView selon les cas).
    /// </summary>
    public static class UserScripts
    {
        public static string Wrap(string source, bool allFrames, bool atDocumentStart)
        {
            string body = atDocumentStart
                ? source
                : "const __pommeRun = function () {\n" + source + "\n};\n" +
                  "if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', __pommeRun, { once: true }); else __pommeRun();";
            string guard = allFrames ? string.Empty : "if (window !== window.top) return;\n";
            return "(function () {\n" + guard + body + "\n})();";
        }
    }
}
