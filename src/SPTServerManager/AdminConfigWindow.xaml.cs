using System.Windows;

namespace SPTServerManager;

public partial class AdminConfigWindow : Window
{
    public AdminConfigWindow()
    {
        InitializeComponent();
    }

    public string NoteText
    {
        get => NoteTextBlock.Text;
        set => NoteTextBlock.Text = value;
    }

    public string Secret
    {
        get => SecretTextBox.Text;
        set => SecretTextBox.Text = value;
    }

    public bool IsAdminEnabled
    {
        get => IsEnabledCheckBox.IsChecked ?? false;
        set => IsEnabledCheckBox.IsChecked = value;
    }

    public bool AllowHeadlessClose
    {
        get => AllowHeadlessCloseCheckBox.IsChecked ?? false;
        set => AllowHeadlessCloseCheckBox.IsChecked = value;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }

    private void GenerateSecretButton_Click(object sender, RoutedEventArgs e)
    {
        var secret = Guid.NewGuid().ToString("N");
        SecretTextBox.Text = secret;
    }

    private void ToggleSecretVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        SecretTextBox.Visibility = SecretTextBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }
}