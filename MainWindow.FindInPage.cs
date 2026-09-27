using Microsoft.Web.WebView2.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Recherche dans la page (Ctrl+F)
        // ---------------------------
        private CoreWebView2? _findCore;
        private DispatcherTimer? _findDebounce;
        private bool _findBarOpen;

        private void InitializeFindBar()
        {
            FindPopup.CustomPopupPlacementCallback = PlaceFindPopup;
            LocationChanged += (_, _) => RepositionFindPopup();
            SizeChanged += (_, _) => RepositionFindPopup();

            // Un Popup reste au premier plan, même devant les autres applications.
            Activated += (_, _) =>
            {
                if (_findBarOpen)
                    FindPopup.IsOpen = true;
            };

            Tabs.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, Tabs))
                    CloseFindBar(focusPage: false);
            };
        }

        private CustomPopupPlacement[] PlaceFindPopup(Size popupSize, Size targetSize, Point offset)
            => new[] { new CustomPopupPlacement(new Point(Math.Max(0, targetSize.Width - popupSize.Width), 0), PopupPrimaryAxis.None) };

        private void RepositionFindPopup()
        {
            if (!FindPopup.IsOpen)
                return;

            FindPopup.HorizontalOffset += 0.01;
            FindPopup.HorizontalOffset -= 0.01;
        }

        private CoreWebView2? GetActiveCore()
            => Tabs.SelectedItem is TabItem { Tag: WebTabContent { IsLegacyExternal: false, IsCustomView: false, Web.CoreWebView2: CoreWebView2 core } }
                ? core
                : null;

        void OpenFindBar()
        {
            if (GetActiveCore() == null)
                return;

            _findBarOpen = true;
            FindPopup.IsOpen = true;
            RepositionFindPopup();

            Dispatcher.BeginInvoke(() =>
            {
                FindBox.Focus();
                Keyboard.Focus(FindBox);
                FindBox.SelectAll();
            }, DispatcherPriority.Input);

            if (FindBox.Text.Length > 0)
                _ = StartFindAsync();
        }

        void CloseFindBar(bool focusPage = true)
        {
            _findDebounce?.Stop();

            if (_findCore != null)
            {
                try
                {
                    _findCore.Find.Stop();
                }
                catch
                {
                    // Onglet fermé entre-temps.
                }

                DetachFind();
            }

            bool wasOpen = _findBarOpen;
            _findBarOpen = false;
            FindPopup.IsOpen = false;
            FindCountText.Text = string.Empty;

            if (wasOpen && focusPage)
                FocusCurrentPage();
        }

        private void DetachFind()
        {
            if (_findCore == null)
                return;

            try
            {
                _findCore.Find.MatchCountChanged -= OnFindResultsChanged;
                _findCore.Find.ActiveMatchIndexChanged -= OnFindResultsChanged;
            }
            catch
            {
            }

            _findCore = null;
        }

        private async Task StartFindAsync()
        {
            CoreWebView2? core = GetActiveCore();
            if (core == null)
            {
                CloseFindBar(focusPage: false);
                return;
            }

            if (!ReferenceEquals(core, _findCore))
            {
                DetachFind();
                _findCore = core;
                core.Find.MatchCountChanged += OnFindResultsChanged;
                core.Find.ActiveMatchIndexChanged += OnFindResultsChanged;
            }

            string term = FindBox.Text;
            if (term.Length == 0)
            {
                core.Find.Stop();
                UpdateFindCount();
                return;
            }

            CoreWebView2FindOptions options = core.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.IsCaseSensitive = FindCaseToggle.IsChecked == true;
            options.ShouldHighlightAllMatches = true;
            options.ShouldMatchWord = false;
            options.SuppressDefaultFindDialog = true;

            try
            {
                await core.Find.StartAsync(options);
            }
            catch
            {
                // Page en cours de navigation : la recherche sera relancée à la prochaine frappe.
            }

            UpdateFindCount();
        }

        private void OnFindResultsChanged(object? sender, object e)
            => Dispatcher.BeginInvoke(UpdateFindCount);

        private void UpdateFindCount()
        {
            if (_findCore == null || FindBox.Text.Length == 0)
            {
                FindCountText.Text = string.Empty;
                return;
            }

            int count;
            int index;
            try
            {
                count = _findCore.Find.MatchCount;
                index = _findCore.Find.ActiveMatchIndex;
            }
            catch
            {
                return;
            }

            FindCountText.Text = count <= 0 ? Tr("Aucun résultat") : Tr("{0} sur {1}", Math.Max(index, 1), count);
            FindCountText.SetResourceReference(TextBlock.ForegroundProperty, count <= 0 ? "DangerBrush" : "TextSecondaryBrush");
        }

        private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_findBarOpen)
                return;

            if (_findDebounce == null)
            {
                _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _findDebounce.Tick += async (_, _) =>
                {
                    _findDebounce.Stop();
                    await StartFindAsync();
                };
            }

            _findDebounce.Stop();
            _findDebounce.Start();
        }

        private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    FindStep(forward: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    e.Handled = true;
                    break;
                case Key.Escape:
                    CloseFindBar();
                    e.Handled = true;
                    break;
            }
        }

        void FindStep(bool forward)
        {
            if (!_findBarOpen)
            {
                OpenFindBar();
                return;
            }

            // Terme modifié ou recherche jamais lancée : on démarre une nouvelle session.
            if (_findCore == null || !ReferenceEquals(_findCore, GetActiveCore()) || (_findDebounce?.IsEnabled ?? false))
            {
                _findDebounce?.Stop();
                _ = StartFindAsync();
                return;
            }

            try
            {
                if (forward)
                    _findCore.Find.FindNext();
                else
                    _findCore.Find.FindPrevious();
            }
            catch
            {
                _ = StartFindAsync();
            }
        }

        private void FindOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (_findBarOpen)
                _ = StartFindAsync();
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(forward: true);

        private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindStep(forward: false);

        private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

        private void MainMenu_Find_Click(object sender, RoutedEventArgs e) => OpenFindBar();
    }
}
