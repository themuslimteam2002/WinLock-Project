using System.Runtime.Versioning;
using System.Windows;

namespace AppGuardian.UI;

/// <summary>
/// The dashboard window. Layout and bindings only.
/// </summary>
/// <remarks>
/// No logic here beyond construction. Everything the window shows comes from
/// <see cref="ViewModels.ShellViewModel"/>, which App.xaml.cs assigns as the DataContext — keeping the
/// code-behind empty is what makes the shell's state testable without a window.
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
