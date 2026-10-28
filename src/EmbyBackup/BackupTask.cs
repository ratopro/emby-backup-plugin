using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyBackup
{
    /// <summary>
    /// Nightly task shown in Dashboard > Scheduled Tasks. It can be disabled in the plugin settings without
    /// removing the task, and a manual backup from the settings page is always allowed.
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
            get { return "Creates a full backup of Emby's server data and databases without metadata, artwork or media."; }
        }

        public string Category
        {
            get { return "Emby Backup"; }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
                },
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Configuration.ScheduledBackupsEnabled)
            {
                _logger.Info("Scheduled Emby Backup is disabled in the plugin settings; nothing to do.");
                return;
            }

            if (string.IsNullOrWhiteSpace(plugin.Configuration.DestinationPath))
            {
                _logger.Warn("Scheduled Emby Backup skipped: no backup destination is configured.");
                return;
            }

            var version = _applicationHost == null || _applicationHost.ApplicationVersion == null
                ? "unknown"
                : _applicationHost.ApplicationVersion.ToString();

            await BackupRuntime.RunScheduledAsync(_logger, version, cancellationToken, progress).ConfigureAwait(false);
        }
    }
}
