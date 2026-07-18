// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using UFS2Tool.GUI.Services;

namespace UFS2Tool.GUI.ViewModels;

public partial class DirectoryEntryItem : ObservableObject
{
    public string Name { get; set; } = "";
    public uint InodeNumber { get; set; }
    public byte FileType { get; set; }
    public string FileTypeName => FileType switch
    {
        4 => "Directory",
        8 => "File",
        10 => "Symlink",
        _ => $"Type({FileType})"
    };
}

public partial class FilesystemOperationsViewModel(ObservableCollection<string> outputLog) : ViewModelBase
{
    private readonly ObservableCollection<string> _outputLog = outputLog;

    [ObservableProperty]
    private string _imagePath = "";

    [ObservableProperty]
    private string _fsPath = "/";

    [ObservableProperty]
    private string _localPath = "";

    [ObservableProperty]
    private string _outputDirectory = "";

    [ObservableProperty]
    private string _chmodMode = "755";

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private bool _chmodRecursive;

    [ObservableProperty]
    private string _findPattern = "";

    [ObservableProperty]
    private string _findTypeFilter = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _superblockInfo = "";

    public ObservableCollection<DirectoryEntryItem> DirectoryEntries { get; } = [];

    [RelayCommand]
    private async Task ShowInfoAsync()
    {
        if (!ValidateImagePath()) return;
        IsRunning = true;
        _outputLog.Add($"[Info] Loading filesystem information from '{ImagePath}'...");
        try
        {
            string info = "";
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                info = image.GetInfo();
            });
            SuperblockInfo = info;
            _outputLog.Add("[Info] Filesystem information loaded successfully.");
            _outputLog.Add(info);
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task ListDirectoryAsync()
    {
        if (!ValidateImagePath()) return;
        IsRunning = true;
        _outputLog.Add($"[LS] Listing directory '{FsPath}' in '{ImagePath}'...");
        try
        {
            (int fileCount, int dirCount, int symlinkCount) = (0, 0, 0);
            string report = "";
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                uint dirInode = string.IsNullOrWhiteSpace(FsPath) || FsPath == "/"
                    ? Ufs2Constants.RootInode
                    : image.ResolvePath(FsPath);
                var entries = image.ListDirectory(dirInode);
                var sb = new System.Text.StringBuilder();

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    DirectoryEntries.Clear();
                    foreach (var entry in entries)
                    {
                        if (entry.Inode == 0) continue;
                        DirectoryEntries.Add(new DirectoryEntryItem
                        {
                            Name = entry.Name,
                            InodeNumber = entry.Inode,
                            FileType = entry.FileType,
                        });
                    }
                });

                foreach (var entry in entries)
                {
                    if (entry.Inode == 0 || entry.Name == "." || entry.Name == "..") continue;
                    string typeStr = "???";
                    switch (entry.FileType)
                    {
                        case Ufs2Constants.DtDir: typeStr = "DIR "; dirCount++; break;
                        case Ufs2Constants.DtReg: typeStr = "FILE"; fileCount++; break;
                        case Ufs2Constants.DtLnk: typeStr = "LINK"; symlinkCount++; break;
                    }
                    sb.AppendLine($"  {typeStr}  {entry.Name}");
                }
                report = sb.ToString();
            });
            _outputLog.Add($"[LS] Listed directory: {FsPath} — {fileCount} file(s), {dirCount} dir(s), {symlinkCount} symlink(s)");
            _outputLog.Add(report);
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task ExtractAsync()
    {
        if (!ValidateImagePath()) return;
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            _outputLog.Add("[Error] Please specify an output directory.");
            return;
        }
        IsRunning = true;
        _outputLog.Add($"[Extract] Extracting '{FsPath}' from '{ImagePath}' to '{OutputDirectory}'...");
        try
        {
            string extractType = "";
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                string targetPath = string.IsNullOrWhiteSpace(FsPath) ? "/" : FsPath;
                var inode = image.ReadInode(image.ResolvePath(targetPath));
                extractType = inode.IsDirectory ? "directory" : "file";
                image.Extract(targetPath, OutputDirectory);
            });
            _outputLog.Add($"[Extract] Successfully extracted {extractType} '{FsPath}' to '{OutputDirectory}'.");
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!ValidateImagePath()) return;
        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            _outputLog.Add("[Error] Please specify a local file or directory path.");
            return;
        }
        IsRunning = true;
        bool isDir = System.IO.Directory.Exists(LocalPath);
        _outputLog.Add($"[Add] Adding {(isDir ? "directory" : "file")} '{LocalPath}' to '{FsPath}' in '{ImagePath}'...");
        try
        {
            await Task.Run(() =>
            {
                using var logWriter = new LogTextWriter(_outputLog);
                using var image = new Ufs2Image(ImagePath);
                image.Output = logWriter;
                string targetPath = string.IsNullOrWhiteSpace(FsPath) ? "/" : FsPath;
                image.Add(targetPath, LocalPath);
            });
            _outputLog.Add($"[Add] Successfully added '{LocalPath}' to '{FsPath}'.");
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!ValidateImagePath()) return;
        if (string.IsNullOrWhiteSpace(FsPath) || FsPath == "/")
        {
            _outputLog.Add("[Error] Please specify a filesystem path to delete.");
            return;
        }
        IsRunning = true;
        _outputLog.Add($"[Delete] Deleting '{FsPath}' from '{ImagePath}'...");
        try
        {
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath);
                image.Delete(FsPath);
            });
            _outputLog.Add($"[Delete] Successfully deleted '{FsPath}'.");
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (!ValidateImagePath()) return;
        if (string.IsNullOrWhiteSpace(FsPath) || FsPath == "/")
        {
            _outputLog.Add("[Error] Please specify a filesystem path to rename.");
            return;
        }
        if (string.IsNullOrWhiteSpace(NewName))
        {
            _outputLog.Add("[Error] Please specify a new name.");
            return;
        }
        IsRunning = true;
        _outputLog.Add($"[Rename] Renaming '{FsPath}' to '{NewName}' in '{ImagePath}'...");
        try
        {
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath);
                image.Rename(FsPath, NewName);
            });
            _outputLog.Add($"[Rename] Successfully renamed '{FsPath}' to '{NewName}'.");
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task ReplaceAsync()
    {
        if (!ValidateImagePath()) return;
        if (string.IsNullOrWhiteSpace(FsPath) || string.IsNullOrWhiteSpace(LocalPath))
        {
            _outputLog.Add("[Error] Please specify both a filesystem path and a local source path.");
            return;
        }
        IsRunning = true;
        _outputLog.Add($"[Replace] Replacing '{FsPath}' with '{LocalPath}' in '{ImagePath}'...");
        try
        {
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath);
                image.Replace(FsPath, LocalPath);
            });
            _outputLog.Add($"[Replace] Successfully replaced '{FsPath}' with '{LocalPath}'.");
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task ChmodAsync()
    {
        if (!ValidateImagePath()) return;
        if (string.IsNullOrWhiteSpace(ChmodMode))
        {
            _outputLog.Add("[Error] Please specify a chmod mode (e.g., 755).");
            return;
        }

        ushort mode;
        try
        {
            mode = Convert.ToUInt16(ChmodMode, 8);
        }
        catch (FormatException)
        {
            _outputLog.Add($"[Error] Invalid octal mode: '{ChmodMode}'. Use octal digits (0-7), e.g. 755.");
            return;
        }
        catch (OverflowException)
        {
            _outputLog.Add($"[Error] Mode value '{ChmodMode}' is out of range. Maximum is 7777.");
            return;
        }

        IsRunning = true;
        _outputLog.Add($"[Chmod] Changing permissions of '{FsPath}' to {ChmodMode}{(ChmodRecursive ? " (recursive)" : "")} in '{ImagePath}'...");
        try
        {
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath);
                if (ChmodRecursive)
                {
                    ushort dirMode = AddExecuteBits(mode);
                    image.ChmodAll(mode, dirMode);
                }
                else
                {
                    string targetPath = string.IsNullOrWhiteSpace(FsPath) ? "/" : FsPath;
                    image.Chmod(targetPath, mode);
                }
            });
            _outputLog.Add($"[Chmod] Successfully changed permissions of '{FsPath}' to {ChmodMode}{(ChmodRecursive ? " (recursive)" : "")}.");
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    /// <summary>
    /// Add execute bits to a file mode for use as a directory mode.
    /// If user/group/other has read permission, add the corresponding execute permission.
    /// </summary>
    private static ushort AddExecuteBits(ushort fileMode)
    {
        ushort dirMode = fileMode;
        if ((dirMode & 0x100) != 0) dirMode |= 0x040; // user read (0400) → user execute (0100)
        if ((dirMode & 0x020) != 0) dirMode |= 0x008; // group read (0040) → group execute (0010)
        if ((dirMode & 0x004) != 0) dirMode |= 0x001; // other read (0004) → other execute (0001)
        return dirMode;
    }

    [RelayCommand]
    private async Task StatAsync()
    {
        if (!ValidateImagePath()) return;
        if (string.IsNullOrWhiteSpace(FsPath))
        {
            _outputLog.Add("[Error] Please specify a filesystem path to stat.");
            return;
        }
        IsRunning = true;
        _outputLog.Add($"[Stat] Getting file information for '{FsPath}' in '{ImagePath}'...");
        try
        {
            string statInfo = "";
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                statInfo = image.GetStat(FsPath);
            });
            SuperblockInfo = statInfo;
            _outputLog.Add("[Stat] File information retrieved successfully.");
            _outputLog.Add(statInfo);
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task FindAsync()
    {
        if (!ValidateImagePath()) return;
        if (string.IsNullOrWhiteSpace(FindPattern))
        {
            _outputLog.Add("[Error] Please specify a search pattern (e.g., *.txt).");
            return;
        }
        IsRunning = true;
        string startPath = string.IsNullOrWhiteSpace(FsPath) ? "/" : FsPath;
        string? typeFilter = string.IsNullOrWhiteSpace(FindTypeFilter) ? null : FindTypeFilter;
        _outputLog.Add($"[Find] Searching for '{FindPattern}' in '{startPath}'{(typeFilter != null ? $" (type={typeFilter})" : "")}...");
        try
        {
            string report = "";
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                var results = image.Find(FindPattern, startPath, typeFilter);

                var sb = new System.Text.StringBuilder();
                if (results.Count == 0)
                {
                    sb.AppendLine("No matches found.");
                }
                else
                {
                    foreach (var result in results)
                    {
                        string typeStr = result.FileType switch
                        {
                            Ufs2Constants.DtDir => "DIR ",
                            Ufs2Constants.DtReg => "FILE",
                            Ufs2Constants.DtLnk => "LINK",
                            _ => "??? "
                        };
                        sb.AppendLine($"  {typeStr}  {result.Size,10}  {result.Path}");
                    }
                    sb.AppendLine($"\n{results.Count} match(es) found.");
                }
                report = sb.ToString();
            });
            SuperblockInfo = report;
            _outputLog.Add("[Find] Search completed.");
            _outputLog.Add(report);
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private async Task DiskUsageAsync()
    {
        if (!ValidateImagePath()) return;
        IsRunning = true;
        string targetPath = string.IsNullOrWhiteSpace(FsPath) ? "/" : FsPath;
        _outputLog.Add($"[DU] Calculating disk usage for '{targetPath}' in '{ImagePath}'...");
        try
        {
            string report = "";
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                var entries = image.DiskUsage(targetPath);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Disk usage for '{targetPath}':");
                sb.AppendLine();
                foreach (var entry in entries)
                {
                    string size = FormatHumanReadableSize(entry.Blocks * 512);
                    sb.AppendLine($"  {size,10}\t{entry.Path}");
                }
                report = sb.ToString();
            });
            SuperblockInfo = report;
            _outputLog.Add("[DU] Disk usage calculated successfully.");
            _outputLog.Add(report);
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    /// <summary>
    /// Format a byte count as a human-readable string (e.g., "1.5 KB", "234 MB").
    /// </summary>
    private static string FormatHumanReadableSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        int unitIndex = -1;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return $"{value:F1} {units[unitIndex]}";
    }

    private bool ValidateImagePath()
    {
        if (string.IsNullOrWhiteSpace(ImagePath))
        {
            _outputLog.Add("[Error] Please specify an image file path.");
            return false;
        }
        return true;
    }
}
