using System;

namespace EmbyBackup
{
    /// <summary>
    /// Settings stored by Emby in plugins/configurations/EmbyBackup.xml.
    /// Property names are also used by Web/configPage.js, so renaming one changes the saved XML.
    /// </summary>
    public class PluginConfiguration : MediaBrowser.Model.Plugins.BasePluginConfiguration
    {
        /// <summary>
        /// Enables the scheduled task. A manual backup can still be started when this is off.
        /// </summary>
        public bool ScheduledBackupsEnabled { get; set; } = true;

        /// <summary>
        /// Daily, AlternateDays, Weekly or Monthly. Matches BackupSchedulePeriods in Core.
        /// </summary>
        public string SchedulePeriod { get; set; } = "Daily";

        /// <summary>Local time of day for the automatic backup, as HH:mm.</summary>
        public string ScheduleTime { get; set; } = "03:00";

        /// <summary>Only used when SchedulePeriod is Weekly.</summary>
        public string ScheduleDayOfWeek { get; set; } = "Monday";

        /// <summary>
        /// Local, Network or Ftp. Matches BackupDestinationKinds in Core.
        /// </summary>
        public string DestinationType { get; set; } = "Local";

        /// <summary>
        /// Absolute path where the ZIP files are written for Local. Empty means "not configured yet".
        /// </summary>
        public string DestinationPath { get; set; } = string.Empty;

        /// <summary>
        /// Network share path: UNC (\\server\share\folder) on Windows or a mount point on Linux/NAS.
        /// </summary>
        public string NetworkPath { get; set; } = string.Empty;

        /// <summary>SMB credentials. Only used on Windows; the password is stored in this XML file as plain text.</summary>
        public string NetworkUsername { get; set; } = string.Empty;

        public string NetworkPassword { get; set; } = string.Empty;

        public string NetworkDomain { get; set; } = string.Empty;

        public string FtpHost { get; set; } = string.Empty;

        public int FtpPort { get; set; } = 21;

        /// <summary>Remote folder on the FTP server. "/" means the login folder.</summary>
        public string FtpPath { get; set; } = "/";

        public string FtpUsername { get; set; } = string.Empty;

        public string FtpPassword { get; set; } = string.Empty;

        /// <summary>Explicit FTPS (AUTH TLS). Plain FTP when false.</summary>
        public bool FtpUseTls { get; set; }

        public bool FtpPassive { get; set; } = true;

        public bool FtpAcceptAnyCertificate { get; set; }

        /// <summary>
        /// Number of successful backup ZIPs to keep. Older ones created by this plugin are deleted.
        /// </summary>
        public int RetentionCount { get; set; } = 7;

        /// <summary>
        /// Fastest, Optimal or NoCompression. Matches System.IO.Compression.CompressionLevel.
        /// </summary>
        public string Compression { get; set; } = "Optimal";

        /// <summary>
        /// Reads the finished archive back and compares every SHA-256 in the manifest.
        /// </summary>
        public bool VerifyAfterCreate { get; set; } = true;

        /// <summary>
        /// Encrypts new archives (manual and scheduled) with <see cref="BackupPassword"/>.
        /// </summary>
        public bool ProtectBackupsWithPassword { get; set; }

        /// <summary>
        /// Password used for new archives. Like the network and FTP passwords, it is stored in this XML
        /// file as plain text. An existing archive keeps the password it was created with.
        /// </summary>
        public string BackupPassword { get; set; } = string.Empty;

        /// <summary>
        /// How long to wait for a database lock held by Emby before giving up.
        /// </summary>
        public int BusyTimeoutSeconds { get; set; } = 60;

        public void Normalize()
        {
            SchedulePeriod = EmbyBackup.Core.BackupSchedulePeriods.Normalize(SchedulePeriod);
            ScheduleTime = EmbyBackup.Core.BackupScheduleRules.NormalizeTime(ScheduleTime);
            ScheduleDayOfWeek = EmbyBackup.Core.BackupScheduleRules.NormalizeDayOfWeek(ScheduleDayOfWeek);

            DestinationType = EmbyBackup.Core.BackupDestinationKinds.Normalize(DestinationType);
            DestinationPath = (DestinationPath ?? string.Empty).Trim();
            NetworkPath = (NetworkPath ?? string.Empty).Trim();
            NetworkUsername = (NetworkUsername ?? string.Empty).Trim();
            NetworkPassword = NetworkPassword ?? string.Empty;
            NetworkDomain = (NetworkDomain ?? string.Empty).Trim();

            FtpHost = (FtpHost ?? string.Empty).Trim();
            FtpPort = Math.Max(1, Math.Min(65535, FtpPort <= 0 ? 21 : FtpPort));
            FtpPath = EmbyBackup.Core.BackupDestinationSettings.NormalizeFtpPath(FtpPath);
            FtpUsername = (FtpUsername ?? string.Empty).Trim();
            FtpPassword = FtpPassword ?? string.Empty;

            RetentionCount = Math.Max(1, Math.Min(365, RetentionCount));
            BusyTimeoutSeconds = Math.Max(5, Math.Min(600, BusyTimeoutSeconds));
            BackupPassword = BackupPassword ?? string.Empty;

            if (!string.Equals(Compression, "Fastest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Compression, "NoCompression", StringComparison.OrdinalIgnoreCase))
            {
                Compression = "Optimal";
            }
            else if (string.Equals(Compression, "Fastest", StringComparison.OrdinalIgnoreCase))
            {
                Compression = "Fastest";
            }
            else
            {
                Compression = "NoCompression";
            }
        }
    }
}
