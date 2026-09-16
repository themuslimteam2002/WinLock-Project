using System.Windows;
using System.Windows.Media;

namespace AppGuardian.UI.Views;

/// <summary>
/// Attached brushes for the animated hover, pressed and focus layers in <c>Theme.xaml</c>'s shared
/// control templates.
/// </summary>
/// <remarks>
/// These exist to let one <c>ControlTemplate</c> serve every button in the app. The four button styles
/// differ only in which colours their interaction states use, and before this there were four
/// copy-pasted templates that had already drifted — one of them used a corner radius of 4 while the
/// other three used 6, and the primary button showed the same colour for hover and for pressed, so
/// clicking it appeared to do nothing.
/// <para>
/// An attached property rather than the usual trick of smuggling the brush through
/// <see cref="FrameworkElement.Tag"/>: Tag is untyped, invisible at the point of use, and can only
/// carry one value, whereas a state layer needs a fill and a border and a focus colour. These are also
/// self-documenting in a style — <c>views:Interaction.HoverBrush</c> says what it is.
/// </para>
/// <para>
/// Read from inside a template with a binding to the templated parent rather than with
/// <c>TemplateBinding</c>:
/// </para>
/// <code>
/// Background="{Binding RelativeSource={RelativeSource TemplatedParent},
///                      Path=(views:Interaction.HoverBrush)}"
/// </code>
/// <para>
/// The parenthesised path is required for an attached property, and going through a full
/// <c>Binding</c> keeps the <c>DynamicResource</c> that the style assigned live, so the layer
/// recolours when the user switches theme. A <c>TemplateBinding</c> is a one-time compiled shortcut
/// and does not re-resolve.
/// </para>
/// <para>
/// Every property defaults to null, which renders as no layer at all. That is the correct fallback: a
/// control that forgets to set a hover brush shows no hover state rather than a black rectangle.
/// </para>
/// </remarks>
public static class Interaction
{
    /// <summary>Fill of the layer that fades in while the pointer is over the control.</summary>
    public static readonly DependencyProperty HoverBrushProperty =
        DependencyProperty.RegisterAttached(
            "HoverBrush",
            typeof(Brush),
            typeof(Interaction),
            new FrameworkPropertyMetadata((Brush?)null));

    /// <summary>
    /// Border of the hover layer. Separate from the fill because an outlined button needs its edge to
    /// brighten while its interior stays transparent, and a filled one needs the opposite.
    /// </summary>
    public static readonly DependencyProperty HoverBorderBrushProperty =
        DependencyProperty.RegisterAttached(
            "HoverBorderBrush",
            typeof(Brush),
            typeof(Interaction),
            new FrameworkPropertyMetadata((Brush?)null));

    /// <summary>Fill of the layer that fades in while the control is held down.</summary>
    /// <remarks>
    /// Always darker than the hover brush, in both palettes. Hover lightens and press darkens is the
    /// direction people expect, and the pair reading the same colour is what made the old primary
    /// button feel unresponsive.
    /// </remarks>
    public static readonly DependencyProperty PressedBrushProperty =
        DependencyProperty.RegisterAttached(
            "PressedBrush",
            typeof(Brush),
            typeof(Interaction),
            new FrameworkPropertyMetadata((Brush?)null));

    /// <summary>Border of the ring drawn when the control has keyboard focus.</summary>
    /// <remarks>
    /// Its own property because the focus ring has to contrast with the control's own fill, not with
    /// the page: an accent ring is invisible on a filled accent button, which is why the primary
    /// button's ring is drawn in the text colour instead.
    /// </remarks>
    public static readonly DependencyProperty FocusBrushProperty =
        DependencyProperty.RegisterAttached(
            "FocusBrush",
            typeof(Brush),
            typeof(Interaction),
            new FrameworkPropertyMetadata((Brush?)null));

    public static void SetHoverBrush(DependencyObject element, Brush? value) =>
        element.SetValue(HoverBrushProperty, value);

    public static Brush? GetHoverBrush(DependencyObject element) =>
        (Brush?)element.GetValue(HoverBrushProperty);

    public static void SetHoverBorderBrush(DependencyObject element, Brush? value) =>
        element.SetValue(HoverBorderBrushProperty, value);

    public static Brush? GetHoverBorderBrush(DependencyObject element) =>
        (Brush?)element.GetValue(HoverBorderBrushProperty);

    public static void SetPressedBrush(DependencyObject element, Brush? value) =>
        element.SetValue(PressedBrushProperty, value);

    public static Brush? GetPressedBrush(DependencyObject element) =>
        (Brush?)element.GetValue(PressedBrushProperty);

    public static void SetFocusBrush(DependencyObject element, Brush? value) =>
        element.SetValue(FocusBrushProperty, value);

    public static Brush? GetFocusBrush(DependencyObject element) =>
        (Brush?)element.GetValue(FocusBrushProperty);
}
