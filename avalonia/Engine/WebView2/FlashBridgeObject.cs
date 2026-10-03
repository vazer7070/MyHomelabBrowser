using System;
using System.Runtime.InteropServices;

namespace PommeBrowser.Engine.WebView2
{
    /// <summary>
    /// Objet offert à la page (chrome.webview.hostObjects.sync.pommeFlash) : CallFunction transmet
    /// un appel de la page au contenu lu par le moteur Flash intégré et rend sa réponse. Visible de
    /// COM, comme l'exige AddHostObjectToScript ; rien d'autre n'y est exposé.
    /// </summary>
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ComVisible(true)]
    public sealed class FlashBridgeObject
    {
        readonly Func<string, string?> _callFunction;

        internal FlashBridgeObject(Func<string, string?> callFunction)
        {
            _callFunction = callFunction;
        }

        /// <summary>Requête XML de Flash (&lt;invoke name="…"&gt;) ; réponse : code JavaScript, ou null.</summary>
        public string? CallFunction(string request) => _callFunction(request ?? string.Empty);
    }
}
