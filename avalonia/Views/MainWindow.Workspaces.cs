using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MyHomelabBrowser.classes.Workspaces;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Espaces de travail (même fichier workspaces.json que les autres éditions) : un ensemble
    /// d'onglets nommé (« Réseau », « Médias »…) que l'on rouvre depuis le menu principal.
    /// </summary>
    public sealed partial class MainWindow
    {
        MenuItem BuildWorkspacesMenu()
        {
            var items = new List<Control>();
            foreach (Workspace workspace in App.Workspaces.GetAll())
            {
                Workspace target = workspace;
                string count = workspace.Tabs.Count == 1 ? Tr("1 onglet") : Tr("{0} onglets", workspace.Tabs.Count);
                var entry = new MenuItem
                {
                    Header = workspace.Name + " (" + count + ")",
                    ItemsSource = new List<Control>
                    {
                        Item(Tr("Ouvrir"), () => OpenWorkspace(target)),
                        Item(Tr("Remplacer par les onglets ouverts"), () =>
                        {
                            if (SaveWorkspace(target.Name) is { } error)
                                ShowToast(error, warning: true);
                        }),
                        Item(Tr("Supprimer"), () =>
                        {
                            if (App.Workspaces.Remove(target.Id))
                                ShowToast(Tr("Espace « {0} » supprimé", target.Name));
                        })
                    }
                };
                items.Add(entry);
            }
            if (items.Count > 0)
                items.Add(new Separator());
            items.Add(Item(Tr("Enregistrer les onglets ouverts…"), () => _ = AskWorkspaceNameAsync()));

            var menu = Item(Tr("Espaces de travail"), () => { }, icon: "IconWorkspaces");
            menu.ItemsSource = items;
            return menu;
        }

        List<WorkspaceTab> CollectWorkspaceTabs()
            => _tabs.Select(tab => (tab, state: tab.GetSessionState()))
                .Where(x => x.state != null)
                .Select(x => new WorkspaceTab { Url = x.state!.Url, Title = x.state.Title ?? string.Empty, IsPinned = x.tab.IsPinned })
                .ToList();

        async System.Threading.Tasks.Task AskWorkspaceNameAsync()
        {
            int count = CollectWorkspaceTabs().Count;
            if (count == 0)
            {
                ShowToast(Tr("Ouvrez des pages web avant de créer un espace de travail."));
                return;
            }

            var form = new FormDialog(Tr("Nouvel espace de travail"), Tr("Enregistrer"));
            form.AddText(count == 1
                ? Tr("L'onglet ouvert sera rouvert d'un clic depuis le menu « Espaces de travail ».")
                : Tr("Les {0} onglets ouverts seront rouverts d'un clic depuis le menu « Espaces de travail ».", count));
            TextBox name = form.AddEntry(Tr("Nom (par exemple « Réseau » ou « Médias »)"));
            name.TextChanged += (_, _) => form.SetConfirmEnabled((name.Text ?? string.Empty).Trim().Length > 0);
            form.SetConfirmEnabled(false);
            form.Submit = () => System.Threading.Tasks.Task.FromResult(SaveWorkspace(name.Text ?? string.Empty));
            await form.ShowAsync(this);
        }

        /// <summary>Enregistre les onglets ouverts ; un espace du même nom est remplacé. Renvoie l'erreur éventuelle.</summary>
        string? SaveWorkspace(string name)
        {
            try
            {
                Workspace saved = App.Workspaces.Save(name, CollectWorkspaceTabs());
                ShowToast(saved.Tabs.Count == 1
                    ? Tr("Espace « {0} » enregistré : 1 onglet", saved.Name)
                    : Tr("Espace « {0} » enregistré : {1} onglets", saved.Name, saved.Tabs.Count));
                return null;
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Ouvre les onglets de l'espace à la suite des onglets actuels ; seul le premier est chargé
        /// tout de suite, les autres quand on les affiche. Un onglet d'accueil seul est remplacé.
        /// </summary>
        void OpenWorkspace(Workspace workspace)
        {
            BrowserTab? replaceable = _tabs.Count == 1 && _selected is { Page: TabPage.Home } home ? home : null;
            bool first = true;
            foreach (WorkspaceTab entry in workspace.Tabs.OrderByDescending(t => t.IsPinned))
            {
                BrowserTab tab;
                if (first)
                {
                    tab = NewTab(entry.Url, select: true);
                }
                else
                {
                    tab = new BrowserTab(this, isPrivate: false);
                    AddTab(tab, _tabs.Count);
                    tab.SetPending(entry.Url, entry.Title);
                }
                if (entry.IsPinned && !tab.IsPinned)
                    TogglePin(tab);
                first = false;
            }

            if (replaceable != null && _tabs.Count > 1)
                CloseTab(replaceable);
        }
    }
}
