using System.Runtime.Versioning;
using System.Windows.Controls;

namespace AppGuardian.UI.Views;

/// <summary>The local audit log. Read-only, no code-behind.</summary>
[SupportedOSPlatform("windows")]
public partial class ActivityView : UserControl
{
    public ActivityView() => InitializeComponent();
}
