using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using AppGuardian.UI.ViewModels;

namespace AppGuardian.UI.Views;

/// <summary>
/// First-run setup view.
/// </summary>
/// <remarks>
/// The two handlers below exist because <see cref="PasswordBox.Password"/> is not a dependency property and
/// cannot be bound. The usual workarounds — an attached behaviour, or a third-party bindable password box —
/// both end up storing the same string in the same view model, so the plain handler is used and the
/// exception to the "no code-behind" rule is confined to this file.
/// <para>
/// The boxes are cleared from here when the view model clears its own copy, so a completed setup does not
/// leave the secret sitting in a control's buffer for the life of the window.
/// </para>
/// <para>
/// Auto-focus of the PIN box when the wizard reaches the security step is also handled here: the view model
/// cannot call Focus without reaching into the visual tree, so the step-current change is mirrored in
/// code-behind instead. FR-300.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();

    private OnboardingViewModel? ViewModel => DataContext as OnboardingViewModel;

    /// <summary>Auto-focuses the PIN entry when the wizard reaches the security step. FR-300.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.StepChanged += OnStepChanged;
            FocusPinBoxIfSecurityStep(vm.Step);
        }
    }

    /// <summary>
    /// Detaches from the view model so a re-hosted view does not leak the handler or fire focus calls
    /// against a dead tree.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.StepChanged -= OnStepChanged;
        }
    }

    /// <summary>
    /// Moves focus to the active PIN box whenever the step becomes the security step, so the user can
    /// type immediately without a click. A PasswordBox already holding text is not re-focused, to avoid
    /// stealing the caret from an in-progress edit.
    /// </summary>
    private void OnStepChanged(object? sender, StepChangedEventArgs e) =>
        FocusPinBoxIfSecurityStep(e.Step);

    private void FocusPinBoxIfSecurityStep(OnboardingStep step)
    {
        if (step != OnboardingStep.Security)
        {
            return;
        }

        // Seed the box that is currently empty — if the user already typed somewhere, leave the caret alone.
        if (SecretBox.Password.Length == 0)
        {
            SecretBox.Focus();
        }
        else if (ConfirmBox.Password.Length == 0)
        {
            ConfirmBox.Focus();
        }
    }

    private void OnSecretChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.Secret = SecretBox.Password;

            SyncClearedBoxes(vm);
        }
    }

    private void OnConfirmChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.Confirm = ConfirmBox.Password;

            SyncClearedBoxes(vm);
        }
    }

    /// <summary>
    /// Mirrors a view-model reset back into the controls.
    /// </summary>
    /// <remarks>
    /// Guarded on length so this cannot recurse: clearing a box raises PasswordChanged again, and without
    /// the check the second pass would write an empty string back over a value the user had just typed.
    /// </remarks>
    private void SyncClearedBoxes(OnboardingViewModel vm)
    {
        if (vm.Secret.Length == 0 && SecretBox.Password.Length > 0)
        {
            SecretBox.Clear();
        }

        if (vm.Confirm.Length == 0 && ConfirmBox.Password.Length > 0)
        {
            ConfirmBox.Clear();
        }
    }
}
