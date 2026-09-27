using MyHomelabBrowser.classes;
using System.Windows;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class EditFavoriteDialog : DialogWindow
    {
        public FavoriteItem Favorite { get; }
        public bool Deleted { get; private set; }

        public EditFavoriteDialog(FavoriteItem fav, IEnumerable<string>? existingFolders = null)
        {
            InitializeComponent();

            Favorite = fav;
            TitleBox.Text = fav.Title;
            UrlText.Text = fav.Url;
            UrlText.ToolTip = fav.Url;
            FaviconImage.Source = FaviconStore.TryGet(fav.Url);
            if (FaviconImage.Source == null)
                FaviconImage.Visibility = Visibility.Collapsed;

            FolderBox.ItemsSource = (existingFolders ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            FolderBox.Text = fav.Folder ?? "";

            Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                TitleBox.Focus();
                TitleBox.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void TitleBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => SaveButton.IsEnabled = TitleBox.Text.Trim().Length > 0;

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Favorite.Title = TitleBox.Text.Trim();
            Favorite.Folder = string.IsNullOrWhiteSpace(FolderBox.Text)
                ? null
                : FolderBox.Text.Trim();

            DialogResult = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageDialog.Show(this, Tr("Supprimer ce favori ?"), Tr("Confirmation"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                Deleted = true;
                DialogResult = true;
            }
        }
    }
}
