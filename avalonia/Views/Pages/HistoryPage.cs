using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MyHomelabBrowser.classes;
using PommeBrowser.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Pages
{
    /// <summary>Historique du profil : recherche, visites par jour, suppression.</summary>
    public sealed class HistoryPage : PageBase
    {
        const int MaxRows = 400;

        string _query = string.Empty;

        public HistoryPage(MainWindow window) : base(window, Tr("Historique"))
        {
            AddToolbar(SearchBox(Tr("Rechercher dans l'historique"), text =>
            {
                _query = text;
                ScheduleRefresh();
            }));
            AddToolbar(TextButton(Tr("Effacer…"), () => _ = AskClearAsync(), danger: true));
            App.History.Changed += ScheduleRefresh;
        }

        protected override void OnDisposed() => App.History.Changed -= ScheduleRefresh;

        protected override void Build(StackPanel content)
        {
            string[] tokens = _query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var entries = new List<HistoryEntry>();
            IReadOnlyList<HistoryEntry> recent = App.History.Recent;
            for (int i = recent.Count - 1; i >= 0 && entries.Count < MaxRows; i--)
            {
                HistoryEntry entry = recent[i];
                string haystack = (entry.Title + " " + entry.Url).ToLowerInvariant();
                if (tokens.All(haystack.Contains))
                    entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                content.Children.Add(EmptyState("IconHistory",
                    tokens.Length > 0 ? Tr("Aucun résultat") : Tr("Historique vide"),
                    tokens.Length > 0 ? Tr("Aucune page visitée ne correspond à cette recherche.") : Tr("Les pages que vous visitez apparaîtront ici.")));
                return;
            }

            foreach (IGrouping<DateTime, HistoryEntry> day in entries.GroupBy(e => e.VisitedAt.Date))
            {
                content.Children.Add(Heading(DayLabel(day.Key)));
                content.Children.Add(Card(day.Select(entry =>
                {
                    HistoryEntry target = entry;
                    return Row(string.IsNullOrWhiteSpace(entry.Title) ? UrlDisplay.ForDisplay(entry.Url) : entry.Title,
                        entry.VisitedAt.ToString("t", Culture) + " · " + UrlDisplay.ForDisplay(entry.Url),
                        SiteIcon(entry.Url),
                        () => Window.Navigate(target.Url),
                        IconButton("IconOpen", Tr("Ouvrir dans un nouvel onglet"), () => Window.NewTab(target.Url, select: true)),
                        IconButton("IconDelete", Tr("Supprimer de l'historique"), () => App.History.Remove(new[] { target })));
                }).ToArray()));
            }
        }

        static string DayLabel(DateTime day)
        {
            if (day == DateTime.Today)
                return Tr("Aujourd'hui");
            if (day == DateTime.Today.AddDays(-1))
                return Tr("Hier");
            return day.ToString("D", Culture);
        }

        async System.Threading.Tasks.Task AskClearAsync()
        {
            int choice = await Dialogs.Dialogs.ChoiceAsync(Window, Tr("Effacer l'historique"), Tr("Quelle période voulez-vous effacer ?"),
                (Tr("Dernière heure"), false, false), (Tr("Aujourd'hui"), false, false), (Tr("Tout"), false, true), (Tr("Annuler"), false, false));
            DateTime? since = choice switch
            {
                0 => DateTime.Now.AddHours(-1),
                1 => DateTime.Today,
                2 => DateTime.MinValue,
                _ => null
            };
            if (since is { } value)
                App.History.RemoveSince(value);
        }
    }
}
