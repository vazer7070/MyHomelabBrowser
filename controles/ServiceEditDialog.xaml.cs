using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    public partial class ServiceEditDialog : DialogWindow
    {
        private readonly HomelabService _service;
        private bool _nameEdited;

        public ServiceEditDialog(HomelabService? service, IEnumerable<string> knownGroups, string? suggestedUrl = null, string? suggestedName = null)
        {
            InitializeComponent();

            bool isNew = service == null;
            _service = service ?? new HomelabService();
            Title = isNew ? "Ajouter un service" : "Modifier le service";

            GroupBox.ItemsSource = knownGroups
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            NameBox.Text = isNew ? suggestedName ?? string.Empty : _service.Name;
            UrlBox.Text = isNew ? suggestedUrl ?? string.Empty : _service.Url;
            GroupBox.Text = _service.Group ?? string.Empty;
            MonitorBox.IsChecked = _service.Monitor;

            _nameEdited = !isNew || !string.IsNullOrWhiteSpace(suggestedName);
            NameBox.TextChanged += (_, _) => _nameEdited = NameBox.IsKeyboardFocused || _nameEdited;

            Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                var target = string.IsNullOrWhiteSpace(UrlBox.Text) ? UrlBox : NameBox;
                target.Focus();
                target.SelectAll();
            }, DispatcherPriority.Input);
        }

        public HomelabService Result => _service;

        private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;

            // Nom proposé à partir de l'hôte tant que l'utilisateur n'en a pas saisi un.
            if (_nameEdited)
                return;

            string? url = UrlResolver.TryResolveUrl(UrlBox.Text);
            if (url != null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                string host = uri.Host.Split('.')[0];
                NameBox.Text = host.Length > 0 ? char.ToUpperInvariant(host[0]) + host[1..] : string.Empty;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlResolver.TryResolveUrl(UrlBox.Text) ?? UrlBox.Text.Trim();
            var candidate = new HomelabService
            {
                Id = _service.Id,
                CreatedAt = _service.CreatedAt,
                Name = NameBox.Text.Trim(),
                Url = url,
                Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? null : GroupBox.Text.Trim(),
                Monitor = MonitorBox.IsChecked == true
            };

            string? error = HomelabServiceStore.Validate(candidate);
            if (error != null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                UrlBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(candidate.Name))
                candidate.Name = new Uri(candidate.Url).Host;

            _service.Name = candidate.Name;
            _service.Url = candidate.Url;
            _service.Group = candidate.Group;
            _service.Monitor = candidate.Monitor;
            DialogResult = true;
        }
    }
}
