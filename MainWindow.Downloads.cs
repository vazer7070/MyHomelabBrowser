using MyHomelabBrowser.classes;
using System.Windows;
using System.Windows.Input;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Actions de la liste des téléchargements.

        private void DownloadItem_Open(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.OpenFile(it);
        }

        private void DownloadItem_Remove(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Remove(it);
            UpdateDownloadsBadge();
        }

        private void DownloadItem_OpenFolder(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.OpenContainingFolder(it);
        }

        private void DownloadItem_Pause(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Pause(it);
        }

        private void DownloadItem_ResumeOrRetry(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Retry(it);
        }

        private void DownloadItem_Cancel(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Cancel(it);
        }
    }
}
