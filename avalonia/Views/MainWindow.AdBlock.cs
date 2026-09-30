using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using PommeBrowser.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Bouclier de la barre d'outils : état du bloqueur et exception pour le site affiché.</summary>
    public sealed partial class MainWindow
    {
        void InitializeAdBlock()
        {
            App.AdBlock.Changed += UpdateAdBlockButton;
            Closed += (_, _) => App.AdBlock.Changed -= UpdateAdBlockButton;
        }

        void UpdateAdBlockButton()
        {
            AdBlockService blocker = App.AdBlock;
            bool enabled = blocker.Settings.Enabled && AdBlockService.IsSupported;
            bool active = enabled && _selected?.Page == TabPage.Web && blocker.ShouldFilter(_selected.WebUrl);
            AdBlockButton.Opacity = active ? 1 : 0.55;
            ToolTip.SetTip(AdBlockButton, !enabled
                ? Tr("Protection web désactivée")
                : active ? Tr("Protection web active sur ce site") : Tr("Protection web en pause sur ce site"));
        }

        void AdBlock_Click(object? sender, RoutedEventArgs e)
        {
            AdBlockService blocker = App.AdBlock;
            var box = new StackPanel { Width = 320, Spacing = 10, Margin = new Thickness(4) };
            box.Children.Add(new TextBlock { Text = Tr("Protection web"), FontSize = 15, FontWeight = FontWeight.SemiBold });
            var status = new TextBlock { Text = blocker.Status, TextWrapping = TextWrapping.Wrap };
            status.Classes.Add("hint");
            box.Children.Add(status);

            var flyout = new Flyout { Content = box, Placement = PlacementMode.BottomEdgeAlignedRight };

            if (AdBlockService.IsSupported && Uri.TryCreate(_selected?.WebUrl, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0 && blocker.Settings.Enabled)
            {
                bool local = blocker.IsLocal(uri.Host);
                var toggle = new ToggleSwitch
                {
                    Content = Tr("Actif sur {0}", uri.Host),
                    IsChecked = !local && !blocker.IsSiteAllowed(uri.Host),
                    IsEnabled = !local,
                    OnContent = null,
                    OffContent = null
                };
                string host = uri.Host;
                toggle.IsCheckedChanged += (_, _) =>
                {
                    blocker.SetSiteAllowed(host, toggle.IsChecked != true);
                    _selected?.Reload();
                };
                box.Children.Add(toggle);
                if (local)
                {
                    var hint = new TextBlock { Text = Tr("Réseau local : jamais filtré") };
                    hint.Classes.Add("hint");
                    box.Children.Add(hint);
                }
            }

            var update = new Button { Content = Tr("Mettre à jour les listes"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            update.IsEnabled = AdBlockService.IsSupported;
            update.Click += async (_, _) =>
            {
                update.IsEnabled = false;
                var result = await blocker.UpdateListsAsync(force: true);
                ShowToast(result.Message, warning: !result.Success);
                update.IsEnabled = true;
                status.Text = blocker.Status;
            };
            var settings = new Button { Content = Tr("Paramètres"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            settings.Click += (_, _) =>
            {
                flyout.Hide();
                OpenSettings("adblock");
            };
            var buttons = new UniformGrid { Columns = 2 };
            update.Margin = new Thickness(0, 0, 4, 0);
            settings.Margin = new Thickness(4, 0, 0, 0);
            buttons.Children.Add(update);
            buttons.Children.Add(settings);
            box.Children.Add(buttons);

            flyout.ShowAt(AdBlockButton);
        }
    }
}
