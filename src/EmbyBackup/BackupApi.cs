using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbyBackup.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace EmbyBackup
{
    [Route("/EmbyBackup/Status", "GET", Summary = "Current backup state and the last backup")]
    [Authenticated(Roles = "Admin")]
    public class BackupStatusRequest : IReturn<BackupStatusView>
    {
    }

    [Route("/EmbyBackup/History", "GET", Summary = "Recent backup history")]
    [Authenticated(Roles = "Admin")]
    public class BackupHistoryRequest : IReturn<List<BackupRecordView>>
    {
    }

    [Route("/EmbyBackup/Files", "GET", Summary = "Backup archives found in the destination folder")]
    [Authenticated(Roles = "Admin")]
    public class BackupFilesRequest : IReturn<BackupFilesView>
    {
    }

    [Route("/EmbyBackup/Run", "POST", Summary = "Start a backup now")]
    [Authenticated(Roles = "Admin")]
    public class BackupRunRequest : IReturn<BackupRunView>
    {
    }

    [Route("/EmbyBackup/Cancel", "POST", Summary = "Cancel the running backup")]
    [Authenticated(Roles = "Admin")]
    public class BackupCancelRequest : IReturn<BackupRunView>
    {
    }

    [Route("/EmbyBackup/Verify", "POST", Summary = "Verify a backup archive against its manifest and .sha256 file")]
    [Authenticated(Roles = "Admin")]
    public class BackupVerifyRequest : IReturn<BackupVerifyView>
    {
        public string FileName { get; set; }
    }

    [Route("/EmbyBackup/TestDestination", "POST", Summary = "Check that the configured destination is reachable and writable")]
    [Authenticated(Roles = "Admin")]
    public class BackupTestRequest : IReturn<BackupTestView>
    {
    }

    public class BackupTestView
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Location { get; set; }
    }

    public class BackupStatusView
    {
        public bool Running { get; set; }
        public string State { get; set; }
        public double Progress { get; set; }
        public string Message { get; set; }
        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public BackupRecordView Last { get; set; }
        public string DestinationPath { get; set; }
        public string DestinationType { get; set; }
        public bool ScheduledBackupsEnabled { get; set; }
        public string PluginVersion { get; set; }
    }

    public class BackupRunView
    {
        public bool Started { get; set; }
        public string Message { get; set; }
        public BackupStatusView Status { get; set; }
    }

    public class BackupRecordView
    {
        public string DateUtc { get; set; }
        public string FileName { get; set; }
        public string Destination { get; set; }
        public string Status { get; set; }
        public string Origin { get; set; }
        public bool Verified { get; set; }
        public long Bytes { get; set; }
        public int FileCount { get; set; }
        public int DatabaseCount { get; set; }
        public string Sha256 { get; set; }
        public double DurationSeconds { get; set; }
        public string Error { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class BackupFilesView
    {
        public string DestinationPath { get; set; }
        public string DestinationType { get; set; }
        public string Error { get; set; }
        public List<BackupFileView> Files { get; set; } = new List<BackupFileView>();
    }

    public class BackupFileView
    {
        public string FileName { get; set; }
        public long Bytes { get; set; }
        public string CreatedUtc { get; set; }
        public string LastWriteUtc { get; set; }
        public bool HasManifest { get; set; }
        public bool IsEmbyBackup { get; set; }
        public bool HasChecksumFile { get; set; }
        public bool MetadataKnown { get; set; }
        public int FileCount { get; set; }
        public int DatabaseCount { get; set; }
        public long TotalBytes { get; set; }
        public string Error { get; set; }
    }

    public class BackupVerifyView
    {
        public bool Valid { get; set; }
        public string Error { get; set; }
        public int CheckedFiles { get; set; }
        public int CheckedDatabases { get; set; }
        public string CreatedUtc { get; set; }
        public string EmbyVersion { get; set; }
        public string PluginVersion { get; set; }
        public string SourceProgramDataPath { get; set; }
        public long TotalBytes { get; set; }
    }

    public class BackupApi : IService
    {
        private readonly ILogger _logger;
        private readonly IServerApplicationHost _applicationHost;

        public BackupApi(ILogManager logManager, IServerApplicationHost applicationHost)
        {
            _logger = logManager.GetLogger("EmbyBackup");
            _applicationHost = applicationHost;
        }

        public object Get(BackupStatusRequest request)
        {
            return BuildStatus();
        }

        public object Get(BackupHistoryRequest request)
        {
            return BackupHistoryStore.Read(BackupRuntime.PluginDataPath)
                .Select(Map)
                .ToList();
        }

        public object Get(BackupFilesRequest request)
        {
            return BuildFiles();
        }

        public object Post(BackupRunRequest request)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return new BackupRunView
                {
                    Started = false,
                    Message = "The plugin is not initialized.",
                    Status = BuildStatus(),
                };
            }

            var configuration = plugin.Configuration;
            configuration.Normalize();
            if (!BackupRuntime.GetDestinationSettings().IsConfigured)
            {
                return new BackupRunView
                {
                    Started = false,
                    Message = "Set a backup destination and save the settings first.",
                    Status = BuildStatus(),
                };
            }

            var version = _applicationHost == null || _applicationHost.ApplicationVersion == null
                ? "unknown"
                : _applicationHost.ApplicationVersion.ToString();

            var start = BackupRuntime.StartManual(_logger, version);
            return new BackupRunView
            {
                Started = start.Started,
                Message = start.Message,
                Status = BuildStatus(),
            };
        }

        public object Post(BackupCancelRequest request)
        {
            var cancelled = BackupRuntime.Cancel();
            return new BackupRunView
            {
                Started = cancelled,
                Message = cancelled ? "Cancelling the running backup..." : "No backup is running.",
                Status = BuildStatus(),
            };
        }

        public object Post(BackupVerifyRequest request)
        {
            var view = new BackupVerifyView();
            var settings = BackupRuntime.GetDestinationSettings();
            if (!settings.IsConfigured)
            {
                view.Error = "No backup destination is configured.";
                return view;
            }

            if (request == null || string.IsNullOrWhiteSpace(request.FileName)
                || !BackupCatalog.IsManagedFileName(request.FileName))
            {
                view.Error = "Choose a backup archive created by this plugin.";
                return view;
            }

            string localPath = null;
            IBackupDestination destination = null;
            try
            {
                destination = BackupRuntime.CreateDestination(message => _logger.Info(message));
                localPath = destination.Fetch(request.FileName, "verify");
                var verification = new BackupVerifier().Verify(localPath, true);
                view.Valid = verification.Valid;
                view.Error = verification.Error;
                view.CheckedFiles = verification.CheckedFiles;
                view.CheckedDatabases = verification.CheckedDatabases;

                if (verification.Manifest != null)
                {
                    view.CreatedUtc = verification.Manifest.CreatedUtc;
                    view.EmbyVersion = verification.Manifest.EmbyVersion;
                    view.PluginVersion = verification.Manifest.PluginVersion;
                    view.SourceProgramDataPath = verification.Manifest.SourceProgramDataPath;
                    view.TotalBytes = verification.Manifest.TotalBytes;
                }
            }
            catch (Exception ex)
            {
                view.Valid = false;
                view.Error = ex.Message;
            }
            finally
            {
                if (destination != null)
                {
                    destination.Release(localPath);
                    destination.Dispose();
                }
            }

            return view;
        }

        public object Post(BackupTestRequest request)
        {
            var view = new BackupTestView();
            var settings = BackupRuntime.GetDestinationSettings();
            view.Location = settings.Location;

            if (!settings.IsConfigured)
            {
                view.Message = settings.IsFtp
                    ? "Enter the FTP server address first."
                    : "Choose a backup folder first.";
                return view;
            }

            try
            {
                using (var destination = BackupRuntime.CreateDestination(message => _logger.Info(message)))
                {
                    destination.Prepare();
                    view.Success = true;
                    view.Message = "The destination is reachable and writable: " + destination.Location;
                }
            }
            catch (Exception ex)
            {
                view.Success = false;
                view.Message = ex.Message;
            }

            return view;
        }

        private static BackupStatusView BuildStatus()
        {
            var status = BackupRuntime.GetStatus();
            var plugin = Plugin.Instance;
            var last = BackupHistoryStore.MostRecent(BackupRuntime.PluginDataPath);
            var destination = BackupRuntime.GetDestinationSettings();

            return new BackupStatusView
            {
                Running = status.Running,
                State = status.State,
                Progress = status.Progress,
                Message = status.Message,
                StartedUtc = status.StartedUtc,
                FinishedUtc = status.FinishedUtc,
                Warnings = status.Warnings,
                Last = last == null ? null : Map(last),
                DestinationPath = destination.Location,
                DestinationType = destination.NormalizedType,
                ScheduledBackupsEnabled = plugin != null && plugin.Configuration.ScheduledBackupsEnabled,
                PluginVersion = typeof(Plugin).Assembly.GetName().Version == null
                    ? "1.3.0"
                    : typeof(Plugin).Assembly.GetName().Version.ToString(),
            };
        }

        private static BackupFilesView BuildFiles()
        {
            var settings = BackupRuntime.GetDestinationSettings();
            var view = new BackupFilesView
            {
                DestinationPath = settings.Location,
                DestinationType = settings.NormalizedType,
            };

            if (!settings.IsConfigured)
            {
                view.Error = "No backup destination is configured.";
                return view;
            }

            try
            {
                using (var destination = BackupRuntime.CreateDestination(message => { }))
                {
                    var root = destination.LocalRootPath;
                    if (root != null)
                    {
                        if (!Directory.Exists(root))
                        {
                            view.Error = "The backup destination does not exist or is not reachable.";
                            return view;
                        }

                        view.Files = BackupCatalog.List(root)
                            .Select(info => new BackupFileView
                            {
                                FileName = info.FileName,
                                Bytes = info.Bytes,
                                CreatedUtc = info.CreatedUtcValue == DateTime.MinValue
                                    ? null
                                    : BackupFormat.Utc(info.CreatedUtcValue),
                                LastWriteUtc = BackupFormat.Utc(info.LastWriteUtc),
                                HasManifest = info.HasManifest,
                                IsEmbyBackup = info.IsEmbyBackup,
                                HasChecksumFile = info.HasChecksumFile,
                                MetadataKnown = true,
                                FileCount = info.FileCount,
                                DatabaseCount = info.DatabaseCount,
                                TotalBytes = info.TotalBytes,
                                Error = info.Error,
                            })
                            .ToList();
                    }
                    else
                    {
                        view.Files = destination.ListArchives()
                            .OrderByDescending(file => file.ModifiedUtc)
                            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(file => new BackupFileView
                            {
                                FileName = file.Name,
                                Bytes = file.Bytes < 0 ? 0 : file.Bytes,
                                CreatedUtc = file.ModifiedUtc == DateTime.MinValue
                                    ? null
                                    : BackupFormat.Utc(file.ModifiedUtc),
                                LastWriteUtc = file.ModifiedUtc == DateTime.MinValue
                                    ? null
                                    : BackupFormat.Utc(file.ModifiedUtc),
                                HasManifest = false,
                                IsEmbyBackup = false,
                                HasChecksumFile = file.HasSidecar,
                                MetadataKnown = false,
                            })
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                view.Error = "Could not list the backup destination: " + ex.Message;
            }

            return view;
        }

        private static BackupRecordView Map(BackupRecord record)
        {
            return new BackupRecordView
            {
                DateUtc = record.CreatedUtc,
                FileName = record.FileName,
                Destination = record.Destination,
                Status = record.Status,
                Origin = string.Equals(record.Origin, "scheduled", StringComparison.OrdinalIgnoreCase)
                    ? "scheduled"
                    : "manual",
                Verified = record.Verified,
                Bytes = record.Bytes,
                FileCount = record.FileCount,
                DatabaseCount = record.DatabaseCount,
                Sha256 = record.Sha256,
                DurationSeconds = record.DurationSeconds,
                Error = record.Error,
                Warnings = record.Warnings == null ? new List<string>() : record.Warnings,
            };
        }
    }
}
