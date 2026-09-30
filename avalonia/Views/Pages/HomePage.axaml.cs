using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using PommeBrowser.Core;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Pages
{
    /// <summary>
    /// Page d'accueil (nouvel onglet) : recherche, services du homelab avec leur état, favoris.
    /// Comme l'accueil de l'édition Windows.
    /// </summary>
    public sealed partial class HomePage : UserControl, IDisposable
    {
        const int MaxFavorites = 12;

        readonly MainWindow _window;
        readonly BrowserApp _app;
        bool _settingText;

        public HomePage() : this(null!)
        {
        }

        public HomePage(MainWindow window)
        {
            _window = window;
            _app = window?.App ?? BrowserApp.Current;
            InitializeComponent();

            int hour = DateTime.Now.Hour;
            GreetingText.Text = hour switch
            {
                < 5 => Tr("Bonne nuit"),
                < 12 => Tr("Bonjour"),
                < 18 => Tr("Bon après-midi"),
                _ => Tr("Bonsoir")
            };

            SuggestionList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<OmniboxEntry>((entry, _) => _window.BuildSuggestion(entry), supportsRecycling: false);
            MainWindow.HandleSuggestionClicks(SuggestionList, Run);

            // TextChanging : signalé tout de suite, pendant que _settingText est levé (voir la barre d'adresse).
            SearchBox.TextChanging += (_, _) =>
            {
                ClearButton.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
                if (_settingText)
                    return;
                var entries = _window.BuildSuggestions((SearchBox.Text ?? string.Empty).Trim());
                SuggestionList.ItemsSource = entries;
                SuggestionList.SelectedIndex = entries.Count > 0 ? 0 : -1;
                SuggestionsPopup.IsOpen = entries.Count > 0 && SearchBox.IsFocused;
            };
            SearchBox.LostFocus += (_, _) => SuggestionsPopup.IsOpen = false;
            SearchBox.AddHandler(KeyDownEvent, SearchBox_KeyDown, RoutingStrategies.Tunnel);

            _app.ServicesChanged += RefreshServices;
            _app.Favorites.Changed += RefreshFavorites;
            FaviconStore.FaviconUpdated += OnFaviconUpdated;
            RefreshServices();
            RefreshFavorites();
        }

        public void Dispose()
        {
            _app.ServicesChanged -= RefreshServices;
            _app.Favorites.Changed -= RefreshFavorites;
            FaviconStore.FaviconUpdated -= OnFaviconUpdated;
            SuggestionsPopup.IsOpen = false;
        }

        void OnFaviconUpdated(string host)
        {
            RefreshServices();
            RefreshFavorites();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SearchBox.Focus());
        }

        void SearchBox_KeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    if (SuggestionsPopup.IsOpen && SuggestionList.SelectedIndex > 0 && SuggestionList.SelectedItem is OmniboxEntry entry)
                        Run(entry);
                    else
                        _window.SubmitInput(SearchBox.Text ?? string.Empty, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                    break;
                case Key.Escape:
                    SuggestionsPopup.IsOpen = false;
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                    if (!SuggestionsPopup.IsOpen || SuggestionList.ItemCount == 0)
                        return;
                    e.Handled = true;
                    SuggestionList.SelectedIndex = Math.Clamp(SuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, SuggestionList.ItemCount - 1);
                    break;
            }
        }

        void Run(OmniboxEntry entry)
        {
            SuggestionsPopup.IsOpen = false;
            entry.Run();
        }

        void Clear_Click(object? sender, RoutedEventArgs e)
        {
            _settingText = true;
            SearchBox.Text = string.Empty;
            _settingText = false;
            SearchBox.Focus();
        }

        // ---------------------------------------------------------------
        // Services
        // ---------------------------------------------------------------

        void RefreshServices()
        {
            ServicesTiles.Children.Clear();
            var services = _app.Services.GetAll();
            ServicesEmpty.IsVisible = services.Count == 0;

            foreach (IGrouping<string?, HomelabService> group in services.GroupBy(s => string.IsNullOrWhiteSpace(s.Group) ? null : s.Group!.Trim()))
            {
                if (group.Key != null)
                {
                    var header = new TextBlock
                    {
                        Text = group.Key,
                        FontSize = 11.5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        Width = 600,
                        Margin = new Thickness(0, 10, 0, 0)
                    };
                    header.Classes.Add("hint");
                    ServicesTiles.Children.Add(header);
                }

                foreach (HomelabService service in group)
                    ServicesTiles.Children.Add(ServiceTileFor(service));
            }
        }

        Control ServiceTileFor(HomelabService service)
        {
            var tile = new ServiceTile(service) { Result = _app.Monitor.GetResult(service.Id) };
            string dotBrush = tile.State switch
            {
                ServiceState.Online => "SuccessBrush",
                ServiceState.Degraded => "WarningBrush",
                ServiceState.Offline => "DangerBrush",
                _ => "TextTertiaryBrush"
            };

            var dot = new Ellipse
            {
                Width = 13,
                Height = 13,
                StrokeThickness = 2.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            dot.Bind(Shape.FillProperty, this.GetResourceObservable(dotBrush));
            dot.Bind(Shape.StrokeProperty, this.GetResourceObservable("WindowBackgroundBrush"));

            var icon = new Panel { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center };
            icon.Children.Add(Badge(tile.Initial, FaviconStore.TryGet(service.Url)));
            icon.Children.Add(dot);

            var status = new TextBlock
            {
                Text = tile.StatusText,
                FontSize = 10.5,
                MaxWidth = 104,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(tile.State is ServiceState.Degraded or ServiceState.Offline ? dotBrush : "TextTertiaryBrush"));

            var content = new StackPanel();
            content.Children.Add(icon);
            content.Children.Add(new TextBlock
            {
                Text = tile.Name,
                Margin = new Thickness(0, 7, 0, 0),
                FontSize = 12,
                MaxWidth = 100,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(status);

            var button = new Button { Content = content };
            button.Classes.Add("tile");
            ToolTip.SetTip(button, tile.Url + "\n" + tile.StatusText);
            button.Click += (_, _) => _window.Navigate(service.Url);
            button.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Middle)
                    _window.NewTab(service.Url, select: false);
            };

            var open = new MenuItem { Header = Tr("Ouvrir dans un nouvel onglet") };
            open.Click += (_, _) => _window.NewTab(service.Url, select: true);
            var check = new MenuItem { Header = Tr("Vérifier maintenant") };
            check.Click += async (_, _) =>
            {
                await _app.Monitor.CheckOneAsync(service);
                RefreshServices();
            };
            var edit = new MenuItem { Header = Tr("Modifier…") };
            edit.Click += (_, _) => _ = ServiceDialog.EditAsync(_window, service);
            var delete = new MenuItem { Header = Tr("Supprimer") };
            delete.Click += async (_, _) =>
            {
                if (await Dialogs.Dialogs.ConfirmAsync(_window, Tr("Supprimer le service ?"), Tr("{0} sera retiré de la page d'accueil.", tile.Name), Tr("Supprimer"), destructive: true))
                    _app.Services.Remove(service.Id);
            };
            button.ContextMenu = new ContextMenu { ItemsSource = new Control[] { open, check, new Separator(), edit, delete } };
            return button;
        }

        /// <summary>Pastille ronde : icône du site, ou initiale.</summary>
        Control Badge(string initial, Avalonia.Media.Imaging.Bitmap? favicon)
        {
            var badge = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center };
            badge.Bind(Border.BackgroundProperty, this.GetResourceObservable("SurfaceRaisedBrush"));
            if (favicon != null)
            {
                badge.Child = new Image { Source = favicon, Width = 22, Height = 22 };
            }
            else
            {
                var text = new TextBlock
                {
                    Text = initial,
                    FontSize = 17,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("AccentBrush"));
                badge.Child = text;
            }
            return badge;
        }

        async void CheckServices_Click(object? sender, RoutedEventArgs e)
        {
            await _app.Monitor.CheckAllAsync();
            RefreshServices();
        }

        void AddService_Click(object? sender, RoutedEventArgs e) => _ = ServiceDialog.EditAsync(_window, null);

        // ---------------------------------------------------------------
        // Favoris
        // ---------------------------------------------------------------

        void RefreshFavorites()
        {
            FavoritesTiles.Children.Clear();
            var favorites = _app.Favorites.All.Take(MaxFavorites).ToList();
            FavoritesSection.IsVisible = favorites.Count > 0;

            foreach (FavoriteItem favorite in favorites)
            {
                string title = string.IsNullOrWhiteSpace(favorite.Title) ? favorite.Url : favorite.Title;
                var content = new StackPanel();
                content.Children.Add(Badge(title[..1].ToUpperInvariant(), FaviconStore.TryGet(favorite.Url)));
                content.Children.Add(new TextBlock
                {
                    Text = title,
                    Margin = new Thickness(0, 8, 0, 0),
                    FontSize = 12,
                    MaxWidth = 92,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                var button = new Button { Content = content, Width = 104, Height = 96 };
                button.Classes.Add("tile");
                ToolTip.SetTip(button, UrlDisplay.ForDisplay(favorite.Url));
                string url = favorite.Url;
                button.Click += (_, _) => _window.Navigate(url);
                button.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton == MouseButton.Middle)
                        _window.NewTab(url, select: false);
                };
                FavoritesTiles.Children.Add(button);
            }
        }
    }
}
