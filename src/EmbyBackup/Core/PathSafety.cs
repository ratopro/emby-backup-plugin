using System;
using System.IO;

namespace EmbyBackup.Core
{
    public static class PathSafety
    {
        public static StringComparison PathComparison
        {
            get
            {
                return Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            }
        }

        public static string NormalizeRelative(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace('\\', '/').Trim('/');
        }

        public static string ToArchivePath(string relative)
        {
            var normalized = NormalizeRelative(relative);
            if (normalized.Length == 0)
            {
                throw new InvalidOperationException("An archive entry cannot be empty.");
            }

            var parts = normalized.Split('/');
            foreach (var part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOf(':') >= 0)
                {
                    throw new InvalidOperationException("Unsafe archive path: " + relative);
                }
            }

            return normalized;
        }

        public static bool IsSameOrSubPath(string parent, string candidate)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            var parentFull = TrimEnd(Path.GetFullPath(parent));
            var candidateFull = TrimEnd(Path.GetFullPath(candidate));

            if (string.Equals(parentFull, candidateFull, PathComparison))
            {
                return true;
            }

            return candidateFull.StartsWith(parentFull + Path.DirectorySeparatorChar, PathComparison)
                || candidateFull.StartsWith(parentFull + Path.AltDirectorySeparatorChar, PathComparison);
        }

        public static string RelativeTo(string root, string fullPath)
        {
            var rootFull = TrimEnd(Path.GetFullPath(root));
            var full = TrimEnd(Path.GetFullPath(fullPath));
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, PathComparison)
                && !full.StartsWith(rootFull + Path.AltDirectorySeparatorChar, PathComparison))
            {
                throw new InvalidOperationException("Path is outside the backup root: " + fullPath);
            }

            return NormalizeRelative(full.Substring(rootFull.Length + 1));
        }

        public static bool IsExcludedDirectory(string relative)
        {
            var normalized = NormalizeRelative(relative);
            foreach (var excluded in BackupConstants.ExcludedDirectories)
            {
                if (string.Equals(normalized, excluded, PathComparison)
                    || normalized.StartsWith(excluded + "/", PathComparison))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsExcludedFile(string relative)
        {
            var normalized = NormalizeRelative(relative);
            var name = Path.GetFileName(normalized);
            if (normalized.IndexOf('/') < 0)
            {
                foreach (var excludedRootName in BackupConstants.ExcludedRootFileNames)
                {
                    if (string.Equals(name, excludedRootName, PathComparison))
                    {
                        return true;
                    }
                }
            }

            foreach (var excludedName in BackupConstants.ExcludedFileNames)
            {
                if (string.Equals(name, excludedName, PathComparison))
                {
                    return true;
                }
            }

            foreach (var extension in BackupConstants.ExcludedFileExtensions)
            {
                if (normalized.EndsWith(extension, PathComparison))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        public static void ValidateBackupDestination(string programDataPath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new InvalidOperationException("Set an absolute backup destination first.");
            }

            if (!Path.IsPathRooted(destinationPath))
            {
                throw new InvalidOperationException("The backup destination must be an absolute path.");
            }

            var source = Path.GetFullPath(programDataPath);
            var destination = Path.GetFullPath(destinationPath);

            if (IsSameOrSubPath(source, destination))
            {
                throw new InvalidOperationException(
                    "The backup destination cannot be inside Emby's program data folder. Choose a separate folder or volume.");
            }

            Directory.CreateDirectory(destination);
        }

        /// <summary>
        /// Fails early, with an actionable message, when the Emby process cannot write to the destination.
        /// Emby usually runs as a service account (emby, docker, a NAS user...) that has no access to a
        /// personal home folder, so the raw "access to the path is denied" only shows up later, mid-backup.
        /// </summary>
        public static void EnsureDestinationWritable(string destinationPath)
        {
            var destination = Path.GetFullPath(destinationPath);

            try
            {
                Directory.CreateDirectory(destination);

                var probe = Path.Combine(destination, ".embybackup-write-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.WriteByte(0);
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(probe))
                        {
                            File.Delete(probe);
                        }
                    }
                    catch
                    {
                        // The probe is harmless: it is ignored by the retention and listed as foreign.
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                var user = Environment.UserName;
                var message = "Emby cannot write to the backup destination '" + destination + "': " + ex.Message
                    + " The Emby process runs as '" + user + "', and that account needs write access to this folder. "
                    + "Grant it explicitly (for example: sudo setfacl -m u:" + user + ":rwx '" + destination + "'"
                    + " and sudo setfacl -m u:" + user + ":x on every parent folder),"
                    + " or use a folder owned by that account"
                    + " (sudo mkdir -p /srv/emby-backup && sudo chown " + user + ":" + user + " /srv/emby-backup).";

                throw new InvalidOperationException(message, ex);
            }
        }

        private static string TrimEnd(string path)
        {
            if (path.Length <= 1)
            {
                return path;
            }

            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
