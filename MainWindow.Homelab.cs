using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.controles;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Services du homelab (page d'accueil)
        // ---------------------------
        private readonly HomelabServiceStore _homelabServices = new(() => Path.Combine(AppDataContext.Root, "services.json"));
        private readonly ObservableCollection<ServiceTile> _serviceTiles = new();
        private ServiceMonitor? _serviceMonitor;

        void InitializeHomelab()
        {
            _serviceMonitor = new ServiceMonitor(() => _homelabServices.GetAll());

            _serviceMonitor.CheckStarted += service => Dispatcher.BeginInvoke(() =>
            {
                if (FindServiceTile(service.Id) is { } tile)
                    tile.IsChecking = true;
            });

            _serviceMonitor.Checked += (service, result) => Dispatcher.BeginInvoke(() =>
            {
                if (FindServiceTile(service.Id) is { } tile)
                {
                    tile.IsChecking = false;
                    tile.Result = result;
                }
            });

            _serviceMonitor.StateChanged += (service, _, current) =>
                Dispatcher.BeginInvoke(() => NotifyServiceStateChanged(service, current));

            _homelabServices.Changed += () => Dispatcher.BeginInvoke(SyncServiceTiles);

            SyncServiceTiles();
            Closed += (_, _) => _serviceMonitor.Dispose();
        }

        void ApplyServiceMonitoringSettings(BrowserSettings settings)
        {
            if (_serviceMonitor == null)
                return;

            _serviceMonitor.Stop();
            if (settings.ServiceMonitoring)
                _serviceMonitor.Start(TimeSpan.FromSeconds(Math.Clamp(settings.ServiceCheckIntervalSeconds, 15, 3600)));
        }

        void ReloadHomelabForProfile()
        {
            _serviceMonitor?.Reset();
            SyncServiceTiles();

            if (_settings.Settings.ServiceMonitoring && _serviceMonitor != null)
                _ = _serviceMonitor.CheckAllAsync();
        }

        ServiceTile? FindServiceTile(Guid id) => _serviceTiles.FirstOrDefault(t => t.Id == id);

        /// <summary>
        /// Aligne les tuiles (partagées par toutes les pages d'accueil) sur la liste enregistrée.
        /// </summary>
        void SyncServiceTiles()
        {
            IReadOnlyList<HomelabService> services = _homelabServices.GetAll();
            var ids = services.Select(s => s.Id).ToHashSet();

            foreach (ServiceTile stale in _serviceTiles.Where(t => !ids.Contains(t.Id)).ToList())
                _serviceTiles.Remove(stale);

            foreach (HomelabService service in services)
            {
                ServiceTile? tile = FindServiceTile(service.Id);
                if (tile == null)
                {
                    _serviceTiles.Add(new ServiceTile(service) { Result = _serviceMonitor?.GetResult(service.Id) ?? ServiceCheckResult.Unknown });
                    continue;
                }

                bool regroup = tile.Name != (string.IsNullOrWhiteSpace(service.Name) ? tile.Address : service.Name) ||
                               !string.Equals(tile.Group, service.Group, StringComparison.CurrentCulture);
                tile.Update(service);

                // Nom ou groupe modifié : on réinsère la tuile pour la reclasser.
                if (regroup)
                {
                    _serviceTiles.Remove(tile);
                    _serviceTiles.Add(tile);
                }
            }

            RefreshStartPageServices();
        }

        int CountLocalFavoritesNotInServices()
        {
            var known = _serviceTiles.Select(t => t.Url.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _favorites.Count(f =>
                Uri.TryCreate(f.Url, UriKind.Absolute, out Uri? uri) &&
                UrlResolver.IsLocalHost(uri.Host) &&
                !known.Contains(f.Url.TrimEnd('/')));
        }

        void RefreshStartPageServices()
        {
            int localFavorites = CountLocalFavoritesNotInServices();
            foreach (EmptyStartPage page in EnumerateStartPages())
                page.SetServices(_serviceTiles, localFavorites);
        }

        IEnumerable<EmptyStartPage> EnumerateStartPages()
        {
            foreach (var item in Tabs.Items)
            {
                if (item is TabItem { Tag: WebTabContent { IsCustomView: true } content } &&
                    content.HostGrid.Children.Count > 0 &&
                    content.HostGrid.Children[0] is EmptyStartPage page)
                {
                    yield return page;
                }
            }
        }

        void WireStartPageServices(EmptyStartPage view, TabItem? owningTab)
        {
            view.SetServices(_serviceTiles, CountLocalFavoritesNotInServices());

            view.ServiceOpenRequested += (url, newTab) =>
            {
                if (!newTab && owningTab != null && Tabs.Items.Contains(owningTab))
                    ReplaceTabWithWeb(owningTab, url);
                else
                    _ = CreateWebTabAsync(url, isPrivate: false, select: !newTab);
            };

            view.AddServiceRequested += () => AddOrEditService(null);
            view.EditServiceRequested += id => AddOrEditService(_homelabServices.Get(id));
            view.DeleteServiceRequested += DeleteService;
            view.CheckServiceRequested += id =>
            {
                if (_homelabServices.Get(id) is { } service && _serviceMonitor != null)
                    _ = _serviceMonitor.CheckOneAsync(service);
            };
            view.CheckAllServicesRequested += () =>
            {
                if (_serviceMonitor != null)
                    _ = _serviceMonitor.CheckAllAsync();
            };
            view.ImportLocalFavoritesRequested += ImportLocalFavoritesAsServices;
        }

        void AddOrEditService(HomelabService? existing, string? suggestedUrl = null, string? suggestedName = null)
        {
            var groups = _homelabServices.GetAll().Select(s => s.Group).OfType<string>();
            var dialog = new ServiceEditDialog(existing, groups, suggestedUrl, suggestedName);
            if (!dialog.ShowFor(this))
                return;

            _homelabServices.AddOrUpdate(dialog.Result);

            if (dialog.Result.Monitor && _serviceMonitor != null)
                _ = _serviceMonitor.CheckOneAsync(dialog.Result);
        }

        void DeleteService(Guid id)
        {
            if (_homelabServices.Get(id) is not { } service)
                return;

            _homelabServices.Remove(id);
            ShowToast("Service retiré", service.Name, ToastKind.Info, "Annuler", () => _homelabServices.AddOrUpdate(service));
        }

        void ImportLocalFavoritesAsServices()
        {
            var services = _favorites
                .Where(f => Uri.TryCreate(f.Url, UriKind.Absolute, out Uri? uri) && UrlResolver.IsLocalHost(uri.Host))
                .Select(f => new HomelabService
                {
                    Name = string.IsNullOrWhiteSpace(f.Title) ? new Uri(f.Url).Host : f.Title,
                    Url = f.Url,
                    Group = f.Folder
                })
                .ToList();

            int added = _homelabServices.AddRange(services);
            if (added > 0 && _serviceMonitor != null)
                _ = _serviceMonitor.CheckAllAsync();

            ShowToast("Services ajoutés", added == 1 ? "1 favori local ajouté." : $"{added} favoris locaux ajoutés.", ToastKind.Success);
        }

        /// <summary>
        /// Menu « Ajouter la page aux services » : pré-remplit avec l'origine de la page active.
        /// </summary>
        void AddCurrentPageAsService()
        {
            string? url = GetCurrentPageUrl();
            string? name = null;
            if (url != null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
            {
                url = uri.GetLeftPart(UriPartial.Authority) + "/";
                if (Tabs.SelectedItem is TabItem { Header: BrowserTabHeader header })
                    name = header.TabTitle;
            }
            else
            {
                url = null;
            }

            AddOrEditService(null, url, name);
        }

        void NotifyServiceStateChanged(HomelabService service, ServiceState current)
        {
            if (!_settings.Settings.ServiceAlerts)
                return;

            string name = string.IsNullOrWhiteSpace(service.Name) ? service.Url : service.Name;
            if (current == ServiceState.Offline)
            {
                ServiceCheckResult result = _serviceMonitor?.GetResult(service.Id) ?? ServiceCheckResult.Unknown;
                ShowToast(
                    $"{name} ne répond plus",
                    string.IsNullOrWhiteSpace(result.Error) ? service.Url : $"{service.Url} — {result.Error}",
                    ToastKind.Warning,
                    "Ouvrir",
                    () => CreateTab(service.Url),
                    TimeSpan.FromSeconds(12));
            }
            else
            {
                ShowToast($"{name} est de nouveau en ligne", service.Url, ToastKind.Success);
            }
        }

        private void MainMenu_AddService_Click(object sender, RoutedEventArgs e) => AddCurrentPageAsService();
    }
}
