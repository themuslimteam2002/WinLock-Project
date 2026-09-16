using System.Runtime.Versioning;
using System.Windows.Controls;
using AppGuardian.UI.ViewModels;

namespace AppGuardian.UI.Views;

/// <summary>
/// Settings view.
/// </summary>
/// <remarks>
/// The two handlers exist only because <see cref="PasswordBox.Password"/> is not bindable — the same
/// limitation as on the onboarding view, handled the same way so there is one pattern rather than two. The
/// view model still owns validation and the call to the service.
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnNewSecretChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.NewSecret = NewSecretBox.Password;

            SyncClearedBoxes(vm);
        }
    }

    private void OnConfirmSecretChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.ConfirmSecret = ConfirmSecretBox.Password;

            SyncClearedBoxes(vm);
        }
    }

    /// <summary>
    /// Clears the boxes once the view model has cleared its own copy, which it does after every change
    /// attempt, successful or not.
    /// </summary>
    /// <remarks>
    /// Guarded on length so clearing a box cannot recurse through PasswordChanged and overwrite a value the
    /// user has just started typing.
    /// </remarks>
    private void SyncClearedBoxes(SettingsViewModel vm)
    {
        if (vm.NewSecret.Length == 0 && NewSecretBox.Password.Length > 0)
        {
            NewSecretBox.Clear();
        }

        if (vm.ConfirmSecret.Length == 0 && ConfirmSecretBox.Password.Length > 0)
        {
            ConfirmSecretBox.Clear();
        }
    }
}
