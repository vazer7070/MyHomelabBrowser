using Microsoft.Web.WebView2.Core;
using System;

namespace MyHomelabBrowser.classes.AdBlock.Integration
{
    /// <summary>
    /// Vue WebView2 filtrée par le bloqueur : le contrôle WPF (édition Windows historique) ou
    /// la vue d'Avalonia. Le bloqueur ne dépend ainsi d'aucune interface graphique.
    /// </summary>
    public interface IAdBlockWebView
    {
        /// <summary>Null tant que la vue n'est pas prête, ou après sa fermeture.</summary>
        CoreWebView2? CoreWebView2 { get; }

        /// <summary>Exécute une action sur le fil de l'interface, en priorité basse.</summary>
        void Post(Action action);
    }
}
