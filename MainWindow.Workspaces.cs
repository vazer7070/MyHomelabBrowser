using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Workspaces;
using MyHomelabBrowser.controles;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Espaces de travail (ensembles d'onglets nommés)
        // ---------------------------
        private readonly WorkspaceStore _workspaces = new(() => Path.Combine(AppDataContext.Root, "workspaces.json"));

        List<WorkspaceTab> CollectWorkspaceTabs()
        {
            var tabs = new List<WorkspaceTab>();
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>())
            {
                if (tab.Tag is not WebTabContent { IsPrivate: false, IsCustomView: false } content)
                    continue;

                string? url = content.IsLegacyExternal ? content.LegacyUrl : content.PendingUrl ?? content.Web?.Source?.AbsoluteUri;
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                tabs.Add(new WorkspaceTab
                {
                    Url = url,
                    Title = (tab.Header as BrowserTabHeader)?.TabTitle ?? string.Empty,
                    IsPinned = content.IsPinned
                });
            }
            return tabs;
        }

        void SaveTabsAsWorkspace(string? existingName = null)
        {
            List<WorkspaceTab> tabs = CollectWorkspaceTabs();
            if (tabs.Count == 0)
            {
                ShowToast("Aucun onglet à enregistrer", "Ouvrez des pages web (hors navigation privée) avant de créer un espace.", ToastKind.Info);
                return;
            }

            string? name = existingName;
            if (name == null)
            {
                var prompt = new TextPromptDialog(
                    "Nouvel espace de travail",
                    $"Nom de l’espace ({tabs.Count} onglet{(tabs.Count > 1 ? "s" : "")}), par exemple « Réseau » ou « Médias » :",
                    "Enregistrer");
                if (!prompt.ShowFor(this))
                    return;
                name = prompt.Value;
            }

            try
            {
                Workspace saved = _workspaces.Save(name, tabs);
                ShowToast("Espace enregistré", $"« {saved.Name} » : {saved.Tabs.Count} onglet{(saved.Tabs.Count > 1 ? "s" : "")}.", ToastKind.Success);
            }
            catch (ArgumentException ex)
            {
                ShowToast("Enregistrement impossible", ex.Message, ToastKind.Warning);
            }
        }

        /// <summary>
        /// Ouvre les onglets de l'espace à la suite des onglets actuels ; seuls le premier
        /// est chargé tout de suite, les autres au moment où on les affiche.
        /// </summary>
        async void OpenWorkspace(Workspace workspace)
        {
            TabItem? replaceableStart = Tabs.Items.Count == 1 &&
                                        Tabs.Items[0] is TabItem { Tag: WebTabContent { IsCustomView: true } } only
                ? only
                : null;

            bool first = true;
            foreach (WorkspaceTab entry in workspace.Tabs.OrderByDescending(t => t.IsPinned))
            {
                WebTabContent? content = await CreateWebTabAsync(
                    entry.Url,
                    isPrivate: false,
                    select: first,
                    pendingTitle: entry.Title,
                    deferNavigation: !first);

                first = false;
                if (content == null || content.IsClosed || !entry.IsPinned)
                    continue;

                if (FindTabFor(content) is { Header: BrowserTabHeader header } tab)
                {
                    content.IsPinned = true;
                    ApplyPinState(tab, header, true);
                }
            }

            // Fenêtre qui ne contenait qu'une page d'accueil : on la remplace.
            if (replaceableStart != null && Tabs.Items.Count > 1)
                Tabs.Items.Remove(replaceableStart);
        }

        void DeleteWorkspace(Workspace workspace)
        {
            if (_workspaces.Remove(workspace.Id))
                ShowToast("Espace supprimé", workspace.Name, ToastKind.Info);
        }

        /// <summary>
        /// Sous-menu « Espaces de travail », reconstruit à chaque ouverture du menu principal.
        /// </summary>
        void PopulateWorkspacesMenu(MenuItem root)
        {
            root.Items.Clear();

            foreach (Workspace workspace in _workspaces.GetAll())
            {
                var item = new MenuItem
                {
                    Header = workspace.Name,
                    InputGestureText = workspace.Tabs.Count == 1 ? "1 onglet" : $"{workspace.Tabs.Count} onglets"
                };

                var open = new MenuItem { Header = "Ouvrir" };
                open.Click += (_, _) => OpenWorkspace(workspace);
                var update = new MenuItem { Header = "Remplacer par les onglets ouverts" };
                update.Click += (_, _) => SaveTabsAsWorkspace(workspace.Name);
                var delete = new MenuItem { Header = "Supprimer" };
                delete.Click += (_, _) => DeleteWorkspace(workspace);

                item.Items.Add(open);
                item.Items.Add(update);
                item.Items.Add(new Separator());
                item.Items.Add(delete);
                root.Items.Add(item);
            }

            if (root.Items.Count > 0)
                root.Items.Add(new Separator());

            var save = new MenuItem { Header = "Enregistrer les onglets ouverts…" };
            save.Click += (_, _) => SaveTabsAsWorkspace();
            root.Items.Add(save);
        }
    }
}
