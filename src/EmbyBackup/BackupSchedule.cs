using System;
using System.Collections.Generic;
using System.Linq;
using EmbyBackup.Core;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyBackup
{
    /// <summary>
    /// Turns the schedule chosen in the plugin settings into the Emby task trigger. Emby can express
    /// daily and weekly triggers; every-other-day and monthly are enforced by the task itself, which
    /// checks the last successful automatic backup before starting.
    /// </summary>
    internal static class BackupSchedule
    {
        public const string TaskKey = "EmbyBackup";

        public static TaskTriggerInfo BuildTrigger(PluginConfiguration configuration)
        {
            configuration.Normalize();

            TimeSpan time;
            if (!BackupScheduleRules.TryParseTime(configuration.ScheduleTime, out time))
            {
                time = BackupScheduleRules.DefaultTime;
            }

            var period = BackupSchedulePeriods.Normalize(configuration.SchedulePeriod);
            var trigger = new TaskTriggerInfo
            {
                Type = string.Equals(period, BackupSchedulePeriods.Weekly, StringComparison.Ordinal)
                    ? TaskTriggerInfo.TriggerWeekly
                    : TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = time.Ticks,
            };

            if (string.Equals(period, BackupSchedulePeriods.Weekly, StringComparison.Ordinal))
            {
                DayOfWeek day;
                if (!BackupScheduleRules.TryParseDayOfWeek(configuration.ScheduleDayOfWeek, out day))
                {
                    day = DayOfWeek.Monday;
                }

                trigger.DayOfWeek = day;
            }

            return trigger;
        }

        public static string Describe(PluginConfiguration configuration)
        {
            return BackupScheduleRules.Describe(
                configuration.SchedulePeriod,
                configuration.ScheduleTime,
                configuration.ScheduleDayOfWeek);
        }

        /// <summary>
        /// Applies the trigger to the Emby task. The worker persists the triggers, so this also survives a
        /// restart; it is called at startup and whenever the plugin configuration is saved.
        /// </summary>
        public static void Apply(ITaskManager taskManager, PluginConfiguration configuration, ILogger logger)
        {
            if (taskManager == null || configuration == null)
            {
                return;
            }

            try
            {
                var worker = taskManager.ScheduledTasks == null
                    ? null
                    : taskManager.ScheduledTasks.FirstOrDefault(
                        candidate => candidate != null
                            && candidate.ScheduledTask != null
                            && string.Equals(candidate.ScheduledTask.Key, TaskKey, StringComparison.Ordinal));

                if (worker == null)
                {
                    logger.Info("Emby Backup task is not registered yet; the schedule will be applied on the next server start.");
                    return;
                }

                var desired = BuildTrigger(configuration);
                var current = worker.Triggers;
                if (current != null && current.Length == 1 && SameTrigger(current[0], desired))
                {
                    return;
                }

                worker.Triggers = new[] { desired };
                logger.Info("Automatic backup schedule set: " + Describe(configuration) + " (server local time).");
            }
            catch (Exception ex)
            {
                // A schedule that could not be applied must never prevent the plugin from loading.
                logger.ErrorException("Could not apply the automatic backup schedule", ex);
            }
        }

        internal static bool SameTrigger(TaskTriggerInfo current, TaskTriggerInfo desired)
        {
            if (current == null || desired == null)
            {
                return false;
            }

            return string.Equals(current.Type, desired.Type, StringComparison.Ordinal)
                && current.TimeOfDayTicks == desired.TimeOfDayTicks
                && current.DayOfWeek == desired.DayOfWeek;
        }

        /// <summary>True when the task should run now, based on the period and the last successful automatic run.</summary>
        public static bool IsDue(PluginConfiguration configuration, IList<BackupRecord> history, DateTime nowLocal)
        {
            DateTime? lastSuccess = null;
            if (history != null)
            {
                foreach (var record in history)
                {
                    if (record == null
                        || !string.Equals(record.Status, "success", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(record.Origin, "scheduled", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var created = BackupHistoryStore.ParseCreated(record.CreatedUtc);
                    if (created != DateTime.MinValue && (lastSuccess == null || created > lastSuccess.Value))
                    {
                        lastSuccess = created;
                    }
                }
            }

            return BackupScheduleRules.IsDue(configuration.SchedulePeriod, nowLocal, lastSuccess);
        }
    }
}
