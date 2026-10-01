using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace Broiler.Hosting.Android;

/// <summary>Keeps app content outside status, navigation, and display-cutout regions.</summary>
public sealed class AndroidInsetLayout : FrameLayout
{
    public AndroidInsetLayout(Context context)
        : base(context)
    {
    }

    public override WindowInsets? OnApplyWindowInsets(WindowInsets? insets)
    {
        if (insets is null)
            return null;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            global::Android.Graphics.Insets bars = insets.GetInsets(
                WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
        }
        else
        {
#pragma warning disable CS0618, CA1422
            SetPadding(
                insets.SystemWindowInsetLeft,
                insets.SystemWindowInsetTop,
                insets.SystemWindowInsetRight,
                insets.SystemWindowInsetBottom);
#pragma warning restore CS0618, CA1422
        }

        return insets;
    }
}
