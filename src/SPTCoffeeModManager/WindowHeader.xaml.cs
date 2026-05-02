using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SPTCoffeeModManager;

public partial class WindowHeader : UserControl
{
    public WindowHeader()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty HeaderTextProperty =
        DependencyProperty.Register(
            nameof(HeaderText),
            typeof(string),
            typeof(WindowHeader),
            new PropertyMetadata("SPT Coffee Mod Manager"));

    public string HeaderText
    {
        get => (string)GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            Window? parent = Window.GetWindow(this);
            parent?.DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Window? parent = Window.GetWindow(this);
        if (parent != null)
            parent.WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Window? parent = Window.GetWindow(this);
        parent?.Close();
    }
}