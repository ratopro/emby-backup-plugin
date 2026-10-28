using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace EmbyBackup.Core
{
    public sealed class SqliteSnapshot
    {
        public string Path { get; set; }
        public string SqliteVersion { get; set; }
        public string QuickCheck { get; set; }
    }

    /// <summary>
    /// Creates point-in-time SQLite snapshots with SQLite's online backup API.
    /// Copying a live .db file with File.Copy is not safe when WAL is enabled; this is.
    /// </summary>
    public sealed class SqliteSnapshotService
    {
        private readonly int _busyTimeoutMilliseconds;
        private readonly Action<string> _log;

        public SqliteSnapshotService(int busyTimeoutSeconds, Action<string> log)
        {
            _busyTimeoutMilliseconds = Math.Max(5, busyTimeoutSeconds) * 1000;
            _log = log ?? (_ => { });
        }

        public SqliteSnapshot Create(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Database not found.", sourcePath);
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            DeleteIfExists(destinationPath);
            DeleteIfExists(destinationPath + "-wal");
            DeleteIfExists(destinationPath + "-shm");
            DeleteIfExists(destinationPath + "-journal");

            var version = SqliteNative.Version();
            IntPtr source = IntPtr.Zero;
            IntPtr destination = IntPtr.Zero;
            IntPtr backup = IntPtr.Zero;

            try
            {
                source = OpenSource(sourcePath);
                SqliteNative.BusyTimeout(source, _busyTimeoutMilliseconds);

                destination = SqliteNative.Open(destinationPath, SqliteNative.ReadWrite | SqliteNative.Create, _busyTimeoutMilliseconds);
                backup = SqliteNative.BackupInit(destination, "main", source, "main");
                if (backup == IntPtr.Zero)
                {
                    throw new InvalidOperationException("SQLite could not start the backup: " + SqliteNative.Error(destination));
                }

                var watch = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rc = SqliteNative.BackupStep(backup, 256);
                    if (rc == SqliteNative.Done)
                    {
                        break;
                    }

                    if (rc == SqliteNative.Ok)
                    {
                        continue;
                    }

                    if (rc == SqliteNative.Busy || rc == SqliteNative.Locked)
                    {
                        if (watch.ElapsedMilliseconds >= _busyTimeoutMilliseconds)
                        {
                            throw new InvalidOperationException(
                                "The database stayed locked for " + (_busyTimeoutMilliseconds / 1000)
                                + " seconds. Close the operation using it, or schedule the backup when Emby is idle.");
                        }

                        Thread.Sleep(100);
                        continue;
                    }

                    throw new InvalidOperationException("SQLite backup failed (" + rc + "): " + SqliteNative.Error(destination));
                }
            }
            finally
            {
                if (backup != IntPtr.Zero)
                {
                    SqliteNative.BackupFinish(backup);
                }

                if (destination != IntPtr.Zero)
                {
                    SqliteNative.Close(destination);
                }

                if (source != IntPtr.Zero)
                {
                    SqliteNative.Close(source);
                }
            }

            DeleteIfExists(destinationPath + "-wal");
            DeleteIfExists(destinationPath + "-shm");
            DeleteIfExists(destinationPath + "-journal");

            var check = QuickCheck(destinationPath);
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Snapshot verification failed for " + sourcePath + ": " + check);
            }

            _log("SQLite snapshot ready: " + sourcePath + " -> " + destinationPath);
            return new SqliteSnapshot
            {
                Path = destinationPath,
                SqliteVersion = version,
                QuickCheck = check,
            };
        }

        public string QuickCheck(string path)
        {
            IntPtr db = IntPtr.Zero;
            IntPtr statement = IntPtr.Zero;
            try
            {
                db = SqliteNative.Open(path, SqliteNative.ReadOnly, _busyTimeoutMilliseconds);
                SqliteNative.BusyTimeout(db, _busyTimeoutMilliseconds);
                statement = SqliteNative.Prepare(db, "PRAGMA quick_check");
                var values = new List<string>();
                while (true)
                {
                    var rc = SqliteNative.Step(statement);
                    if (rc == SqliteNative.Row)
                    {
                        var text = SqliteNative.ColumnText(statement, 0);
                        if (!string.IsNullOrEmpty(text))
                        {
                            values.Add(text);
                        }
                        continue;
                    }

                    if (rc == SqliteNative.Done)
                    {
                        break;
                    }

                    throw new InvalidOperationException("SQLite quick_check failed (" + rc + "): " + SqliteNative.Error(db));
                }

                return values.Count == 0 ? "ok" : string.Join("; ", values.ToArray());
            }
            finally
            {
                if (statement != IntPtr.Zero)
                {
                    SqliteNative.Finalize(statement);
                }
                if (db != IntPtr.Zero)
                {
                    SqliteNative.Close(db);
                }
            }
        }

        private static IntPtr OpenSource(string path)
        {
            try
            {
                return SqliteNative.Open(path, SqliteNative.ReadOnly, 0);
            }
            catch (SqliteException)
            {
                // A WAL database without -shm cannot always be opened read-only. Opening it read/write lets
                // SQLite create/read the shared-memory file without changing the logical database content.
                return SqliteNative.Open(path, SqliteNative.ReadWrite, 0);
            }
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // The caller will get a clearer error when the snapshot is opened.
            }
        }
    }

    internal sealed class SqliteException : Exception
    {
        public SqliteException(string message) : base(message)
        {
        }
    }

    internal static class SqliteNative
    {
        public const int Ok = 0;
        public const int Row = 100;
        public const int Done = 101;
        public const int Busy = 5;
        public const int Locked = 6;

        public const int ReadOnly = 1;
        public const int ReadWrite = 2;
        public const int Create = 4;
        private const int FullMutex = 0x10000;

        public static string Version()
        {
            try
            {
                var pointer = sqlite3_libversion();
                return pointer == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(pointer);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The native SQLite library could not be loaded. Emby must be able to load sqlite3 on this system.", ex);
            }
        }

        public static IntPtr Open(string path, int flags, int busyTimeoutMilliseconds)
        {
            IntPtr db = IntPtr.Zero;
            var rc = WithUtf8(path, pointer => sqlite3_open_v2(pointer, out db, flags | FullMutex, IntPtr.Zero));
            if (rc != Ok)
            {
                var message = db == IntPtr.Zero ? "unknown error" : Error(db);
                if (db != IntPtr.Zero)
                {
                    sqlite3_close_v2(db);
                }
                throw new SqliteException("Could not open SQLite database " + path + " (" + rc + "): " + message);
            }

            if (busyTimeoutMilliseconds > 0)
            {
                BusyTimeout(db, busyTimeoutMilliseconds);
            }

            return db;
        }

        public static void BusyTimeout(IntPtr db, int milliseconds)
        {
            sqlite3_busy_timeout(db, milliseconds);
        }

        public static IntPtr BackupInit(IntPtr destination, string destinationName, IntPtr source, string sourceName)
        {
            return sqlite3_backup_init(destination, destinationName, source, sourceName);
        }

        public static int BackupStep(IntPtr backup, int pages)
        {
            return sqlite3_backup_step(backup, pages);
        }

        public static int BackupFinish(IntPtr backup)
        {
            return sqlite3_backup_finish(backup);
        }

        public static IntPtr Prepare(IntPtr db, string sql)
        {
            IntPtr statement;
            IntPtr tail;
            var rc = sqlite3_prepare_v2(db, sql, -1, out statement, out tail);
            if (rc != Ok)
            {
                throw new SqliteException("SQLite prepare failed (" + rc + "): " + Error(db));
            }
            return statement;
        }

        public static int Step(IntPtr statement)
        {
            return sqlite3_step(statement);
        }

        public static string ColumnText(IntPtr statement, int column)
        {
            var pointer = sqlite3_column_text(statement, column);
            return pointer == IntPtr.Zero ? null : FromUtf8(pointer);
        }

        public static void Finalize(IntPtr statement)
        {
            sqlite3_finalize(statement);
        }

        public static string Error(IntPtr db)
        {
            if (db == IntPtr.Zero)
            {
                return "unknown error";
            }

            var pointer = sqlite3_errmsg(db);
            return pointer == IntPtr.Zero ? "unknown error" : FromUtf8(pointer);
        }

        public static void Close(IntPtr db)
        {
            if (db != IntPtr.Zero)
            {
                sqlite3_close_v2(db);
            }
        }

        private static int WithUtf8(string text, Func<IntPtr, int> action)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                return action(pointer);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private static string FromUtf8(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            var length = 0;
            while (Marshal.ReadByte(pointer, length) != 0)
            {
                length++;
            }

            if (length == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_libversion")]
        private static extern IntPtr sqlite3_libversion();

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
        private static extern int sqlite3_open_v2(
            IntPtr filename,
            out IntPtr db,
            int flags,
            IntPtr vfs);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_busy_timeout")]
        private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_init")]
        private static extern IntPtr sqlite3_backup_init(
            IntPtr destination,
            [MarshalAs(UnmanagedType.LPStr)] string destinationName,
            IntPtr source,
            [MarshalAs(UnmanagedType.LPStr)] string sourceName);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_step")]
        private static extern int sqlite3_backup_step(IntPtr backup, int pages);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_finish")]
        private static extern int sqlite3_backup_finish(IntPtr backup);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        private static extern int sqlite3_prepare_v2(
            IntPtr db,
            [MarshalAs(UnmanagedType.LPStr)] string sql,
            int bytes,
            out IntPtr statement,
            out IntPtr tail);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        private static extern int sqlite3_finalize(IntPtr statement);
    }
}
