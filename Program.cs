using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BaronyCheckpoint;

class Program
{
    public const string BackupDir = "backups";
    public const int MaxBackupsPerSave = 1000;
    public const int FileAccessRetryDelay = 50;
    public const int MaxFileAccessRetries = 20;

    static void Main(string[] args)
    {
        if (!IsInSavegamesDirectory())
        {
            Console.WriteLine("This program must be run from a savegames directory.");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
            return;
        }

        Directory.CreateDirectory(BackupDir);

        var checkpointManager = new BaronyCheckpointManager();
        checkpointManager.StartWatching();

        Console.WriteLine("Barony Checkpoint Manager started!");
        Console.WriteLine($"- Auto-backup: ON (max {MaxBackupsPerSave} per save)");
        Console.WriteLine($"- Auto-restore: ON (restores newest backup on deletion)");
        Console.WriteLine($"- Backup location: ./{BackupDir}/");
        Console.WriteLine();
        Console.WriteLine("Press Enter to quit...");
        Console.ReadLine();

        checkpointManager.StopWatching();
    }

    private static bool IsInSavegamesDirectory()
    {
        var currentDir = Environment.CurrentDirectory;
        Console.WriteLine($"Current directory: {currentDir}");
        return currentDir.Contains("savegames", StringComparison.OrdinalIgnoreCase);
    }
}

public class BaronyCheckpointManager : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly object _lockObject = new object();
    private volatile bool _isProcessingDeletion = false;

    public void StartWatching()
    {
        _watcher = new FileSystemWatcher(".")
        {
            Filter = "*.baronysave",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };

        _watcher.Changed += OnSaveFileChanged;
        _watcher.Deleted += OnSaveFileDeleted;
        _watcher.EnableRaisingEvents = true;
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
    }

    private void OnSaveFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isProcessingDeletion) return;

        lock (_lockObject)
        {
            try
            {
                if (!WaitForFileAccess(e.FullPath))
                {
                    Console.WriteLine($"Could not access {e.Name} for backup - file may be locked.");
                    return;
                }

                CreateBackup(e.Name);
                CleanupOldBackups(e.Name);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Backup failed for {e.Name}: {exception.Message}");
            }
        }
    }

    private void OnSaveFileDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_lockObject)
        {
            try
            {
                _isProcessingDeletion = true;
                Console.WriteLine($"Save file deleted: {e.Name}");

                RestoreFromBackup(e.Name);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Restore failed for {e.Name}: {exception.Message}");
            }
            finally
            {
                Task.Delay(500).ContinueWith(_ => _isProcessingDeletion = false);
            }
        }
    }

    private void CreateBackup(string saveFileName)
    {
        try
        {
            var saveContent = File.ReadAllText(saveFileName);
            var level = ExtractDungeonLevel(saveContent);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"{saveFileName}.{level}.{timestamp}";
            var backupPath = Path.Combine(Program.BackupDir, backupFileName);

            File.Copy(saveFileName, backupPath);
            Console.WriteLine($"Backed up: {saveFileName} (Level {level}) -> {backupFileName}");
        }
        catch (JsonException)
        {
            CreateSimpleBackup(saveFileName);
        }
    }

    private void CreateSimpleBackup(string saveFileName)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupFileName = $"{saveFileName}.unknown.{timestamp}";
        var backupPath = Path.Combine(Program.BackupDir, backupFileName);

        File.Copy(saveFileName, backupPath);
        Console.WriteLine($"Backed up: {saveFileName} (corrupted json) -> {backupFileName}");
    }

    private int ExtractDungeonLevel(string jsonContent)
    {
        using var doc = JsonDocument.Parse(jsonContent);
        return doc.RootElement.GetProperty("dungeon_lvl").GetInt32();
    }

    private void RestoreFromBackup(string deletedSaveFileName)
    {
        var backupFiles = GetBackupFilesForSave(deletedSaveFileName);

        if (!backupFiles.Any())
        {
            Console.WriteLine($"No backups found for {deletedSaveFileName}");
            return;
        }

        var newestBackup = backupFiles.OrderByDescending(f => File.GetCreationTime(f))
            .First();

        File.Copy(newestBackup, deletedSaveFileName);

        var backupIUnfo = ParseBackupFileName(newestBackup);
        Console.WriteLine($"Restored: {deletedSaveFileName} from {Path.GetFileName(newestBackup)}");
        if (backupIUnfo.Level.HasValue)
        {
            Console.WriteLine($" Restored to dungeon level: {backupIUnfo.Level}");
        }
    }

    private void CleanupOldBackups(string saveFileName)
    {
        var backupFiles = GetBackupFilesForSave(saveFileName);

        if (backupFiles.Count <= Program.MaxBackupsPerSave) return;

        var filesToDelete = backupFiles
            .OrderBy(File.GetCreationTime)
            .Take(backupFiles.Count - Program.MaxBackupsPerSave);

        foreach (var file in filesToDelete)
        {
            try
            {
                File.Delete(file);
                Console.WriteLine($"Cleaned up old backup: {Path.GetFileName(file)}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to delete old backup {Path.GetFileName(file)}: {e.Message}");
            }
        }
    }

    private List<string> GetBackupFilesForSave(string saveFileName)
    {
        if (!Directory.Exists(Program.BackupDir)) return new List<string>();

        var saveNameWithoutExtension = Path.GetFileNameWithoutExtension(saveFileName);
        var backupPattern = $"{saveFileName}.*";

        return Directory.GetFiles(Program.BackupDir, backupPattern).ToList();
    }

    private (int? Level, DateTime TimeStamp) ParseBackupFileName(string backupPath)
    {
        try
        {
            var fileName = Path.GetFileName(backupPath);
            var parts = fileName.Split('.');

            if (parts.Length >= 4)
            {
                var levelStr = parts[^3];
                var timestampStr = parts[^2] + "." + parts[^1];

                if (int.TryParse(levelStr, out var level) && DateTime.TryParseExact(timestampStr, "yyyyMMdd_HHmmss", null, DateTimeStyles.None, out var timestamp))
                {
                    return (level, timestamp);
                }
            }
        }
        catch (Exception e)
        {
        }

        return (null, File.GetCreationTime(backupPath));
    }

    private bool WaitForFileAccess(string filePath)
    {
        for (int i = 0; i < Program.MaxFileAccessRetries; i++)
        {
            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(Program.FileAccessRetryDelay);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    public void Dispose()
    {
        StopWatching();
    }
}