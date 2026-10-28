using System;

namespace EmbyBackup.Core
{
    public static class BackupDestinationKinds
    {
        public const string Local = "Local";
        public const string Network = "Network";
        public const string Ftp = "Ftp";

        public static string Normalize(string value)
        {
            if (string.Equals(value, Ftp, StringComparison.OrdinalIgnoreCase))
            {
                return Ftp;
            }

            if (string.Equals(value, Network, StringComparison.OrdinalIgnoreCase))
            {
                return Network;
            }

            return Local;
        }
    }

    /// <summary>
    /// Destination chosen in the settings page. Local and Network both end up writing to a filesystem path
    /// (a local folder, a UNC share on Windows or a mounted share on Linux); FTP/FTPS uploads the archive.
    /// </summary>
    public sealed class BackupDestinationSettings
    {
        public string Type { get; set; } = BackupDestinationKinds.Local;

        /// <summary>Local folder, UNC path (Windows) or mount point (Linux) for Local and Network.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Only used on Windows, where the plugin can authenticate to an SMB share.</summary>
        public string NetworkUsername { get; set; }

        public string NetworkPassword { get; set; }

        public string NetworkDomain { get; set; }

        public string FtpHost { get; set; }

        public int FtpPort { get; set; } = 21;

        /// <summary>Remote folder, always starting with a slash. "/" means the login folder.</summary>
        public string FtpPath { get; set; } = "/";

        public string FtpUsername { get; set; }

        public string FtpPassword { get; set; }

        /// <summary>Explicit FTPS (AUTH TLS). Plain FTP when false.</summary>
        public bool FtpUseTls { get; set; }

        public bool FtpPassive { get; set; } = true;

        /// <summary>Accepts a self-signed certificate, only for the configured FTP host.</summary>
        public bool FtpAcceptAnyCertificate { get; set; }

        public bool IsLocal
        {
            get { return string.Equals(NormalizedType, BackupDestinationKinds.Local, StringComparison.Ordinal); }
        }

        public bool IsNetwork
        {
            get { return string.Equals(NormalizedType, BackupDestinationKinds.Network, StringComparison.Ordinal); }
        }

        public bool IsFtp
        {
            get { return string.Equals(NormalizedType, BackupDestinationKinds.Ftp, StringComparison.Ordinal); }
        }

        public string NormalizedType
        {
            get { return BackupDestinationKinds.Normalize(Type); }
        }

        /// <summary>Human readable destination used in the UI, the history and the logs (never the password).</summary>
        public string Location
        {
            get
            {
                if (IsFtp)
                {
                    var user = string.IsNullOrWhiteSpace(FtpUsername) ? string.Empty : FtpUsername + "@";
                    var port = FtpPort > 0 && FtpPort != 21 ? ":" + FtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
                    return "ftp://" + user + (FtpHost ?? string.Empty) + port + NormalizeFtpPath(FtpPath);
                }

                return (Path ?? string.Empty).Trim();
            }
        }

        public bool IsConfigured
        {
            get
            {
                if (IsFtp)
                {
                    return !string.IsNullOrWhiteSpace(FtpHost);
                }

                return !string.IsNullOrWhiteSpace(Path);
            }
        }

        public static string NormalizeFtpPath(string value)
        {
            var path = (value ?? string.Empty).Trim().Replace('\\', '/');
            if (path.Length == 0)
            {
                return "/";
            }

            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                path = "/" + path;
            }

            path = path.TrimEnd('/');
            return path.Length == 0 ? "/" : path;
        }
    }
}
