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
    public partial class SimplePasswordDialog : Window
    {
        public string Password => PasswordInput.Password;

        public SimplePasswordDialog(string title)
        {
            InitializeComponent();
            TitleText.Text = title;
        }
        public Func<string, bool>? ValidatePassword { get; set; }

        private int _failedAttempts = 0;

        void Ok_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;

            var password = PasswordInput.Password;

            if (ValidatePassword != null)
            {
                if (!ValidatePassword(password))
                {
                    _failedAttempts++;

                    ErrorText.Text = _failedAttempts >= 3
                        ? "Trop de tentatives. Veuillez patienter."
                        : "Mot de passe incorrect";

                    ErrorText.Visibility = Visibility.Visible;

                    PasswordInput.Clear();
                    PasswordInput.Focus();
                    return;
                }
            }

            DialogResult = true;
        }

        
    }
}
