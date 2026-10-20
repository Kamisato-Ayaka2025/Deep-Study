using System.Globalization;

namespace StudyHarness.Domain;

public static class WeeklyStudyTime
{
    // UI accepts decimal hours that map exactly to whole minutes. Never round input.
    public static bool TryMinutes(decimal hours, out int minutes)
    {
        minutes = 0;
        if (hours <= 0 || hours > 168) return false;
        var exact = hours * 60;
        if (decimal.Truncate(exact) != exact) return false;
        minutes = (int)exact;
        return true;
    }

    // Legacy one-minute values cannot be represented exactly by finite decimal hours.
    public static string Format(int minutes)
    {
        if (minutes <= 0 || minutes > 10080) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (minutes % 3 == 0) return (minutes / 60m).ToString("0.##", CultureInfo.InvariantCulture) + " 小时";
        return $"{minutes / 60} 小时 {minutes % 60} 分钟（原值 {minutes} 分钟）";
    }
}
