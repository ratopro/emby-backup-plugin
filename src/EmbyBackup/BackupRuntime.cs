using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyBackup.Core;
using MediaBrowser.Model.Logging;

namespace EmbyBackup
{
    public sealed class BackupRuntimeStatus
    {
        public bool Running { get; set; }
        public string State { get; set; }
        public double Progress { get; set; }
        public string Message { get; set; }
        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class BackupStartResult
    {
        public bool Started { get; set; }
        public string Message { get; set; }
        public BackupRuntimeStatus Status { get; set; }
    }

    /// <summary>
    /// Shared state between the scheduled task and the settings page. Only one backup can run at a time, whether
    /// it was started by the task or by the page.
    /// </summary>
    public static class BackupRuntime
    {
        private static readonly SemaphoreSlim RunLock = new SemaphoreSlim(1, 1);
        private static readonly object Sync = new object();

        private static bool _configured;
        private static string _programDataPath;
        private static string _pluginDataPath;
        private static string _pluginVersion;
        private static CancellationTokenSource _currentCancellation;
        private static BackupRuntimeStatus _status = Idle();

        public static void Configure(string programDataPath, string pluginDataPath, string pluginVersion)
        {
            lock (Sync)
            {
                _programDataPath = programDataPath;
                _pluginDataPath = pluginDataPath;
                _pluginVersion = pluginVersion;
                _configured = true;
            }
        }

        public static BackupRuntimeStatus GetStatus()
        {
            lock (Sync)
            {
                return Clone(_status);
            }
        }

        public static BackupStartResult StartManual(ILogger logger, string embyVersion)
        {
            EnsureConfigured();

            if (!IsPluginEnabled())
            {
                return new BackupStartResult
                {
                    Started = false,
                    Message = "Emby Backup is disabled in the plugin settings.",
                    Status = GetStatus(),
                };
            }

            if (!RunLock.Wait(0))
            {
                return new BackupStartResult
                {
                    Started = false,
                    Message = "A backup or restore is already running.",
                    Status = GetStatus(),
                };
            }

            var cancellation = new CancellationTokenSource();
            lock (Sync)
            {
                _currentCancellation = cancellation;
                _status = Running("Manual backup started", null);
            }

            Task.Run(() => Execute(logger, embyVersion, cancellation, null, "manual"));

            return new BackupStartResult
            {
                Started = true,
                Message = "The backup was started.",
                Status = GetStatus(),
            };
        }

        public static async Task RunScheduledAsync(
            ILogger logger,
            string embyVersion,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            EnsureConfigured();

            if (!IsPluginEnabled())
            {
                return;
            }

            await RunLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (Sync)
            {
                _currentCancellation = cancellation;
                _status = Running("Scheduled backup started", null);
            }

            try
            {
                Execute(logger, embyVersion, cancellation, progress, "scheduled");
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        public static bool Cancel()
        {
            CancellationTokenSource cancellation;
            lock (Sync)
            {
                cancellation = _currentCancellation;
            }

            if (cancellation == null)
            {
                return false;
            }

            try
            {
                cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public static string PluginDataPath
        {
            get
            {
                EnsureConfigured();
                return _pluginDataPath;
            }
        }

        public static string ProgramDataPath
        {
            get
            {
                EnsureConfigured();
                return _programDataPath;
            }
        }

        /// <summary>
        /// The backup and restore jobs share one lock: only one operation that reads or writes the program
        /// data folder may run at a time. RestoreRuntime uses these methods around its background task.
        /// </summary>
        internal static bool TryEnterOperation()
        {
            return RunLock.Wait(0);
        }

        internal static void ExitOperation()
        {
            RunLock.Release();
        }

        internal static bool IsOperationRunning
        {
            get { return RunLock.CurrentCount == 0; }
        }

        public static string DestinationPath
        {
            get
            {
                var plugin = Plugin.Instance;
                return plugin == null ? string.Empty : plugin.Configuration.DestinationPath;
            }
        }

        public static bool IsPluginEnabled()
        {
            var plugin = Plugin.Instance;
            return plugin != null && plugin.Configuration != null && plugin.Configuration.PluginEnabled;
        }

        public static BackupDestinationSettings GetDestinationSettings()
        {
            var plugin = Plugin.Instance;
            var configuration = plugin == null ? null : plugin.Configuration;
            if (configuration == null)
            {
                return new BackupDestinationSettings();
            }

            configuration.Normalize();
            return new BackupDestinationSettings
            {
                Type = configuration.DestinationType,
                Path = string.Equals(configuration.DestinationType, BackupDestinationKinds.Network, StringComparison.Ordinal)
                    ? configuration.NetworkPath
                    : configuration.DestinationPath,
                NetworkUsername = configuration.NetworkUsername,
                NetworkPassword = configuration.NetworkPassword,
                NetworkDomain = configuration.NetworkDomain,
                FtpHost = configuration.FtpHost,
                FtpPort = configuration.FtpPort,
                FtpPath = configuration.FtpPath,
                FtpUsername = configuration.FtpUsername,
                FtpPassword = configuration.FtpPassword,
                FtpUseTls = configuration.FtpUseTls,
                FtpPassive = configuration.FtpPassive,
                FtpAcceptAnyCertificate = configuration.FtpAcceptAnyCertificate,
            };
        }

        public static IBackupDestination CreateDestination(Action<string> log)
        {
            EnsureConfigured();
            return BackupDestinations.Create(GetDestinationSettings(), _programDataPath, log);
        }

        public static string DestinationLocation
        {
            get { return GetDestinationSettings().Location; }
        }

        private static void Execute(
            ILogger logger,
            string embyVersion,
            CancellationTokenSource cancellation,
            IProgress<double> progress,
            string origin)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                {
                    throw new InvalidOperationException("The Emby Backup plugin is not initialized.");
                }

                var configuration = plugin.Configuration;
                configuration.Normalize();

                if (configuration.ProtectBackupsWithPassword
                    && string.IsNullOrEmpty(configuration.BackupPassword))
                {
                    throw new InvalidOperationException(
                        "Password protection is enabled but no backup password is set. "
                        + "Set one in the plugin settings or turn password protection off.");
                }

                var request = new BackupRequest
                {
                    ProgramDataPath = _programDataPath,
                    PluginDataPath = _pluginDataPath,
                    Destination = GetDestinationSettings(),
                    EmbyVersion = embyVersion ?? "unknown",
                    PluginVersion = _pluginVersion,
                    Origin = origin,
                    Compression = configuration.Compression,
                    RetentionCount = configuration.RetentionCount,
                    BusyTimeoutSeconds = configuration.BusyTimeoutSeconds,
                    VerifyAfterCreate = configuration.VerifyAfterCreate,
                    Password = configuration.ProtectBackupsWithPassword
                        ? configuration.BackupPassword
                        : null,
                    Log = message =>
                    {
                        try
                        {
                            logger.Info(message);
                        }
                        catch
                        {
                            // Logging must never break a backup.
                        }
                    },
                    Progress = (percent, message) =>
                    {
                        UpdateProgress(percent, message);
                        if (progress != null)
                        {
                            try
                            {
                                progress.Report(Math.Max(0, Math.Min(100, percent)));
                            }
                            catch
                            {
                                // The scheduled task's progress sink is optional.
                            }
                        }
                    },
                };

                var result = new BackupService().Run(request, cancellation.Token);
                Complete(result);
            }
            catch (Exception ex)
            {
                try
                {
                    logger.ErrorException("Emby Backup failed unexpectedly", ex);
                }
                catch
                {
                    // Ignore logging failures.
                }

                lock (Sync)
                {
                    _status = new BackupRuntimeStatus
                    {
                        Running = false,
                        State = "failed",
                        Progress = _status.Progress,
                        Message = ex.Message,
                        StartedUtc = _status.StartedUtc,
                        FinishedUtc = BackupFormat.Utc(DateTime.UtcNow),
                        Warnings = new List<string>(_status.Warnings),
                    };
                }
            }
            finally
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_currentCancellation, cancellation))
                    {
                        _currentCancellation = null;
                    }
                }

                RunLock.Release();
            }
        }

        private static void Complete(BackupRunResult result)
        {
            lock (Sync)
            {
                var state = result.Success
                    ? "success"
                    : (result.Error == "The backup was cancelled." ? "cancelled" : "failed");

                _status = new BackupRuntimeStatus
                {
                    Running = false,
                    State = state,
                    Progress = result.Success ? 100 : _status.Progress,
                    Message = result.Success
                        ? "Backup complete: " + (result.ArchivePath == null ? string.Empty : System.IO.Path.GetFileName(result.ArchivePath))
                        : result.Error,
                    StartedUtc = _status.StartedUtc,
                    FinishedUtc = BackupFormat.Utc(DateTime.UtcNow),
                    Warnings = result.Warnings == null ? new List<string>() : new List<string>(result.Warnings),
                };
            }
        }

        private static void UpdateProgress(double percent, string message)
        {
            lock (Sync)
            {
                if (!_status.Running)
                {
                    return;
                }

                _status.Progress = Math.Max(0, Math.Min(100, percent));
                _status.Message = message ?? string.Empty;
            }
        }

        private static BackupRuntimeStatus Running(string message, string startedUtc)
        {
            return new BackupRuntimeStatus
            {
                Running = true,
                State = "running",
                Progress = 0,
                Message = message,
                StartedUtc = startedUtc ?? BackupFormat.Utc(DateTime.UtcNow),
                FinishedUtc = null,
                Warnings = new List<string>(),
            };
        }

        private static BackupRuntimeStatus Idle()
        {
            return new BackupRuntimeStatus
            {
                Running = false,
                State = "idle",
                Progress = 0,
                Message = "No backup is running.",
                Warnings = new List<string>(),
            };
        }

        private static BackupRuntimeStatus Clone(BackupRuntimeStatus status)
        {
            return new BackupRuntimeStatus
            {
                Running = status.Running,
                State = status.State,
                Progress = status.Progress,
                Message = status.Message,
                StartedUtc = status.StartedUtc,
                FinishedUtc = status.FinishedUtc,
                Warnings = status.Warnings == null ? new List<string>() : new List<string>(status.Warnings),
            };
        }

        private static void EnsureConfigured()
        {
            if (_configured)
            {
                return;
            }

            throw new InvalidOperationException("The Emby Backup plugin has not finished loading.");
        }
    }
}
