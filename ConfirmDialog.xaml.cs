using System.Windows;

namespace AskaServerManager
{
    public partial class ConfirmDialog : Window
    {
        public bool Result { get; private set; }
        public ConfirmDialog(string message)
        {
            InitializeComponent();
            NativeMethods.SetDarkTitleBar(this);
            MessageText.Text = message;
            Owner = System.Windows.Application.Current.MainWindow;
        }
        private void BtnYes_Click(object sender, RoutedEventArgs e) { Result = true; Close(); }
        private void BtnNo_Click(object sender, RoutedEventArgs e) { Result = false; Close(); }
    }
}