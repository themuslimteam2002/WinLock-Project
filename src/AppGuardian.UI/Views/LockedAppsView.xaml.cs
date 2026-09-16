using System.Runtime.Versioning;
using System.Windows.Controls;

namespace AppGuardian.UI.Views;

/// <summary>Locked apps and active unlocks. No code-behind: everything is bound.</summary>
[SupportedOSPlatform("windows")]
public partial class LockedAppsView : UserControl
{
    public LockedAppsView() => InitializeComponent();
}
