using System;
using System.Windows;
using MyHomelabBrowser.classes.Flash;

namespace MyHomelabBrowser
{
    public partial class FlashConsoleWindow : Window
    {
        public FlashConsoleWindow()
        {
            InitializeComponent();

            // afficher les lignes déjà en mémoire
            foreach (var line in FlashDebugConsole.Lines)
                ConsoleBox.AppendText(line + Environment.NewLine);

            FlashDebugConsole.LineAdded += OnLineAdded;

            ClearBtn.Click += (_, _) =>
            {
                FlashDebugConsole.Clear();
                ConsoleBox.Clear();
            };

            Closed += (_, _) =>
            {
                FlashDebugConsole.LineAdded -= OnLineAdded;
            };
        }

        private void OnLineAdded(string line)
        {
            Dispatcher.Invoke(() =>
            {
                ConsoleBox.AppendText(line + Environment.NewLine);
                ConsoleBox.ScrollToEnd();
            });
        }
    }
}
