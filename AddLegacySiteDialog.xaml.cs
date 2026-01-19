using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MyHomelabBrowser
{
    /// <summary>
    /// Logique d'interaction pour AddLegacySiteDialog.xaml
    /// </summary>
    public partial class AddLegacySiteDialog : Window
    {
        public string? ResultDomain { get; private set; }

        public AddLegacySiteDialog()
        {
            InitializeComponent();

            OkBtn.Click += (_, _) =>
            {
                ResultDomain = DomainBox.Text;
                DialogResult = true;
                Close();
            };

            CancelBtn.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };
        }
    }
}
