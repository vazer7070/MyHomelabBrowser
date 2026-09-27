using System;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles
{
    public partial class ClearBrowsingDataDialog : DialogWindow
    {
        public ClearBrowsingDataDialog()
        {
            InitializeComponent();
            HistoryBox.Checked += (_, _) => UpdateState();
            HistoryBox.Unchecked += (_, _) => UpdateState();
            CookiesBox.Checked += (_, _) => UpdateState();
            CookiesBox.Unchecked += (_, _) => UpdateState();
            CacheBox.Checked += (_, _) => UpdateState();
            CacheBox.Unchecked += (_, _) => UpdateState();
            DownloadsBox.Checked += (_, _) => UpdateState();
            DownloadsBox.Unchecked += (_, _) => UpdateState();
            AutofillBox.Checked += (_, _) => UpdateState();
            AutofillBox.Unchecked += (_, _) => UpdateState();
        }

        /// <summary>
        /// Début de la période à effacer (DateTime.MinValue pour « depuis toujours »).
        /// </summary>
        public DateTime Since { get; private set; }

        public bool ClearHistory => HistoryBox.IsChecked == true;
        public bool ClearCookies => CookiesBox.IsChecked == true;
        public bool ClearCache => CacheBox.IsChecked == true;
        public bool ClearDownloads => DownloadsBox.IsChecked == true;
        public bool ClearAutofill => AutofillBox.IsChecked == true;

        private void UpdateState()
        {
            ClearButton.IsEnabled = ClearHistory || ClearCookies || ClearCache || ClearDownloads || ClearAutofill;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            int hours = RangeBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int value) ? value : 24;
            Since = hours <= 0 ? DateTime.MinValue : DateTime.Now.AddHours(-hours);
            DialogResult = true;
        }
    }
}
