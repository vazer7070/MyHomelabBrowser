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

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Logique d'interaction pour DetachedCustomWindow.xaml
    /// </summary>
    public partial class DetachedCustomWindow : Window
    {
        public event Action<UserControl>? RequestRedock;


        public DetachedCustomWindow()
        {
            InitializeComponent();


        }
        void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                DragMove();
            }
            catch { }
        }

        public void RequestRedockAndClose()
        {
            if (Root.Children.Count > 0 &&
                Root.Children[0] is UserControl view)
            {
                RequestRedock?.Invoke(view);
            }

            Close();
        }

        void Redock_Click(object sender, RoutedEventArgs e)
        {
            RequestRedockAndClose();
        }

        public void SetContent(UserControl view)
        {
            Root.Children.Clear();

            view.HorizontalAlignment = HorizontalAlignment.Stretch;
            view.VerticalAlignment = VerticalAlignment.Stretch;

            Root.Children.Add(view);
        }


      

    }

}
