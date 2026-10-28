using System;
using System.Globalization;

namespace EmbyBackup.Core
{
    public static class BackupSchedulePeriods
    {
        public const string Daily = "Daily";
        public const string AlternateDays = "AlternateDays";
        public const string Weekly = "Weekly";
        public const string Monthly = "Monthly";

        public static string Normalize(string value)
        {
            if (string.Equals(value, AlternateDays, StringComparison.OrdinalIgnoreCase))
            {
                return AlternateDays;
            }

            if (string.Equals(value, Weekly, StringComparison.OrdinalIgnoreCase))
            {
                return Weekly;
            }

            if (string.Equals(value, Monthly, StringComparison.OrdinalIgnoreCase))
            {
                return Monthly;
            }

            return Daily;
        }
    }

    /// <summary>
    /// Pure scheduling rules shared by the settings page and the scheduled task. Times are local to the
    /// server, because Emby's own triggers also use the server's local clock.
    /// </summary>
    public static class BackupScheduleRules
    {
        public static readonly TimeSpan DefaultTime = TimeSpan.FromHours(3);

        public static readonly string DefaultDayOfWeek = DayOfWeek.Monday.ToString();

        public static string NormalizeTime(string value)
        {
            TimeSpan time;
            return TryParseTime(value, out time) ? FormatTime(time) : FormatTime(DefaultTime);
        }

        public static bool TryParseTime(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out time))
            {
                return false;
            }

            return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        }

        public static string FormatTime(TimeSpan time)
        {
            return time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }

        public static string NormalizeDayOfWeek(string value)
        {
            DayOfWeek day;
            return TryParseDayOfWeek(value, out day) ? day.ToString() : DefaultDayOfWeek;
        }

        public static bool TryParseDayOfWeek(string value, out DayOfWeek day)
        {
            day = DayOfWeek.Monday;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return Enum.TryParse(value.Trim(), true, out day) && Enum.IsDefined(typeof(DayOfWeek), day);
        }

        /// <summary>
        /// Decides whether the scheduled task should back up now. The daily/weekly trigger already fires on
        /// the configured day and time; this adds the period that Emby's triggers cannot express and blocks
        /// a second automatic backup on the same day.
        /// </summary>
        public static bool IsDue(string period, DateTime nowLocal, DateTime? lastSuccess)
        {
            if (lastSuccess == null || lastSuccess.Value == DateTime.MinValue)
            {
                return true;
            }

            var lastLocal = ToLocal(lastSuccess.Value);
            if (lastLocal == DateTime.MinValue)
            {
                return true;
            }

            switch (BackupSchedulePeriods.Normalize(period))
            {
                case BackupSchedulePeriods.AlternateDays:
                    return (nowLocal.Date - lastLocal.Date).Days >= 2;
                case BackupSchedulePeriods.Monthly:
                    return lastLocal.Year != nowLocal.Year || lastLocal.Month != nowLocal.Month;
                default:
                    return nowLocal.Date > lastLocal.Date;
            }
        }

        /// <summary>Lower-case sentence fragment, so callers can put it after "Automatic backups run".</summary>
        public static string Describe(string period, string time, string dayOfWeek)
        {
            var normalizedTime = NormalizeTime(time);
            switch (BackupSchedulePeriods.Normalize(period))
            {
                case BackupSchedulePeriods.AlternateDays:
                    return "every other day at " + normalizedTime;
                case BackupSchedulePeriods.Weekly:
                    return "every " + NormalizeDayOfWeek(dayOfWeek) + " at " + normalizedTime;
                case BackupSchedulePeriods.Monthly:
                    return "on the first day of each month at " + normalizedTime;
                default:
                    return "every day at " + normalizedTime;
            }
        }

        private static DateTime ToLocal(DateTime value)
        {
            if (value.Kind == DateTimeKind.Local)
            {
                return value;
            }

            try
            {
                return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();
            }
            catch
            {
                return value;
            }
        }
    }
}
