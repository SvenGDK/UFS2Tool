// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using UFS2Tool.GUI.Services;

namespace UFS2Tool.GUI.ViewModels;

/// <summary>
/// Represents a single node in the filesystem tree (file or directory).
/// </summary>
public partial class FileTreeNodeItem : ObservableObject
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "/";
    public uint InodeNumber { get; set; }
    public byte FileType { get; set; }
    public bool IsDirectory => FileType == 4;
    public string Icon => FileType switch
    {
        4 => "📁",
        10 => "🔗",
        _ => "📄",
    };

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Whether the children of this directory have been loaded from the image.
    /// </summary>
    public bool IsLoaded { get; set; }

    public ObservableCollection<FileTreeNodeItem> Children { get; } = [];
}

/// <summary>
/// ViewModel for the Content Browser tab, providing a tree-view of UFS image contents
/// with drag &amp; drop support.
/// </summary>
public partial class ContentBrowserViewModel(ObservableCollection<string> outputLog) : ViewModelBase
{
    private readonly ObservableCollection<string> _outputLog = outputLog;
    private readonly System.Collections.Generic.HashSet<uint> _expandingNodes = [];

    [ObservableProperty]
    private string _imagePath = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private FileTreeNodeItem? _selectedNode;

    [ObservableProperty]
    private string _statusText = "";

    public ObservableCollection<FileTreeNodeItem> RootNodes { get; } = [];

    /// <summary>
    /// Load the entire root directory of the UFS image into the tree.
    /// </summary>
    [RelayCommand]
    private async Task LoadImageAsync()
    {
        if (string.IsNullOrWhiteSpace(ImagePath))
        {
            _outputLog.Add("[Browser] Please specify an image file path.");
            return;
        }
        if (!File.Exists(ImagePath))
        {
            _outputLog.Add($"[Browser] Image file not found: '{ImagePath}'.");
            return;
        }

        IsRunning = true;
        StatusText = "";
        _outputLog.Add($"[Browser] Loading filesystem tree from '{ImagePath}'...");
        try
        {
            ObservableCollection<FileTreeNodeItem> rootChildren = [];
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                var entries = image.ListDirectory(Ufs2Constants.RootInode);
                foreach (var entry in entries)
                {
                    if (entry.Inode == 0 || entry.Name == "." || entry.Name == "..") continue;
                    var node = new FileTreeNodeItem
                    {
                        Name = entry.Name,
                        FullPath = "/" + entry.Name,
                        InodeNumber = entry.Inode,
                        FileType = entry.FileType,
                    };
                    // Add a placeholder child for directories so the expand arrow appears
                    if (node.IsDirectory)
                        node.Children.Add(new FileTreeNodeItem { Name = "Loading..." });
                    rootChildren.Add(node);
                }
            });

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RootNodes.Clear();
                foreach (var node in rootChildren)
                    RootNodes.Add(node);
            });

            int dirCount = rootChildren.Count(n => n.IsDirectory);
            int fileCount = rootChildren.Count - dirCount;
            StatusText = $"{fileCount} file(s), {dirCount} directory(ies) at root";
            _outputLog.Add($"[Browser] Filesystem tree loaded: {fileCount} file(s), {dirCount} directory(ies) at root.");
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Browser Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    /// <summary>
    /// Lazily load children of a directory node when it is expanded.
    /// </summary>
    public async Task ExpandNodeAsync(FileTreeNodeItem node)
    {
        if (node.IsLoaded || !node.IsDirectory) return;
        if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath)) return;
        if (!_expandingNodes.Add(node.InodeNumber)) return;

        try
        {
            ObservableCollection<FileTreeNodeItem> children = [];
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                var entries = image.ListDirectory(node.InodeNumber);
                foreach (var entry in entries)
                {
                    if (entry.Inode == 0 || entry.Name == "." || entry.Name == "..") continue;
                    string childPath = node.FullPath.TrimEnd('/') + "/" + entry.Name;
                    var child = new FileTreeNodeItem
                    {
                        Name = entry.Name,
                        FullPath = childPath,
                        InodeNumber = entry.Inode,
                        FileType = entry.FileType,
                    };
                    if (child.IsDirectory)
                        child.Children.Add(new FileTreeNodeItem { Name = "Loading..." });
                    children.Add(child);
                }
            });

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                node.Children.Clear();
                foreach (var child in children)
                    node.Children.Add(child);
                node.IsLoaded = true;
            });
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Browser Error] Failed to expand '{node.FullPath}': {ex.Message}");
        }
        finally
        {
            _expandingNodes.Remove(node.InodeNumber);
        }
    }

    /// <summary>
    /// Extract the selected node from the UFS image to a local directory.
    /// </summary>
    public async Task ExtractNodeAsync(FileTreeNodeItem node, string outputDir)
    {
        if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath)) return;

        IsRunning = true;
        _outputLog.Add($"[Browser] Extracting '{node.FullPath}' to '{outputDir}'...");
        try
        {
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath, readOnly: true);
                image.Extract(node.FullPath, outputDir);
            });
            _outputLog.Add($"[Browser] Successfully extracted '{node.FullPath}' to '{outputDir}'.");
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Browser Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    /// <summary>
    /// Add a local file or directory into the UFS image at the given target path.
    /// </summary>
    public async Task AddToImageAsync(string targetFsPath, string localSourcePath)
    {
        if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath)) return;

        IsRunning = true;
        bool isDir = Directory.Exists(localSourcePath);
        _outputLog.Add($"[Browser] Adding {(isDir ? "directory" : "file")} '{localSourcePath}' to '{targetFsPath}'...");
        try
        {
            await Task.Run(() =>
            {
                using var logWriter = new LogTextWriter(_outputLog);
                using var image = new Ufs2Image(ImagePath);
                image.Output = logWriter;
                image.Add(targetFsPath, localSourcePath);
            });
            _outputLog.Add($"[Browser] Successfully added '{Path.GetFileName(localSourcePath)}' to '{targetFsPath}'.");
            // Refresh the tree after adding
            await LoadImageAsync();
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Browser Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    /// <summary>
    /// Delete the selected node from the UFS image.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedNode == null)
        {
            _outputLog.Add("[Browser] No item selected.");
            return;
        }
        if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath)) return;

        IsRunning = true;
        _outputLog.Add($"[Browser] Deleting '{SelectedNode.FullPath}' from '{ImagePath}'...");
        try
        {
            string path = SelectedNode.FullPath;
            await Task.Run(() =>
            {
                using var image = new Ufs2Image(ImagePath);
                image.Delete(path);
            });
            _outputLog.Add($"[Browser] Successfully deleted '{path}'.");
            await LoadImageAsync();
        }
        catch (Exception ex)
        {
            _outputLog.Add($"[Browser Error] {ex.Message}{(ex.InnerException != null ? $" — {ex.InnerException.Message}" : "")}");
        }
        finally { IsRunning = false; }
    }

    /// <summary>
    /// Extract the selected node to a folder chosen by the user.
    /// Called from code-behind after folder picker dialog.
    /// </summary>
    [RelayCommand]
    private async Task ExtractSelectedAsync()
    {
        // This is a placeholder — the actual folder picker is handled in code-behind,
        // which calls ExtractNodeAsync directly.
        if (SelectedNode == null)
            _outputLog.Add("[Browser] No item selected.");
    }
}
