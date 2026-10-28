using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EmbyBackup.Core
{
    public sealed class InventoryFile
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public bool IsDatabase { get; set; }
    }

    public sealed class BackupInventory
    {
        public List<InventoryFile> Files { get; } = new List<InventoryFile>();
        public List<InventoryFile> Databases { get; } = new List<InventoryFile>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> ExcludedPaths { get; } = new List<string>();
    }

    public sealed class BackupInventoryScanner
    {
        private readonly string _root;
        private readonly List<string> _additionalExcludedDirectories;
        private readonly BackupInventory _inventory = new BackupInventory();

        public BackupInventoryScanner(string root, IEnumerable<string> additionalExcludedDirectories)
        {
            _root = Path.GetFullPath(root);
            _additionalExcludedDirectories = (additionalExcludedDirectories ?? Enumerable.Empty<string>())
                .Select(PathSafety.NormalizeRelative)
                .Where(p => p.Length > 0)
                .ToList();
        }

        public BackupInventory Scan()
        {
            foreach (var excluded in BackupConstants.ExcludedDirectories)
            {
                _inventory.ExcludedPaths.Add(excluded);
            }
            foreach (var excluded in _additionalExcludedDirectories)
            {
                _inventory.ExcludedPaths.Add(excluded);
            }

            ScanDirectory(string.Empty);

            _inventory.Files.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));
            _inventory.Databases.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));
            _inventory.ExcludedPaths.Sort(StringComparer.Ordinal);
            return _inventory;
        }

        private void ScanDirectory(string relativeDirectory)
        {
            var fullDirectory = relativeDirectory.Length == 0
                ? _root
                : Path.Combine(_root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(fullDirectory).GetFileSystemInfos();
            }
            catch (Exception ex)
            {
                _inventory.Warnings.Add("Could not read folder " + relativeDirectory + ": " + ex.Message);
                return;
            }

            foreach (var entry in entries)
            {
                var relative = relativeDirectory.Length == 0
                    ? entry.Name
                    : relativeDirectory + "/" + entry.Name;

                if (PathSafety.IsReparsePoint(entry))
                {
                    _inventory.Warnings.Add("Skipped symbolic link or junction: " + relative);
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    if (PathSafety.IsExcludedDirectory(relative) || IsAdditionalExcluded(relative))
                    {
                        continue;
                    }

                    ScanDirectory(relative);
                    continue;
                }

                if (PathSafety.IsExcludedFile(relative))
                {
                    continue;
                }

                var fileInfo = (FileInfo)entry;
                var isDatabase = IsSqliteDatabase(fileInfo.FullName);

                var item = new InventoryFile
                {
                    RelativePath = PathSafety.NormalizeRelative(relative),
                    FullPath = fileInfo.FullName,
                    Length = fileInfo.Length,
                    LastWriteUtc = fileInfo.LastWriteTimeUtc,
                    IsDatabase = isDatabase,
                };

                if (isDatabase)
                {
                    _inventory.Databases.Add(item);
                }
                else
                {
                    _inventory.Files.Add(item);
                }
            }
        }

        private bool IsAdditionalExcluded(string relative)
        {
            var normalized = PathSafety.NormalizeRelative(relative);
            foreach (var excluded in _additionalExcludedDirectories)
            {
                if (string.Equals(normalized, excluded, PathSafety.PathComparison)
                    || normalized.StartsWith(excluded + "/", PathSafety.PathComparison))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSqliteDatabase(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16, FileOptions.SequentialScan))
                {
                    if (stream.Length < 16)
                    {
                        return false;
                    }

                    var header = new byte[16];
                    var read = 0;
                    while (read < header.Length)
                    {
                        var chunk = stream.Read(header, read, header.Length - read);
                        if (chunk <= 0)
                        {
                            break;
                        }
                        read += chunk;
                    }

                    if (read < 16)
                    {
                        return false;
                    }

                    var expected = Encoding.ASCII.GetBytes("SQLite format 3\0");
                    for (var i = 0; i < expected.Length; i++)
                    {
                        if (header[i] != expected[i])
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
