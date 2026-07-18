// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

using DokanNet;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using FileAccess = DokanNet.FileAccess;

namespace UFS2Tool
{
    /// <summary>
    /// Implements the Dokan filesystem interface to mount a UFS1/UFS2 image
    /// as a Windows drive letter. Modeled after FreeBSD mount_udf(8) / mount(8).
    ///
    /// This provides read-only access to the UFS filesystem contents through
    /// a virtual Windows drive. The Dokan driver must be installed on the system.
    /// </summary>
    /// <remarks>
    /// Create a new Dokan filesystem backed by a UFS image.
    /// </remarks>
    /// <param name="imagePath">Path to the UFS1/UFS2 filesystem image.</param>
    /// <param name="readOnly">Mount as read-only.</param>
    [SupportedOSPlatform("windows")]
    public class Ufs2DokanOperations(string imagePath, bool readOnly) : IDokanOperations, IDisposable
    {
        private readonly Ufs2Image _image = new(imagePath, readOnly: readOnly);
        private readonly object _lock = new();
        private readonly bool _readOnly = readOnly;

        public void Dispose()
        {
            _image.Dispose();
        }

        /// <summary>
        /// Normalize a Windows path (backslashes) to a UFS path (forward slashes).
        /// </summary>
        private static string NormalizePath(string fileName)
        {
            return fileName.Replace('\\', '/');
        }

        /// <summary>
        /// Resolve a Windows path to a UFS inode number.
        /// Returns null if the path does not exist.
        /// </summary>
        private uint? TryResolvePath(string fileName)
        {
            try
            {
                string ufsPath = NormalizePath(fileName);
                lock (_lock)
                {
                    return _image.ResolvePath(ufsPath);
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Resolve a Windows path to a UFS inode number, using the cached inode in
        /// <see cref="IDokanFileInfo.Context"/> when available. The cache is populated
        /// by <see cref="CreateFile"/>, so subsequent operations on an open handle do
        /// not pay the cost of walking the directory tree again.
        /// </summary>
        private uint? TryGetInode(string fileName, IDokanFileInfo info)
        {
            if (info.Context is uint cachedInode)
                return cachedInode;
            return TryResolvePath(fileName);
        }

        /// <summary>
        /// Convert a Unix timestamp (seconds since epoch) to a DateTime.
        /// </summary>
        private static DateTime UnixToDateTime(long unixTime)
        {
            if (unixTime <= 0)
                return DateTime.MinValue;
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share,
            FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            // For a read-only mount, deny any write/create operations
            if (_readOnly)
            {
                if (mode == FileMode.CreateNew || mode == FileMode.Create ||
                    mode == FileMode.OpenOrCreate || mode == FileMode.Append)
                {
                    return DokanResult.AccessDenied;
                }

                if (access.HasFlag(FileAccess.WriteData) || access.HasFlag(FileAccess.AppendData) ||
                    access.HasFlag(FileAccess.Delete) || access.HasFlag(FileAccess.GenericWrite))
                {
                    return DokanResult.AccessDenied;
                }
            }

            // Creating new files is not supported (no new inode allocation via Dokan)
            if (mode == FileMode.CreateNew || mode == FileMode.Create)
            {
                return DokanResult.AccessDenied;
            }

            var inode = TryResolvePath(fileName);
            if (inode == null)
            {
                if (mode == FileMode.OpenOrCreate)
                    return DokanResult.AccessDenied;
                return DokanResult.FileNotFound;
            }

            Ufs2Inode inodeData;
            lock (_lock)
            {
                inodeData = _image.ReadInode(inode.Value);
            }

            if (inodeData.IsDirectory)
            {
                info.IsDirectory = true;
            }

            // Store inode number in context for later operations
            info.Context = inode.Value;
            return DokanResult.Success;
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
            // No cleanup needed for read-only filesystem
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
            info.Context = null;
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead,
            long offset, IDokanFileInfo info)
        {
            bytesRead = 0;

            if (offset < 0)
                return DokanResult.InvalidParameter;

            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.FileNotFound;

            try
            {
                lock (_lock)
                {
                    var inodeData = _image.ReadInode(inode.Value);
                    if (!inodeData.IsRegularFile && !inodeData.IsSymlink)
                        return DokanResult.AccessDenied;

                    if (offset >= inodeData.Size || buffer.Length == 0)
                        return DokanResult.Success;

                    long toRead = Math.Min(buffer.Length, inodeData.Size - offset);
                    using var destination = new MemoryStream(buffer, 0, (int)toRead, writable: true, publiclyVisible: true);
                    bytesRead = (int)_image.ReadFile(inode.Value, destination, offset, toRead, bufferSize: buffer.Length);
                }

                return DokanResult.Success;
            }
            catch (InvalidOperationException)
            {
                return DokanResult.AccessDenied;
            }
            catch (ArgumentOutOfRangeException)
            {
                return DokanResult.InvalidParameter;
            }
            catch (IOException)
            {
                return DokanResult.Error;
            }
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten,
            long offset, IDokanFileInfo info)
        {
            bytesWritten = 0;

            if (_readOnly)
                return DokanResult.AccessDenied;

            if (offset < 0)
                return DokanResult.InvalidParameter;

            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.FileNotFound;

            try
            {
                lock (_lock)
                {
                    var inodeData = _image.ReadInode(inode.Value);
                    if (!inodeData.IsRegularFile)
                        return DokanResult.AccessDenied;

                    // Read existing file data, apply the write, then replace
                    byte[] existingData = _image.ReadFileBytes(inode.Value);
                    long newSize = Math.Max(existingData.Length, offset + buffer.Length);
                    if (newSize > int.MaxValue)
                        return DokanResult.InvalidParameter;
                    byte[] newData = new byte[(int)newSize];
                    Buffer.BlockCopy(existingData, 0, newData, 0, existingData.Length);
                    Buffer.BlockCopy(buffer, 0, newData, (int)offset, buffer.Length);

                    string ufsPath = NormalizePath(fileName);
                    _image.ReplaceFileContent(ufsPath, newData);
                    bytesWritten = buffer.Length;
                }
                return DokanResult.Success;
            }
            catch (InvalidOperationException)
            {
                return DokanResult.AccessDenied;
            }
            catch (IOException)
            {
                return DokanResult.DiskFull;
            }
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo,
            IDokanFileInfo info)
        {
            fileInfo = new FileInformation();

            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.FileNotFound;

            Ufs2Inode inodeData;
            lock (_lock)
            {
                inodeData = _image.ReadInode(inode.Value);
            }

            fileInfo.FileName = Path.GetFileName(fileName.TrimEnd('\\'));
            if (string.IsNullOrEmpty(fileInfo.FileName))
                fileInfo.FileName = "\\";

            if (inodeData.IsDirectory)
            {
                fileInfo.Attributes = FileAttributes.Directory;
                if (_readOnly) fileInfo.Attributes |= FileAttributes.ReadOnly;
                fileInfo.Length = 0;
            }
            else
            {
                fileInfo.Attributes = _readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal;
                fileInfo.Length = inodeData.Size;
            }

            fileInfo.CreationTime = UnixToDateTime(inodeData.CreateTime);
            fileInfo.LastAccessTime = UnixToDateTime(inodeData.AccessTime);
            fileInfo.LastWriteTime = UnixToDateTime(inodeData.ModTime);

            return DokanResult.Success;
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files,
            IDokanFileInfo info)
        {
            files = [];

            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.PathNotFound;

            List<Ufs2DirectoryEntry> entries;
            lock (_lock)
            {
                var inodeData = _image.ReadInode(inode.Value);
                if (!inodeData.IsDirectory)
                    return DokanResult.NotADirectory;

                entries = _image.ListDirectory(inode.Value);
            }

            foreach (var entry in entries)
            {
                // Skip . and .. — Windows Explorer handles these
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                var fi = new FileInformation
                {
                    FileName = entry.Name
                };

                if (entry.FileType == Ufs2Constants.DtDir)
                {
                    fi.Attributes = FileAttributes.Directory;
                    if (_readOnly) fi.Attributes |= FileAttributes.ReadOnly;
                    fi.Length = 0;
                }
                else if (entry.FileType == Ufs2Constants.DtReg)
                {
                    fi.Attributes = _readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal;

                    // Read size from the inode
                    lock (_lock)
                    {
                        var entryInode = _image.ReadInode(entry.Inode);
                        fi.Length = entryInode.Size;
                        fi.CreationTime = UnixToDateTime(entryInode.CreateTime);
                        fi.LastAccessTime = UnixToDateTime(entryInode.AccessTime);
                        fi.LastWriteTime = UnixToDateTime(entryInode.ModTime);
                    }
                }
                else if (entry.FileType == Ufs2Constants.DtLnk)
                {
                    fi.Attributes = _readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal;

                    lock (_lock)
                    {
                        var entryInode = _image.ReadInode(entry.Inode);
                        fi.Length = entryInode.Size;
                        fi.CreationTime = UnixToDateTime(entryInode.CreateTime);
                        fi.LastAccessTime = UnixToDateTime(entryInode.AccessTime);
                        fi.LastWriteTime = UnixToDateTime(entryInode.ModTime);
                    }
                }
                else
                {
                    // Other types (fifo, char, block, socket) — show as system files
                    fi.Attributes = FileAttributes.ReadOnly | FileAttributes.System;

                    lock (_lock)
                    {
                        var entryInode = _image.ReadInode(entry.Inode);
                        fi.CreationTime = UnixToDateTime(entryInode.CreateTime);
                        fi.LastAccessTime = UnixToDateTime(entryInode.AccessTime);
                        fi.LastWriteTime = UnixToDateTime(entryInode.ModTime);
                    }
                }

                files.Add(fi);
            }

            return DokanResult.Success;
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern,
            out IList<FileInformation> files, IDokanFileInfo info)
        {
            // Get all files then filter by pattern
            var result = FindFiles(fileName, out var allFiles, info);
            if (result != DokanResult.Success)
            {
                files = [];
                return result;
            }

            if (string.IsNullOrEmpty(searchPattern) || searchPattern == "*")
            {
                files = allFiles;
                return DokanResult.Success;
            }

            files = [];
            foreach (var file in allFiles)
            {
                if (DokanHelper.DokanIsNameInExpression(searchPattern, file.FileName, true))
                {
                    files.Add(file);
                }
            }
            return DokanResult.Success;
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes,
            IDokanFileInfo info)
        {
            if (_readOnly)
                return DokanResult.AccessDenied;

            // UFS does not use Windows file attributes; accept but ignore
            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.FileNotFound;

            return DokanResult.Success;
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime,
            DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            if (_readOnly)
                return DokanResult.AccessDenied;

            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.FileNotFound;

            try
            {
                lock (_lock)
                {
                    var inodeData = _image.ReadInode(inode.Value);

                    if (creationTime.HasValue && creationTime.Value != DateTime.MinValue)
                        inodeData.CreateTime = new DateTimeOffset(creationTime.Value).ToUnixTimeSeconds();
                    if (lastAccessTime.HasValue && lastAccessTime.Value != DateTime.MinValue)
                        inodeData.AccessTime = new DateTimeOffset(lastAccessTime.Value).ToUnixTimeSeconds();
                    if (lastWriteTime.HasValue && lastWriteTime.Value != DateTime.MinValue)
                        inodeData.ModTime = new DateTimeOffset(lastWriteTime.Value).ToUnixTimeSeconds();

                    inodeData.ChangeTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _image.WriteInode(inode.Value, inodeData);
                }
                return DokanResult.Success;
            }
            catch (InvalidOperationException)
            {
                return DokanResult.AccessDenied;
            }
        }

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            if (_readOnly)
                return DokanResult.AccessDenied;

            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.FileNotFound;

            // Dokan invokes DeleteFile to ask whether deletion is allowed; the actual
            // unlink happens here for simplicity (the underlying image API is atomic).
            try
            {
                lock (_lock)
                {
                    var inodeData = _image.ReadInode(inode.Value);
                    if (!inodeData.IsRegularFile && !inodeData.IsSymlink)
                        return DokanResult.AccessDenied;

                    string ufsPath = NormalizePath(fileName);
                    _image.Delete(ufsPath);
                    info.Context = null;
                }
                return DokanResult.Success;
            }
            catch (InvalidOperationException)
            {
                return DokanResult.AccessDenied;
            }
            catch (IOException)
            {
                return DokanResult.AccessDenied;
            }
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            if (_readOnly)
                return DokanResult.AccessDenied;

            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.PathNotFound;

            try
            {
                lock (_lock)
                {
                    var inodeData = _image.ReadInode(inode.Value);
                    if (!inodeData.IsDirectory)
                        return DokanResult.AccessDenied;

                    // Refuse if the directory still contains user entries (anything
                    // other than "." and "..").
                    var entries = _image.ListDirectory(inode.Value);
                    foreach (var e in entries)
                    {
                        if (e.Name != "." && e.Name != "..")
                            return DokanResult.DirectoryNotEmpty;
                    }

                    string ufsPath = NormalizePath(fileName);
                    _image.Delete(ufsPath);
                    info.Context = null;
                }
                return DokanResult.Success;
            }
            catch (InvalidOperationException)
            {
                return DokanResult.AccessDenied;
            }
            catch (IOException)
            {
                return DokanResult.AccessDenied;
            }
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace,
            IDokanFileInfo info)
        {
            if (_readOnly)
                return DokanResult.AccessDenied;

            try
            {
                lock (_lock)
                {
                    string oldPath = NormalizePath(oldName);
                    string newPath = NormalizePath(newName);

                    // Extract just the new file/directory name from the new path
                    string newFileName = newPath.Contains('/')
                        ? newPath[(newPath.LastIndexOf('/') + 1)..]
                        : newPath;

                    _image.Rename(oldPath, newFileName);
                }
                return DokanResult.Success;
            }
            catch (InvalidOperationException)
            {
                return DokanResult.AccessDenied;
            }
            catch (IOException)
            {
                return DokanResult.AccessDenied;
            }
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            if (_readOnly)
                return DokanResult.AccessDenied;

            if (length < 0 || length > int.MaxValue)
                return DokanResult.InvalidParameter;

            var inode = TryGetInode(fileName, info);
            if (inode == null)
                return DokanResult.FileNotFound;

            try
            {
                lock (_lock)
                {
                    string ufsPath = NormalizePath(fileName);
                    byte[] existingData = _image.ReadFileBytes(inode.Value);
                    byte[] newData = new byte[(int)length];
                    Buffer.BlockCopy(existingData, 0, newData, 0, Math.Min(existingData.Length, (int)length));
                    _image.ReplaceFileContent(ufsPath, newData);
                }
                return DokanResult.Success;
            }
            catch (InvalidOperationException)
            {
                return DokanResult.AccessDenied;
            }
            catch (IOException)
            {
                return DokanResult.DiskFull;
            }
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            if (_readOnly)
                return DokanResult.AccessDenied;
            // Allocation size changes are handled via SetEndOfFile
            return DokanResult.Success;
        }

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable,
            out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
        {
            var sb = _image.Superblock;

            long totalFrags = sb.TotalBlocks;
            int fragSize = sb.FSize;

            totalNumberOfBytes = totalFrags * fragSize;
            // FreeBlocks is the count of free full blocks (cs_nbfree),
            // FreeFragments is the count of free individual fragments (cs_nffree).
            totalNumberOfFreeBytes = sb.FreeBlocks * sb.BSize + sb.FreeFragments * fragSize;
            freeBytesAvailable = _readOnly ? 0 : totalNumberOfFreeBytes;

            return DokanResult.Success;
        }

        public NtStatus GetVolumeInformation(out string volumeLabel,
            out FileSystemFeatures features, out string fileSystemName,
            out uint maximumComponentLength, IDokanFileInfo info)
        {
            string label = _image.Superblock.VolumeName?.Trim('\0') ?? "";
            volumeLabel = string.IsNullOrWhiteSpace(label)
                ? (_image.Superblock.IsUfs1 ? "UFS1" : "UFS2")
                : label;

            features = FileSystemFeatures.CasePreservedNames
                     | FileSystemFeatures.CaseSensitiveSearch
                     | FileSystemFeatures.UnicodeOnDisk;

            if (_readOnly)
                features |= FileSystemFeatures.ReadOnlyVolume;

            fileSystemName = _image.Superblock.IsUfs1 ? "UFS1" : "UFS2";
            maximumComponentLength = Ufs2Constants.MaxNameLen;

            return DokanResult.Success;
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security,
            AccessControlSections sections, IDokanFileInfo info)
        {
            // UFS uses Unix permissions, not Windows ACLs. Returning NotImplemented
            // tells Windows to fall back to its default handling.
            security = null;
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
            AccessControlSections sections, IDokanFileInfo info)
        {
            if (_readOnly)
                return DokanResult.AccessDenied;

            // UFS uses Unix permissions; Windows ACL changes are not applicable
            return DokanResult.NotImplemented;
        }

        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus Unmounted(IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams,
            IDokanFileInfo info)
        {
            streams = Array.Empty<FileInformation>();
            return DokanResult.NotImplemented;
        }
    }
}
