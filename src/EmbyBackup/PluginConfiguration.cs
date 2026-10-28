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
        /// Enables the nightly scheduled task. A manual backup can still be started when this is off.
        /// </summary>
        public bool ScheduledBackupsEnabled { get; set; } = true;

        /// <summary>
        /// Absolute path where the ZIP files are written. Empty means "not configured yet".
        /// </summary>
        public string DestinationPath { get; set; } = string.Empty;

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
        /// How long to wait for a database lock held by Emby before giving up.
        /// </summary>
        public int BusyTimeoutSeconds { get; set; } = 60;

        public void Normalize()
        {
            DestinationPath = (DestinationPath ?? string.Empty).Trim();
            RetentionCount = Math.Max(1, Math.Min(365, RetentionCount));
            BusyTimeoutSeconds = Math.Max(5, Math.Min(600, BusyTimeoutSeconds));

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
