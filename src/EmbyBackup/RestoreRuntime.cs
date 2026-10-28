using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbyBackup.Core;
using MediaBrowser.Model.Logging;

namespace EmbyBackup
{
    public sealed class RestoreRuntimeStatus
    {
        public bool Running { get; set; }
        public string State { get; set; }
        public double Progress { get; set; }
        public string Message { get; set; }
        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
        public string FileName { get; set; }
        public string Error { get; set; }
        public bool Applied { get; set; }
        public bool RestartRequired { get; set; }
        public bool PasswordRequired { get; set; }
        public bool WrongPassword { get; set; }
        public int RestoredFiles { get; set; }
        public int RestoredDatabases { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class RestoreStartResult
    {
        public bool Started { get; set; }
        public string Message { get; set; }
        public RestoreRuntimeStatus Status { get; set; }
    }

    /// <summary>
    /// Runs one restore in the background. It shares BackupRuntime's operation lock, so a backup and a
    /// restore can never read or write the program data folder at the same time.
    /// </summary>
    public static class RestoreRuntime
    {
        private static readonly object Sync = new object();
        private static CancellationTokenSource _currentCancellation;
        private static RestoreRuntimeStatus _status = Idle();

        public static RestoreRuntimeStatus GetStatus()
        {
            lock (Sync)
            {
                return Clone(_status);
            }
        }

        public static RestoreStartResult Start(ILogger logger, string fileName, string password)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return NotStarted("Choose a backup archive first.");
            }

            var destination = BackupRuntime.GetDestinationSettings();
            if (!destination.IsConfigured)
            {
                return NotStarted("Set a backup destination and save the settings first.");
            }

            if (!BackupRuntime.TryEnterOperation())
            {
                return NotStarted("A backup or restore is already running.");
            }

            var cancellation = new CancellationTokenSource();
            lock (Sync)
            {
                _currentCancellation = cancellation;
                _status = new RestoreRuntimeStatus
                {
                    Running = true,
                    State = "running",
                    Progress = 0,
                    Message = "Restore started. Verifying the archive...",
                    StartedUtc = BackupFormat.Utc(DateTime.UtcNow),
                    FileName = fileName,
                };
            }

            Task.Run(() => Execute(logger, fileName, password, cancellation));

            return new RestoreStartResult
            {
                Started = true,
                Message = "The restore was started.",
                Status = GetStatus(),
            };
        }

        private static void Execute(
            ILogger logger,
            string fileName,
            string password,
            CancellationTokenSource cancellation)
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

                var request = new RestoreRequest
                {
                    ProgramDataPath = BackupRuntime.ProgramDataPath,
                    PluginDataPath = BackupRuntime.PluginDataPath,
                    Destination = BackupRuntime.GetDestinationSettings(),
                    FileName = fileName,
                    Password = password,
                    BusyTimeoutSeconds = configuration.BusyTimeoutSeconds,
                    Log = message =>
                    {
                        try
                        {
                            logger.Info(message);
                        }
                        catch
                        {
                            // Logging must never break a restore.
                        }
                    },
                    Progress = UpdateProgress,
                };

                var service = new RestoreService(
                    configuration.BusyTimeoutSeconds,
                    message =>
                    {
                        try
                        {
                            logger.Info(message);
                        }
                        catch
                        {
                            // Ignore logging failures.
                        }
                    });

                var result = service.Run(request, cancellation.Token);
                Complete(result);
            }
            catch (Exception ex)
            {
                try
                {
                    logger.ErrorException("Emby Backup restore failed unexpectedly", ex);
                }
                catch
                {
                    // Ignore logging failures.
                }

                lock (Sync)
                {
                    _status = new RestoreRuntimeStatus
                    {
                        Running = false,
                        State = "failed",
                        Progress = _status.Progress,
                        Message = ex.Message,
                        StartedUtc = _status.StartedUtc,
                        FinishedUtc = BackupFormat.Utc(DateTime.UtcNow),
                        FileName = _status.FileName,
                        Error = ex.Message,
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

                cancellation.Dispose();
                BackupRuntime.ExitOperation();
            }
        }

        private static void Complete(RestoreRunResult result)
        {
            lock (Sync)
            {
                var state = result.Success
                    ? "success"
                    : (result.Error == "The restore was cancelled." ? "cancelled" : "failed");

                _status = new RestoreRuntimeStatus
                {
                    Running = false,
                    State = state,
                    Progress = result.Success ? 100 : _status.Progress,
                    Message = result.Success
                        ? "Restore complete. Restart Emby to load the restored data."
                        : result.Error,
                    StartedUtc = _status.StartedUtc,
                    FinishedUtc = BackupFormat.Utc(DateTime.UtcNow),
                    FileName = result.FileName ?? _status.FileName,
                    Error = result.Error,
                    Applied = result.Applied,
                    RestartRequired = result.RestartRequired,
                    PasswordRequired = result.PasswordRequired,
                    WrongPassword = result.WrongPassword,
                    RestoredFiles = result.RestoredFiles,
                    RestoredDatabases = result.RestoredDatabases,
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

        private static RestoreStartResult NotStarted(string message)
        {
            return new RestoreStartResult
            {
                Started = false,
                Message = message,
                Status = GetStatus(),
            };
        }

        private static RestoreRuntimeStatus Idle()
        {
            return new RestoreRuntimeStatus
            {
                Running = false,
                State = "idle",
                Progress = 0,
                Message = "No restore is running.",
                Warnings = new List<string>(),
            };
        }

        private static RestoreRuntimeStatus Clone(RestoreRuntimeStatus status)
        {
            return new RestoreRuntimeStatus
            {
                Running = status.Running,
                State = status.State,
                Progress = status.Progress,
                Message = status.Message,
                StartedUtc = status.StartedUtc,
                FinishedUtc = status.FinishedUtc,
                FileName = status.FileName,
                Error = status.Error,
                Applied = status.Applied,
                RestartRequired = status.RestartRequired,
                PasswordRequired = status.PasswordRequired,
                WrongPassword = status.WrongPassword,
                RestoredFiles = status.RestoredFiles,
                RestoredDatabases = status.RestoredDatabases,
                Warnings = status.Warnings == null ? new List<string>() : new List<string>(status.Warnings),
            };
        }
    }
}
