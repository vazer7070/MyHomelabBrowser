using MyHomelabBrowser.classes;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace MyHomelabBrowser
{
    public partial class SettingsView : UserControl
    {
        readonly SettingsService _service;
        BrowserSettings _original;
        BrowserSettings _working;
        public event Action? OpenHistoryRequested;


        public SettingsView(SettingsService service)
        {
            InitializeComponent();

            _service = service;

            _original = _service.Settings.Clone();
            _working = _service.Settings.Clone();

            DataContext = _working;
        }
        private void OpenHistory_Click(object sender, RoutedEventArgs e)
        {
            OpenHistoryRequested?.Invoke();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _working = _original.Clone();
            DataContext = _working;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _service.Apply(_working.Clone());

            _original = _service.Settings.Clone();
            _working = _service.Settings.Clone();
            DataContext = _working;

            ShowSaveFeedback();
        }

        void ShowSaveFeedback()
        {
            SaveFeedback.Opacity = 1;

            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromSeconds(2),
                BeginTime = TimeSpan.FromSeconds(1)
            };

            SaveFeedback.BeginAnimation(OpacityProperty, anim);
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }
    }
}
