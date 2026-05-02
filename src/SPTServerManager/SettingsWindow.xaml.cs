using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace SPTServerManager;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }


    public string ModsPath
    {
        get => ModsPathTextBox.Text;
        set => ModsPathTextBox.Text = value;
    }

    public string LauncherPort
    {
        get => LauncherPortTextBox.Text;
        set => LauncherPortTextBox.Text = value;
    }

    public string ServerFolderPath
    {
        get => ServerFolderPathTextBox.Text;
        set => ServerFolderPathTextBox.Text = value;
    }

    public string AdditionalModsPath
    {
        get => AdditionalModsPathTextBox.Text;
        set => AdditionalModsPathTextBox.Text = value;
    }

    private void BrowseModsPath_Click(object sender, RoutedEventArgs e)
    {
        using var fbd = new FolderBrowserDialog();
        fbd.Description = "Select BepInEx Plugins Folder";
        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ModsPathTextBox.Text = fbd.SelectedPath;
        }
    }

    private void OpenModsPath_Click(object sender, RoutedEventArgs e)
    {
        var modsPath = ModsPathTextBox.Text;

        if (Directory.Exists(modsPath))
        {
            Process.Start("explorer.exe", modsPath);
        }
        else
        {
            System.Windows.MessageBox.Show("Mods path does not exist!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }

    private void BrowseServerFolderPath_Click(object sender, RoutedEventArgs e)
    {
        using var fbd = new FolderBrowserDialog();
        fbd.Description = "Select SPT Server Folder";
        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ServerFolderPathTextBox.Text = fbd.SelectedPath;
        }
    }

    private void OpenServerFolderPath_Click(object sender, RoutedEventArgs e)
    {
        var serverPath = ServerFolderPathTextBox.Text;

        if (Directory.Exists(serverPath))
        {
            Process.Start("explorer.exe", serverPath);
        }
        else
        {
            System.Windows.MessageBox.Show("Server folder path does not exist!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseAdditionalModsPath_Click(object sender, RoutedEventArgs e)
    {
        using var fbd = new FolderBrowserDialog();
        fbd.Description = "Select Additional Mods Folder";
        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            AdditionalModsPathTextBox.Text = fbd.SelectedPath;
        }
    }

    private void OpenAdditionalModsPath_Click(object sender, RoutedEventArgs e)
    {
        var modsPath = AdditionalModsPathTextBox.Text;

        if (Directory.Exists(modsPath))
        {
            Process.Start("explorer.exe", modsPath);
        }
        else
        {
            System.Windows.MessageBox.Show("Additional mods path does not exist!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}