using System.Runtime.Versioning;
using System.Windows.Controls;

namespace AppGuardian.UI.Views;

/// <summary>CPU and battery limits, and whether they are in force. No code-behind.</summary>
[SupportedOSPlatform("windows")]
public partial class PowerView : UserControl
{
    public PowerView() => InitializeComponent();
}
