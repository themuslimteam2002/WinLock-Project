using System.Runtime.Versioning;
using System.Windows.Controls;
using AppGuardian.UI.ViewModels;
using Microsoft.Win32;

namespace AppGuardian.UI.Views;

/// <summary>
/// App picker and rule editor view.
/// </summary>
/// <remarks>
/// The file-picker handler is the only code here. A dialog is a view concern — it needs an owner window and
/// it blocks the UI thread — so putting it behind a command would mean the view model either referencing
/// <c>Window</c> or taking a file-dialog abstraction that exists for exactly one call site. The view model
/// still owns the decision about what to do with the path: this method hands it over and nothing more.
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();

    private async void OnBrowseClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not AppsViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose the app's program file",

            // Executables only. The service derives an app identity from the executable path and its hash,
            // and a shortcut or document would resolve to something the process monitor never sees.
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // Resolved through the service rather than parsed here. Deriving the appId in two places would
        // eventually produce two different ids for one app.
        await vm.AddByPathAsync(dialog.FileName).ConfigureAwait(true);
    }
}
