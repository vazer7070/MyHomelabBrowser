using MyHomelabBrowser.classes;
using System.Windows;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class EditFavoriteDialog : Window
    {
        public FavoriteItem Favorite { get; }
        public bool Deleted { get; private set; }

        public EditFavoriteDialog(FavoriteItem fav)
        {
            InitializeComponent();

            Favorite = fav;
            TitleBox.Text = fav.Title;
            FolderBox.Text = fav.Folder ?? "";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Favorite.Title = TitleBox.Text.Trim();
            Favorite.Folder = string.IsNullOrWhiteSpace(FolderBox.Text)
                ? null
                : FolderBox.Text.Trim();

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Tr("Supprimer ce favori ?"), Tr("Confirmation"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                Deleted = true;
                DialogResult = true;
            }
        }
    }
}
