using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyBackup.Core;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyBackup
{
    /// <summary>
    /// Task shown in Dashboard > Scheduled Tasks. Its trigger follows the schedule saved in the plugin
    /// settings. The task can be disabled without removing it, and a manual backup from the settings page
    /// is always allowed.
    /// </summary>
    public class BackupTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IServerApplicationHost _applicationHost;

        public BackupTask(ILogManager logManager, IServerApplicationHost applicationHost)
        {
            _logger = logManager.GetLogger("EmbyBackup");
            _applicationHost = applicationHost;
        }

        public string Name
        {
            get { return "Emby Backup"; }
        }

        public string Key
        {
            get { return "EmbyBackup"; }
        }

        public string Description
        {
            get
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                {
                    return "Creates a full backup of Emby's server data and databases without metadata, artwork or media.";
                }

                var configuration = plugin.Configuration;
                if (configuration == null || !configuration.PluginEnabled)
                {
                    return "Emby Backup is disabled in the plugin settings.";
                }

                if (!configuration.ScheduledBackupsEnabled)
                {
                    return "Automatic backups are disabled in the plugin settings. Manual backups can still be "
                        + "started from the Emby Backup page.";
                }

                return "Creates a full backup of Emby's server data and databases without metadata, artwork or "
                    + "media. Automatic backups run " + BackupSchedule.Describe(configuration)
                    + " (server local time).";
            }
        }

        public string Category
        {
            get { return "Emby Backup"; }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var plugin = Plugin.Instance;
            if (plugin != null && plugin.Configuration != null)
            {
                try
                {
                    return new[] { BackupSchedule.BuildTrigger(plugin.Configuration) };
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Could not build the default backup trigger", ex);
                }
            }

            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = BackupScheduleRules.DefaultTime.Ticks,
                },
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Configuration.PluginEnabled)
            {
                _logger.Info("Scheduled Emby Backup is disabled because the plugin is inactive; nothing to do.");
                return;
            }

            if (!plugin.Configuration.ScheduledBackupsEnabled)
            {
                _logger.Info("Scheduled Emby Backup is disabled in the plugin settings; nothing to do.");
                return;
            }

            var configuration = plugin.Configuration;
            configuration.Normalize();

            if (!BackupRuntime.GetDestinationSettings().IsConfigured)
            {
                _logger.Warn("Scheduled Emby Backup skipped: no backup destination is configured.");
                return;
            }

            // Emby can only express "every day at" and "every week on", so every-other-day and monthly are
            // enforced here. This also prevents a second automatic backup on the same day.
            var now = DateTime.Now;
            var history = BackupHistoryStore.Read(BackupRuntime.PluginDataPath);
            if (!BackupSchedule.IsDue(configuration, history, now))
            {
                _logger.Info("Scheduled Emby Backup skipped: the configured period ("
                    + BackupSchedule.Describe(configuration) + ") is not due yet.");
                return;
            }

            var version = _applicationHost == null || _applicationHost.ApplicationVersion == null
                ? "unknown"
                : _applicationHost.ApplicationVersion.ToString();

            await BackupRuntime.RunScheduledAsync(_logger, version, cancellationToken, progress).ConfigureAwait(false);
        }
    }
}
