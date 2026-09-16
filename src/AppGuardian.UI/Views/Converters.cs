using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace AppGuardian.UI.Views;

/// <summary>
/// Inverts a boolean. Used where a control is enabled precisely when a flag is false.
/// </summary>
/// <remarks>
/// Present because the alternative — an <c>IsNotBusy</c> property beside every <c>IsBusy</c> — doubles the
/// notification surface of every view model for no gain.
/// <para>
/// Every converter in this file declares its parameters nullable, matching the annotations on
/// <see cref="IValueConverter"/> itself. The build treats nullability warnings as errors, and a
/// non-nullable <c>object value</c> here is a signature mismatch rather than a stylistic choice.
/// </para>
/// </remarks>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;
}

/// <summary>Collapses an element when the bound boolean is false.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>Set to invert, for the common "show this when the flag is false" case.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;

        if (Invert)
        {
            flag = !flag;
        }

        // Collapsed rather than Hidden: a hidden element still occupies its slot, which leaves a gap where
        // a status banner used to be.
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when the bound string is null or blank.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    /// <summary>Set to invert, for "show this only when there is nothing to say".</summary>
    /// <remarks>
    /// A property rather than a converter parameter, so the inversion is fixed where the resource is
    /// declared. A parameter would let two bindings share one key and disagree about what it means.
    /// </remarks>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);

        if (Invert)
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns a base64 PNG from <c>apps.getIcon</c> into an image source.
/// </summary>
/// <remarks>
/// Returns null rather than throwing on malformed data. An icon comes from a third-party executable's
/// resources, so a corrupt or unexpected payload is a realistic input, and a binding exception over a
/// decorative element would take out the whole row.
/// <para>
/// <c>CacheOption = OnLoad</c> matters: without it the decoder keeps the stream open, and the stream here
/// is a temporary that is disposed the moment this method returns.
/// </para>
/// </remarks>
public sealed class Base64ImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string data || string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            var bytes = System.Convert.FromBase64String(data);

            using var stream = new MemoryStream(bytes);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();

            // Frozen so it can be handed to the UI thread from anywhere and shared between rows without
            // a per-use copy.
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Compares a bound value against the parameter, for radio-style selection of an enum.
/// </summary>
/// <remarks>
/// The one-way direction returns a boolean; the reverse returns the parameter when the control is checked
/// and <see cref="Binding.DoNothing"/> when it is cleared. Returning the default enum value on uncheck
/// would make selecting one radio button write twice — once for the button being cleared and once for the
/// one being set — and the losing write would sometimes land last.
/// </remarks>
public sealed class EnumEqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is not null)
        {
            return parameter;
        }

        return Binding.DoNothing;
    }
}

/// <summary>
/// Radio-style selection over a <c>PowerProfile?</c>, where "no rule at all" is null.
/// </summary>
/// <remarks>
/// A separate converter from <see cref="EnumEqualityConverter"/> because the absence of a power rule is a
/// distinct state from <c>PowerProfile.Balanced</c> — the contract uses a null profile to mean "no rule" and
/// Balanced to mean "a recorded decision to leave this app alone" — and a converter parameter is a string,
/// which cannot express null. The sentinel below is the one place that mapping lives.
/// <para>
/// The sentinel is deliberately <c>NoRule</c> and not <c>None</c>. <c>PowerProfile</c> has no member called
/// <c>None</c> today, so <c>None</c> worked, but it reads exactly like an enum member name and the next
/// person to add one would create a silent collision: the new member would be swallowed by the null branch
/// and the radio button for it would never select. <c>NoRule</c> cannot be mistaken for a member, and
/// <see cref="ConvertBack"/> below rejects any parameter that is neither the sentinel nor a real member
/// rather than falling through to null.
/// </para>
/// </remarks>
public sealed class NullableEnumEqualityConverter : IValueConverter
{
    /// <summary>The parameter value that stands for null. Must not match any enum member name.</summary>
    public const string NoneSentinel = "NoRule";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parameterText = parameter?.ToString();

        if (string.Equals(parameterText, NoneSentinel, StringComparison.Ordinal))
        {
            return value is null;
        }

        return Equals(value?.ToString(), parameterText);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // DoNothing on uncheck, for the same reason as EnumEqualityConverter: selecting one button in a
        // group raises both a clear and a set, and only the set carries the user's intent.
        if (value is not bool isChecked || !isChecked)
        {
            return Binding.DoNothing;
        }

        var parameterText = parameter?.ToString();

        if (string.Equals(parameterText, NoneSentinel, StringComparison.Ordinal))
        {
            return null!;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // A parameter that is neither the sentinel nor a real member is a typo in the view. DoNothing, so
        // the binding leaves the value alone instead of writing null — which would look like the user had
        // chosen "no rule" and would quietly remove their limit.
        return parameterText is not null && Enum.TryParse(enumType, parameterText, out var parsed)
            ? parsed!
            : Binding.DoNothing;
    }
}

/// <summary>Formats a nullable value with a fallback, so an empty field never shows as blank.</summary>
public sealed class FallbackTextConverter : IValueConverter
{
    public string Fallback { get; set; } = "Unknown";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();

        return string.IsNullOrWhiteSpace(text) ? Fallback : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when a bound integer has reached the threshold in the converter parameter.
/// </summary>
/// <remarks>
/// This is what lights the two segmented meters in the app: the onboarding progress rail, where the
/// threshold is a step number, and the PIN strength meter, where it is a score out of four. Segment
/// four asks "is the value at least 4", and the answer drives that one segment's fill.
/// <para>
/// Segments rather than a single proportional bar, because a proportional bar needs its width expressed
/// as a fraction of its parent — which in WPF means either a converter that returns a
/// <see cref="System.Windows.GridLength"/> or a binding to ActualWidth, and both make the meter
/// depend on layout having already run. Four fixed segments have no such dependency, and a discrete
/// count is the honest way to present a score that is itself discrete.
/// </para>
/// <para>
/// The threshold is a converter parameter here, unlike the <c>Invert</c> properties elsewhere in this
/// file which are deliberately fixed at the point the resource is declared. The reasoning that applies
/// there does not apply here: <c>Invert</c> changes what a shared converter key *means*, so two
/// bindings sharing one key could disagree about it, whereas the threshold is simply which segment is
/// asking. One converter instance genuinely serves all eight segments.
/// </para>
/// <para>
/// Returns a <see cref="Visibility"/> when the binding target is one, and a boolean otherwise. This is
/// what lets a lit segment be expressed as a single attribute in the view —
/// <c>Visibility="{Binding StrengthScore, Converter={StaticResource IntAtLeast}, ConverterParameter=2}"</c>
/// — rather than needing a second converter chained behind this one, which WPF has no syntax for. WPF
/// passes the real type of the target property as <paramref name="targetType"/>, and a
/// <see cref="System.Windows.DataTrigger"/> condition passes <c>object</c>, so the boolean branch still
/// serves trigger comparisons against <c>Value="True"</c>.
/// </para>
/// <para>
/// Returns false for anything unparseable rather than throwing. An unlit segment is a visual detail; a
/// binding exception during onboarding would take out the first screen the user ever sees.
/// </para>
/// </remarks>
public sealed class IntAtLeastConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Invariant, not the current culture: the threshold is authored in XAML, not entered by a user.
        var reached = value is int actual
            && int.TryParse(
                parameter?.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var threshold)
            && actual >= threshold;

        if (targetType == typeof(Visibility))
        {
            return reached ? Visibility.Visible : Visibility.Collapsed;
        }

        return reached;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
