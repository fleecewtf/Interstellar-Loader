using System.Windows;
using System.Windows.Markup;

namespace Interstellar.Loader;

public sealed class MotionDurationExtension : MarkupExtension
{
    internal static bool ReducedMotion { get; set; } = !SystemParameters.ClientAreaAnimation;
    public double Milliseconds { get; set; }

    public MotionDurationExtension(double milliseconds) => Milliseconds = milliseconds;

    public override object ProvideValue(IServiceProvider serviceProvider) => new Duration(TimeSpan.FromMilliseconds(ReducedMotion ? 0 : Milliseconds));
}
