using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AppGuardian.UI.Controls;

/// <summary>
/// The illustrated empty state: a tinted icon badge, a headline, one sentence of explanation, and a
/// single call to action.
/// </summary>
/// <remarks>
/// Replaces the four bare sentences that stood in for this — one each in the apps, locked, hidden and
/// power views — which said only that a list was empty and gave the user nothing to do about it. An
/// empty list is the first thing a new user sees on four of the six pages, so it is the app's real
/// first impression and the highest-leverage place to put a next step.
/// <para>
/// A <see cref="UserControl"/> rather than a <see cref="ContentControl"/> style in <c>Theme.xaml</c>,
/// because this has five independent pieces of content. Expressing that as a style would mean either
/// five attached properties or a hand-assembled <see cref="ContentControl.Content"/> at every use site,
/// and both are worse than one control with five properties.
/// </para>
/// <para>
/// Every property is a dependency property so that each one can be bound. The headline and body of the
/// hidden-apps view differ depending on whether the user has hidden anything yet or has simply not run
/// the agent, and that distinction lives in a view model.
/// </para>
/// <para>
/// The properties are read inside <c>EmptyState.xaml</c> through <c>ElementName=Root</c> and not through
/// a plain <c>{Binding Headline}</c>. A UserControl inherits its DataContext from wherever it is placed,
/// so a plain binding inside this file would resolve against the page's view model — silently finding
/// nothing, or worse, finding a same-named property that means something else.
/// </para>
/// </remarks>
public partial class EmptyState : UserControl
{
    /// <summary>The glyph, taken from <c>Icons.xaml</c>. Drawn at 24 inside a 56px tinted badge.</summary>
    /// <remarks>
    /// A <see cref="Geometry"/> rather than an image or a font glyph, for the reason Icons.xaml gives:
    /// geometry recolours with the palette and needs no per-DPI asset. Null collapses the badge rather
    /// than leaving an empty tinted circle, which reads as a failed image load.
    /// </remarks>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(Geometry),
            typeof(EmptyState),
            new FrameworkPropertyMetadata((Geometry?)null));

    /// <summary>The one-line statement of what is missing. Sentence case, no full stop.</summary>
    public static readonly DependencyProperty HeadlineProperty =
        DependencyProperty.Register(
            nameof(Headline),
            typeof(string),
            typeof(EmptyState),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>One sentence saying what the user can do about it. Wraps at 320px.</summary>
    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(
            nameof(Body),
            typeof(string),
            typeof(EmptyState),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>
    /// The label on the call-to-action button. Empty collapses the button, for the empty states that
    /// genuinely have no action — a filtered list with no matches is fixed by clearing the filter, not
    /// by a button.
    /// </summary>
    public static readonly DependencyProperty ActionTextProperty =
        DependencyProperty.Register(
            nameof(ActionText),
            typeof(string),
            typeof(EmptyState),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>The command the call-to-action runs.</summary>
    /// <remarks>
    /// Separate from <see cref="ActionText"/> rather than inferred from it: the button's visibility
    /// follows the text, so a state can offer a label with no command while a command is still loading,
    /// and the button disables itself through <see cref="ICommand.CanExecute"/> instead of vanishing.
    /// A control that appears and disappears as data arrives is the more disorienting of the two.
    /// </remarks>
    public static readonly DependencyProperty ActionCommandProperty =
        DependencyProperty.Register(
            nameof(ActionCommand),
            typeof(ICommand),
            typeof(EmptyState),
            new FrameworkPropertyMetadata((ICommand?)null));

    /// <summary>Optional parameter passed to <see cref="ActionCommand"/>.</summary>
    public static readonly DependencyProperty ActionCommandParameterProperty =
        DependencyProperty.Register(
            nameof(ActionCommandParameter),
            typeof(object),
            typeof(EmptyState),
            new FrameworkPropertyMetadata((object?)null));

    public EmptyState()
    {
        InitializeComponent();
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Headline
    {
        get => (string)GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }

    public string Body
    {
        get => (string)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public object? ActionCommandParameter
    {
        get => GetValue(ActionCommandParameterProperty);
        set => SetValue(ActionCommandParameterProperty, value);
    }
}
