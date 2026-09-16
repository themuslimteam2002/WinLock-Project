using System.Runtime.Versioning;
using System.Windows.Controls;

namespace AppGuardian.UI.Views;

/// <summary>Apps set to be hidden, and the way to bring them back. No code-behind.</summary>
[SupportedOSPlatform("windows")]
public partial class HiddenAppsView : UserControl
{
    public HiddenAppsView() => InitializeComponent();
}
