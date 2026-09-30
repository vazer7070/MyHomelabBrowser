using System;
using System.Collections.Generic;
using System.Linq;
using MyHomelabBrowser.classes.Workspaces;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Espaces de travail (même fichier workspaces.json que l'édition Windows) : un ensemble
    /// d'onglets nommé (« Réseau », « Médias »…) que l'on rouvre depuis le menu principal.
    /// </summary>
    sealed partial class BrowserWindow
    {
        Gio.Menu? _workspacesMenu;

        /// <summary>Sous-menu reconstruit à chaque ouverture du menu principal.</summary>
        void RebuildWorkspacesMenu()
        {
            if (_workspacesMenu == null)
                return;

            _workspacesMenu.RemoveAll();
            var list = Gio.Menu.New();
            foreach (Workspace workspace in _app.Workspaces.GetAll())
            {
                var entry = Gio.Menu.New();
                entry.AppendItem(WorkspaceItem(Tr("Ouvrir"), "win.workspace-open", workspace.Id));
                entry.AppendItem(WorkspaceItem(Tr("Remplacer par les onglets ouverts"), "win.workspace-replace", workspace.Id));
                entry.AppendItem(WorkspaceItem(Tr("Supprimer"), "win.workspace-delete", workspace.Id));
                string count = workspace.Tabs.Count == 1 ? Tr("1 onglet") : Tr("{0} onglets", workspace.Tabs.Count);
                list.AppendSubmenu((workspace.Name + " (" + count + ")").Replace("_", "__"), entry);
            }
            _workspacesMenu.AppendSection(null, list);

            var save = Gio.Menu.New();
            save.Append(Tr("Enregistrer les onglets ouverts…"), "win.workspace-save");
            _workspacesMenu.AppendSection(null, save);
        }

        static Gio.MenuItem WorkspaceItem(string label, string action, Guid id)
        {
            var item = Gio.MenuItem.New(label, null);
            item.SetActionAndTargetValue(action, GLib.Variant.NewString(id.ToString()));
            return item;
        }

        void AddWorkspaceActions()
        {
            AddWithId("workspace-open", workspace => OpenWorkspace(workspace));
            AddWithId("workspace-replace", workspace =>
            {
                if (SaveWorkspace(workspace.Name) is { } error)
                    ShowToast(error);
            });
            AddWithId("workspace-delete", workspace =>
            {
                if (_app.Workspaces.Remove(workspace.Id))
                    ShowToast(Tr("Espace « {0} » supprimé", workspace.Name));
            });
            Add("workspace-save", AskWorkspaceName);
        }

        void AddWithId(string name, Action<Workspace> handler)
        {
            var action = Gio.SimpleAction.New(name, GLib.VariantType.New("s"));
            action.OnActivate += (_, args) =>
            {
                if (Guid.TryParse(args.Parameter?.GetString(out nuint _), out Guid id) &&
                    _app.Workspaces.GetAll().FirstOrDefault(w => w.Id == id) is { } workspace)
                {
                    handler(workspace);
                }
            };
            Window.AddAction(action);
        }

        List<WorkspaceTab> CollectWorkspaceTabs()
            => Tabs.Select(tab => (tab, session: tab.GetSessionTab()))
                .Where(x => x.session != null)
                .Select(x => new WorkspaceTab
                {
                    Url = x.session!.Url,
                    Title = x.session.Title ?? string.Empty,
                    IsPinned = x.tab.Page?.GetPinned() == true
                })
                .ToList();

        void AskWorkspaceName()
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
            Adw.EntryRow name = FormDialog.EntryRow(Tr("Nom (par exemple « Réseau » ou « Médias »)"));
            form.AddGroup(null, name);
            name.OnChanged += (_, _) => form.SetConfirmEnabled(name.GetText().Trim().Length > 0);
            form.SetConfirmEnabled(false);
            form.Submit = () => System.Threading.Tasks.Task.FromResult(SaveWorkspace(name.GetText()));
            form.Present(Window);
            name.GrabFocus();
        }

        /// <summary>Enregistre les onglets ouverts ; un espace du même nom est remplacé. Renvoie l'erreur éventuelle.</summary>
        string? SaveWorkspace(string name)
        {
            try
            {
                Workspace saved = _app.Workspaces.Save(name, CollectWorkspaceTabs());
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
        /// tout de suite, les autres quand on les affiche. Une fenêtre qui ne contenait qu'une page
        /// d'accueil est remplacée.
        /// </summary>
        void OpenWorkspace(Workspace workspace)
        {
            BrowserTab? replaceable = TabCount == 1 && Current is { Content: TabContent.Home } home ? home : null;

            bool first = true;
            foreach (WorkspaceTab entry in workspace.Tabs.OrderByDescending(t => t.IsPinned))
            {
                var tab = new BrowserTab(_app, this);
                AddTab(tab, select: first);
                if (first)
                    tab.Navigate(entry.Url);
                else
                    tab.SetPending(entry.Url, entry.Title);
                if (entry.IsPinned && tab.Page != null)
                    _tabs.SetPagePinned(tab.Page, true);
                first = false;
            }

            if (replaceable != null && TabCount > 1)
                CloseTab(replaceable);
        }
    }
}
