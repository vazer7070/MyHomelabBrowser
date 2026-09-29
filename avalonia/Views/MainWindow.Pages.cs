using System;
using System.Linq;
using Avalonia.Controls;
using PommeBrowser.Views.Pages;

namespace PommeBrowser.Views
{
    /// <summary>Pages de PommeBrowser ouvertes dans un onglet (historique, favoris, coffre, paramètres…).</summary>
    public sealed partial class MainWindow
    {
        /// <summary>Onglet déjà ouvert sur cette page, sinon l'accueil vide de l'onglet actif, sinon un nouvel onglet.</summary>
        void OpenPage(TabPage kind, Func<Control> create)
        {
            if (_tabs.FirstOrDefault(t => t.Page == kind) is { } existing)
            {
                SelectTab(existing);
                return;
            }

            BrowserTab tab = _selected is { Page: TabPage.Home, IsPrivate: false, WebUrl.Length: 0 } home ? home : NewTab(null, select: true);
            tab.ShowPage(kind, create());
            SelectTab(tab);
        }

        public void OpenHistory() => OpenPage(TabPage.History, () => new HistoryPage(this));

        public void OpenFavorites() => OpenPage(TabPage.Favorites, () => new FavoritesPage(this));

        public void OpenPasswords() => OpenPage(TabPage.Passwords, () => new PasswordsPage(this));

        public void OpenDiagnostics() => OpenPage(TabPage.Diagnostics, () => new DiagnosticsPage(this));

        public void OpenReport() => OpenPage(TabPage.Report, () => new ReportPage(this, _selected));

        /// <summary>Paramètres, éventuellement ouverts sur une section (« flash », « adblock »…).</summary>
        public void OpenSettings(string? section = null)
        {
            if (_tabs.FirstOrDefault(t => t.Page == TabPage.Settings) is { CurrentPage: SettingsPage page } existing)
            {
                SelectTab(existing);
                page.ScrollTo(section);
                return;
            }
            OpenPage(TabPage.Settings, () => new SettingsPage(this, section));
        }
    }
}
