using System.Globalization;

namespace OsmoActionViewer.Utils;

public static class TimeFormat
{
    public static string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "00:00";
        var total = (int)seconds;
        var h = total / 3600;
        var m = (total % 3600) / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    public static string FormatInvariant(double value, string fmt = "F1")
        => value.ToString(fmt, CultureInfo.InvariantCulture);
}
