using System.Windows.Controls;

namespace SPTCoffeeModManager.Tabs;

/// <summary>
/// Interaction logic for ModsTab.xaml
/// </summary>
public partial class ModsTab : UserControl
{
    public ModsTab()
    {
        InitializeComponent();
    }

    // Expose controls to parent window for easy access
    public Button RefreshModsButtonRef => RefreshModsButton;
    public Button CheckForModsButtonRef => CheckForModsButton;
}