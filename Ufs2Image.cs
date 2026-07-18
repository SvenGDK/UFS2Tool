// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool
{
    /// <summary>
    /// High-level API for reading and writing to a UFS1/UFS2 filesystem image.
    /// </summary>
    public class Ufs2Image : IDisposable
    {
        private FileStream _stream;
        private BinaryReader _reader;
        private BinaryWriter? _writer;

        /// <summary>
        /// Gets the BinaryWriter, throwing if the image was opened read-only.
        /// </summary>
        private BinaryWriter Writer => _writer ?? throw new InvalidOperationException("Image is read-only.");

        public Ufs2Superblock Superblock { get; private set; } = null!;
        public string ImagePath { get; }
        public bool IsReadOnly { get; }

        /// <summary>
        /// Optional output writer for progress messages during add operations.
        /// </summary>
        public TextWriter? Output { get; set; }

        public Ufs2Image(string imagePath, bool readOnly = false)
            : this(imagePath, readOnly, altSuperblockSector: -1)
        {
        }

        /// <summary>
        /// Open a UFS1/UFS2 filesystem image, optionally reading the superblock from
        /// an alternate location specified as a 512-byte sector offset (e.g. one of
        /// the backup superblock locations printed by newfs/growfs). Mirrors
        /// FreeBSD's <c>fsck_ffs -b block</c> behavior. When
        /// <paramref name="altSuperblockSector"/> is negative the primary superblock
        /// at <see cref="Ufs2Constants.SuperblockOffset"/> is used.
        /// </summary>
        public Ufs2Image(string imagePath, bool readOnly, long altSuperblockSector)
        {
            ImagePath = imagePath;
            IsReadOnly = readOnly;

            var access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
            _stream = new FileStream(imagePath, FileMode.Open, access);
            _reader = new BinaryReader(_stream);
            if (!readOnly)
                _writer = new BinaryWriter(_stream);

            try
            {
                long sbOffset = altSuperblockSector >= 0
                    ? altSuperblockSector * Ufs2Constants.DefaultSectorSize
                    : Ufs2Constants.SuperblockOffset;
                ReadSuperblock(sbOffset);
            }
            catch
            {
                _writer?.Dispose();
                _reader.Dispose();
                _stream.Dispose();
                throw;
            }
        }

        private void ReadSuperblock(long offset = Ufs2Constants.SuperblockOffset)
        {
            if (offset < 0 || offset + Ufs2Constants.SuperblockSize > _stream.Length)
                throw new InvalidDataException(
                    $"Superblock offset {offset} is outside the image bounds ({_stream.Length} bytes).");

            _stream.Position = offset;
            Superblock = Ufs2Superblock.ReadFrom(_reader);

            if (!Superblock.IsValid)
                throw new InvalidDataException(
                    $"Invalid UFS superblock at offset {offset}. " +
                    $"Magic: 0x{Superblock.Magic:X8}, " +
                    $"Expected: 0x{Ufs2Constants.Ufs2Magic:X8} (UFS2) or 0x{Ufs2Constants.Ufs1Magic:X8} (UFS1)");
        }

        /// <summary>
        /// Read an inode by its inode number. Supports both UFS1 and UFS2 formats.
        /// </summary>
        public Ufs2Inode ReadInode(uint inodeNumber)
        {
            long totalInodes = (long)Superblock.NumCylGroups * Superblock.InodesPerGroup;
            if (inodeNumber >= totalInodes)
                throw new ArgumentOutOfRangeException(nameof(inodeNumber),
                    $"Inode number {inodeNumber} exceeds total inodes ({totalInodes}).");

            int cgIndex = (int)(inodeNumber / Superblock.InodesPerGroup);
            int inodeIndexInCg = (int)(inodeNumber % Superblock.InodesPerGroup);

            int inodeSize = Superblock.IsUfs1
                ? Ufs2Constants.Ufs1InodeSize : Ufs2Constants.Ufs2InodeSize;

            long cgStart = (long)cgIndex * Superblock.CylGroupSize * Superblock.FSize;
            long inodeTableStart = cgStart + (long)Superblock.IblkNo * Superblock.FSize;

            long inodeOffset = inodeTableStart + (long)inodeIndexInCg * inodeSize;

            if (inodeOffset < 0 || inodeOffset + inodeSize > _stream.Length)
                throw new InvalidOperationException(
                    $"Inode {inodeNumber} offset {inodeOffset} is outside the image bounds ({_stream.Length} bytes).");

            _stream.Position = inodeOffset;

            if (Superblock.IsUfs1)
            {
                // Read UFS1 inode and convert to UFS2 inode for API consistency
                var ufs1 = Ufs1Inode.ReadFrom(_reader);
                return ConvertUfs1ToUfs2Inode(ufs1);
            }

            return Ufs2Inode.ReadFrom(_reader);
        }

        /// <summary>
        /// Convert a UFS1 inode to a UFS2 inode for unified API access.
        /// </summary>
        private static Ufs2Inode ConvertUfs1ToUfs2Inode(Ufs1Inode ufs1)
        {
            var inode = new Ufs2Inode
            {
                Mode = ufs1.Mode,
                NLink = ufs1.NLink,
                Uid = ufs1.Uid,
                Gid = ufs1.Gid,
                Size = ufs1.Size,
                Blocks = ufs1.Blocks,
                AccessTime = ufs1.AccessTime,
                ModTime = ufs1.ModTime,
                ChangeTime = ufs1.ChangeTime,
                ATimeNsec = ufs1.ATimeNsec,
                MTimeNsec = ufs1.MTimeNsec,
                CTimeNsec = ufs1.CTimeNsec,
                Generation = ufs1.Generation,
                Flags = ufs1.Flags,
                DirDepth = ufs1.DirDepth
            };

            // Convert 32-bit block pointers to 64-bit
            for (int i = 0; i < Ufs2Constants.NDirect; i++)
                inode.DirectBlocks[i] = ufs1.DirectBlocks[i];
            for (int i = 0; i < Ufs2Constants.NIndirect; i++)
                inode.IndirectBlocks[i] = ufs1.IndirectBlocks[i];

            return inode;
        }

        /// <summary>
        /// List directory entries for the given directory inode.
        /// </summary>
        public List<Ufs2DirectoryEntry> ListDirectory(uint dirInodeNumber)
        {
            var inode = ReadInode(dirInodeNumber);
            if (!inode.IsDirectory)
                throw new InvalidOperationException($"Inode {dirInodeNumber} is not a directory.");

            var entries = new List<Ufs2DirectoryEntry>();
            long remaining = inode.Size;

            // Read through direct blocks
            for (int i = 0; i < Ufs2Constants.NDirect && remaining > 0; i++)
            {
                if (inode.DirectBlocks[i] == 0) continue;

                long blockOffset = inode.DirectBlocks[i] * Superblock.FSize;
                _stream.Position = blockOffset;

                long toRead = Math.Min(remaining, Superblock.BSize);
                long blockEnd = _stream.Position + toRead;

                while (_stream.Position < blockEnd)
                {
                    var entry = Ufs2DirectoryEntry.ReadFrom(_reader);
                    if (entry.RecordLength == 0) break; // Safety: prevent infinite loop
                    if (entry.Inode != 0)
                        entries.Add(entry);
                    remaining -= entry.RecordLength;
                }
            }

            // Handle indirect blocks for large directories
            if (remaining > 0 && inode.IndirectBlocks[0] != 0)
            {
                remaining = ReadDirectoryIndirectBlock(inode.IndirectBlocks[0], entries, remaining);
            }

            return entries;
        }

        /// <summary>
        /// Read directory entries from data blocks referenced by an indirect pointer block.
        /// </summary>
        private long ReadDirectoryIndirectBlock(long indirectBlockFrag,
            List<Ufs2DirectoryEntry> entries, long remaining)
        {
            long blockOffset = indirectBlockFrag * Superblock.FSize;
            _stream.Position = blockOffset;

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;

            for (int i = 0; i < pointersPerBlock && remaining > 0; i++)
            {
                _stream.Position = blockOffset + (long)i * ptrSize;
                long ptr = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
                if (ptr == 0) continue;

                long dataOffset = ptr * Superblock.FSize;
                _stream.Position = dataOffset;

                long toRead = Math.Min(remaining, Superblock.BSize);
                long blockEnd = _stream.Position + toRead;

                while (_stream.Position < blockEnd)
                {
                    var entry = Ufs2DirectoryEntry.ReadFrom(_reader);
                    if (entry.RecordLength == 0) break;
                    if (entry.Inode != 0)
                        entries.Add(entry);
                    remaining -= entry.RecordLength;
                }
            }

            return remaining;
        }

        /// <summary>
        /// List the root directory.
        /// </summary>
        public List<Ufs2DirectoryEntry> ListRoot()
        {
            return ListDirectory(Ufs2Constants.RootInode);
        }

        /// <summary>
        /// Read the contents of a regular file or symlink by inode number.
        /// </summary>
        public byte[] ReadFileBytes(uint inodeNumber)
        {
            var inode = ReadInode(inodeNumber);
            if (!inode.IsRegularFile && !inode.IsSymlink)
                throw new InvalidOperationException($"Inode {inodeNumber} is not a regular file or symlink.");

            if (inode.Size < 0)
                throw new InvalidDataException($"Inode {inodeNumber} has an invalid negative size ({inode.Size}).");

            if (inode.Size > int.MaxValue)
                throw new InvalidOperationException(
                    $"File too large to read into memory ({inode.Size:N0} bytes exceeds {int.MaxValue:N0} byte limit).");

            byte[] data = new byte[(int)inode.Size];
            using var ms = new MemoryStream(data, writable: true);
            ReadFileToStream(inodeNumber, inode, ms, bufferSize: 1024 * 1024);
            return data;
        }

        /// <summary>
        /// Read the entire contents of a regular file or symlink by inode number
        /// into <paramref name="destination"/> without materializing the whole file
        /// in memory.
        /// </summary>
        public void ReadFile(uint inodeNumber, Stream destination, int bufferSize = 1024 * 1024)
        {
            var inode = ReadInode(inodeNumber);
            ReadFileToStream(inodeNumber, inode, destination, bufferSize);
        }

        /// <summary>
        /// Read a byte range from a regular file or symlink by inode number into
        /// <paramref name="destination"/>. Returns the number of bytes copied.
        /// This is optimized for random-access callers such as Dokany ReadFile.
        /// </summary>
        public long ReadFile(uint inodeNumber, Stream destination, long fileOffset, long count, int bufferSize = 1024 * 1024)
        {
            var inode = ReadInode(inodeNumber);
            return ReadFileRangeToStream(inodeNumber, inode, destination, fileOffset, count, bufferSize);
        }

        private void ReadFileToStream(uint inodeNumber, Ufs2Inode inode, Stream destination, int bufferSize)
        {
            ReadFileRangeToStream(inodeNumber, inode, destination, fileOffset: 0, count: long.MaxValue, bufferSize);
        }

        private long ReadFileRangeToStream(uint inodeNumber, Ufs2Inode inode, Stream destination, long fileOffset, long count, int bufferSize)
        {
            if (destination is null)
                throw new ArgumentNullException(nameof(destination));

            if (!destination.CanWrite)
                throw new ArgumentException("Destination stream must be writable.", nameof(destination));

            if (!inode.IsRegularFile && !inode.IsSymlink)
                throw new InvalidOperationException($"Inode {inodeNumber} is not a regular file or symlink.");

            if (inode.Size < 0)
                throw new InvalidDataException($"Inode {inodeNumber} has an invalid negative size ({inode.Size}).");

            if (fileOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(fileOffset), "File offset must be non-negative.");

            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Read count must be non-negative.");

            if (count == 0 || fileOffset >= inode.Size)
                return 0;

            long remaining = Math.Min(count, inode.Size - fileOffset);
            long copied = 0;

            int actualBufferSize = bufferSize <= 0 ? 1024 * 1024 : bufferSize;
            actualBufferSize = Math.Min(actualBufferSize, Math.Max(1, Superblock.BSize));
            byte[] buffer = new byte[actualBufferSize];

            while (remaining > 0)
            {
                long logicalOffset = fileOffset + copied;
                long logicalBlockIndex = logicalOffset / Superblock.BSize;
                int offsetInBlock = (int)(logicalOffset % Superblock.BSize);
                long toCopy = Math.Min(remaining, Superblock.BSize - offsetInBlock);

                long dataBlockFrag = ResolveFileDataBlock(inode, logicalBlockIndex);
                if (dataBlockFrag == 0)
                {
                    // Write zeros to stream		
                    WriteZeroes(destination, toCopy, buffer);
                }
                else
                {
                    _stream.Position = dataBlockFrag * Superblock.FSize + offsetInBlock;
                    CopyFromImageTo(destination, toCopy, buffer);
                }

                copied += toCopy;
                remaining -= toCopy;
            }

            return copied;
        }

        private long ResolveFileDataBlock(Ufs2Inode inode, long logicalBlockIndex)
        {
            if (logicalBlockIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(logicalBlockIndex));

            // Direct blocks
            if (logicalBlockIndex < Ufs2Constants.NDirect)
                return inode.DirectBlocks[(int)logicalBlockIndex];
            logicalBlockIndex -= Ufs2Constants.NDirect;

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;
            if (pointersPerBlock <= 0)
                throw new InvalidDataException($"Invalid block size {Superblock.BSize} for pointer size {ptrSize}.");

            // Single indirect
            if (logicalBlockIndex < pointersPerBlock)
            {
                if (inode.IndirectBlocks[0] == 0)
                    return 0;

                return ReadBlockPointer(inode.IndirectBlocks[0], (int)logicalBlockIndex);
            }
            logicalBlockIndex -= pointersPerBlock;

            // Double indirect
            long doubleIndirectBlockCount = (long)pointersPerBlock * pointersPerBlock;
            if (logicalBlockIndex < doubleIndirectBlockCount)
            {
                if (inode.IndirectBlocks[1] == 0)
                    return 0;

                int firstIndex = (int)(logicalBlockIndex / pointersPerBlock);
                int secondIndex = (int)(logicalBlockIndex % pointersPerBlock);

                long singleIndirectFrag = ReadBlockPointer(inode.IndirectBlocks[1], firstIndex);
                if (singleIndirectFrag == 0)
                    return 0;

                return ReadBlockPointer(singleIndirectFrag, secondIndex);
            }
            logicalBlockIndex -= doubleIndirectBlockCount;

            // Triple indirect
            {
                if (inode.IndirectBlocks[2] == 0)
                    return 0;

                long doubleIndirectIndexSpan = (long)pointersPerBlock * pointersPerBlock;
                int outerIndex = (int)(logicalBlockIndex / doubleIndirectIndexSpan);
                if (outerIndex >= pointersPerBlock)
                    return 0;

                long innerIndex = logicalBlockIndex % doubleIndirectIndexSpan;
                int middleIndex = (int)(innerIndex / pointersPerBlock);
                int leafIndex = (int)(innerIndex % pointersPerBlock);

                long doubleIndirectFrag = ReadBlockPointer(inode.IndirectBlocks[2], outerIndex);
                if (doubleIndirectFrag == 0)
                    return 0;

                long singleIndirectFrag2 = ReadBlockPointer(doubleIndirectFrag, middleIndex);
                if (singleIndirectFrag2 == 0)
                    return 0;

                return ReadBlockPointer(singleIndirectFrag2, leafIndex);
            }
        }

        private long ReadBlockPointer(long pointerBlockFrag, int pointerIndex)
        {
            int ptrSize = Superblock.IsUfs1 ? 4 : 8; // UFS1: 32-bit, UFS2: 64-bit
            int pointersPerBlock = Superblock.BSize / ptrSize;
            if (pointerIndex < 0 || pointerIndex >= pointersPerBlock)
                throw new ArgumentOutOfRangeException(nameof(pointerIndex));

            _stream.Position = pointerBlockFrag * Superblock.FSize + (long)pointerIndex * ptrSize;
            return Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
        }

        private void CopyFromImageTo(Stream destination, long byteCount, byte[] buffer)
        {
            long remaining = byteCount;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                ReadFully(buffer, 0, toRead);
                destination.Write(buffer, 0, toRead);
                remaining -= toRead;
            }
        }

        private static void WriteZeroes(Stream destination, long byteCount, byte[] buffer)
        {
            Array.Clear(buffer, 0, buffer.Length);

            long remaining = byteCount;
            while (remaining > 0)
            {
                int toWrite = (int)Math.Min(buffer.Length, remaining);
                destination.Write(buffer, 0, toWrite);
                remaining -= toWrite;
            }
        }

        /// <summary>
        /// Write the current superblock back to the primary location in the image.
        /// The image must be opened in read-write mode.
        /// </summary>
        public void WriteSuperblock()
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot write superblock: image is opened read-only.");

            _stream.Position = Ufs2Constants.SuperblockOffset;
            Superblock.WriteTo(Writer);
            Writer.Flush();
        }

        /// <summary>
        /// Write the current superblock to all alternate (backup) locations
        /// in addition to the primary location.
        /// </summary>
        public void WriteSuperblockToAll()
        {
            WriteSuperblock();

            // Write to each backup location in CG 1..N-1
            for (int cg = 1; cg < Superblock.NumCylGroups; cg++)
            {
                long cgStartByte = (long)cg * Superblock.CylGroupSize * Superblock.FSize;
                long backupSbOffset = cgStartByte + (long)Superblock.SuperblockLocation * Superblock.FSize;

                int usableFragsInCg = Superblock.CylGroupSize;
                if (cg == Superblock.NumCylGroups - 1)
                    usableFragsInCg = (int)(Superblock.TotalBlocks - (long)cg * Superblock.CylGroupSize);

                if (backupSbOffset + Ufs2Constants.SuperblockSize <=
                    cgStartByte + (long)usableFragsInCg * Superblock.FSize)
                {
                    _stream.Position = backupSbOffset;
                    Superblock.WriteTo(Writer);
                }
            }

            Writer.Flush();
        }

        /// <summary>
        /// Get filesystem summary information.
        /// </summary>
        public string GetInfo()
        {
            string formatStr = Superblock.IsUfs1 ? "UFS1" : "UFS2";
            var flags = new System.Collections.Generic.List<string>();
            if ((Superblock.Flags & Ufs2Constants.FsDosoftdep) != 0) flags.Add("SOFTUPDATES");
            if ((Superblock.Flags & Ufs2Constants.FsSuj) != 0) flags.Add("SUJ");
            if ((Superblock.Flags & Ufs2Constants.FsGjournal) != 0) flags.Add("GJOURNAL");
            if ((Superblock.Flags & Ufs2Constants.FsMultilabel) != 0) flags.Add("MULTILABEL");
            if ((Superblock.Flags & Ufs2Constants.FsTrim) != 0) flags.Add("TRIM");
            string flagStr = flags.Count > 0 ? string.Join(", ", flags) : "none";

            string optimStr = Superblock.Optimization == Ufs2Constants.FsOptSpace ? "space" : "time";

            return $"""
                {formatStr} Filesystem Image: {ImagePath}
                Magic: 0x{Superblock.Magic:X8} ({(Superblock.IsValid ? "Valid " + formatStr : "INVALID")})
                Block Size: {Superblock.BSize} bytes
                Fragment Size: {Superblock.FSize} bytes
                Cylinder Groups: {Superblock.NumCylGroups}
                Inodes Per Group: {Superblock.InodesPerGroup}
                Total Fragments: {Superblock.TotalBlocks}
                Total Size: {Superblock.TotalBlocks * Superblock.FSize / (1024 * 1024)} MB
                Free Blocks: {Superblock.FreeBlocks}
                Free Inodes: {Superblock.FreeInodes}
                Directories: {Superblock.Directories}
                Volume Name: {Superblock.VolumeName}
                Min Free: {Superblock.MinFreePercent}%
                Optimization: {optimStr}
                Flags: {flagStr}
                Last Modified: {DateTimeOffset.FromUnixTimeSeconds(Superblock.Time):u}
                """;
        }

        /// <summary>
        /// Get detailed file/directory/symlink information (like Unix stat(1)).
        /// </summary>
        public string GetStat(string fsPath)
        {
            uint inodeNumber = ResolvePath(fsPath);
            Ufs2Inode inode = ReadInode(inodeNumber);

            string fileType = (inode.Mode & 0xF000) switch
            {
                Ufs2Constants.IfDir => "Directory",
                Ufs2Constants.IfReg => "Regular File",
                Ufs2Constants.IfLnk => "Symbolic Link",
                0x1000 => "FIFO",
                0x2000 => "Character Device",
                0x6000 => "Block Device",
                0xC000 => "Socket",
                _ => "Unknown"
            };

            ushort permBits = (ushort)(inode.Mode & 0x0FFF);
            string symbolicMode = FormatSymbolicMode(inode.Mode);

            string atime = inode.AccessTime != 0
                ? DateTimeOffset.FromUnixTimeSeconds(inode.AccessTime).ToString("u")
                : "-";
            string mtime = inode.ModTime != 0
                ? DateTimeOffset.FromUnixTimeSeconds(inode.ModTime).ToString("u")
                : "-";
            string ctime = inode.ChangeTime != 0
                ? DateTimeOffset.FromUnixTimeSeconds(inode.ChangeTime).ToString("u")
                : "-";
            string btime = inode.CreateTime != 0
                ? DateTimeOffset.FromUnixTimeSeconds(inode.CreateTime).ToString("u")
                : "-";

            int directBlocksUsed = 0;
            for (int i = 0; i < Ufs2Constants.NDirect; i++)
            {
                if (inode.DirectBlocks[i] != 0)
                    directBlocksUsed++;
            }

            int indirectBlocksUsed = 0;
            for (int i = 0; i < Ufs2Constants.NIndirect; i++)
            {
                if (inode.IndirectBlocks[i] != 0)
                    indirectBlocksUsed++;
            }

            return $"""
                  File: {fsPath}
                  Type: {fileType}
                 Inode: {inodeNumber}
                  Mode: ({Convert.ToString(permBits, 8).PadLeft(4, '0')}/{symbolicMode})
                 Links: {inode.NLink}
                   Uid: {inode.Uid}
                   Gid: {inode.Gid}
                  Size: {inode.Size}
                Blocks: {inode.Blocks}
                BlkSiz: {inode.BlkSize}
                Access: {atime}
                Modify: {mtime}
                Change: {ctime}
                 Birth: {btime}
                Direct: {directBlocksUsed}/{Ufs2Constants.NDirect} blocks
                Indrct: {indirectBlocksUsed}/{Ufs2Constants.NIndirect} blocks
                """;
        }

        /// <summary>
        /// Represents a single result from a filesystem find operation.
        /// </summary>
        public sealed class FindResult(string path, byte fileType, uint inode, long size)
        {
            /// <summary>Full path within the filesystem (e.g., "/dir/file.txt").</summary>
            public string Path { get; } = path;

            /// <summary>Directory entry file type constant (DtDir, DtReg, DtLnk, etc.).</summary>
            public byte FileType { get; } = fileType;

            /// <summary>Inode number of the entry.</summary>
            public uint Inode { get; } = inode;

            /// <summary>File size in bytes (from inode).</summary>
            public long Size { get; } = size;
        }

        /// <summary>
        /// Search for files and directories matching a name pattern within the filesystem.
        /// Supports glob-style wildcards: '*' matches any sequence of characters,
        /// '?' matches any single character.
        /// </summary>
        /// <param name="namePattern">Glob pattern to match entry names (e.g., "*.txt", "game*", "?ata").</param>
        /// <param name="startPath">Filesystem path to start searching from (default: "/").</param>
        /// <param name="typeFilter">
        /// Optional type filter: 'f' for regular files, 'd' for directories, 'l' for symlinks.
        /// Null or empty means all types.
        /// </param>
        /// <returns>List of matching entries with path, type, inode, and size.</returns>
        public List<FindResult> Find(string namePattern, string startPath = "/", string? typeFilter = null)
        {
            uint startInode = ResolvePath(startPath);
            var results = new List<FindResult>();
            FindRecursive(startInode, startPath == "/" ? "" : startPath, namePattern, typeFilter, results);
            return results;
        }

        /// <summary>
        /// Recursively walk the directory tree and collect entries matching the pattern and type filter.
        /// </summary>
        private void FindRecursive(uint dirInodeNumber, string currentPath, string namePattern, string? typeFilter, List<FindResult> results)
        {
            var entries = ListDirectory(dirInodeNumber);

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                string entryPath = currentPath + "/" + entry.Name;

                // Check if this entry matches the type filter and name pattern
                bool typeMatches = string.IsNullOrEmpty(typeFilter) || typeFilter switch
                {
                    "f" => entry.FileType == Ufs2Constants.DtReg,
                    "d" => entry.FileType == Ufs2Constants.DtDir,
                    "l" => entry.FileType == Ufs2Constants.DtLnk,
                    _ => true
                };

                if (typeMatches && GlobMatch(namePattern, entry.Name))
                {
                    var inode = ReadInode(entry.Inode);
                    results.Add(new FindResult(entryPath, entry.FileType, entry.Inode, inode.Size));
                }

                // Recurse into subdirectories
                if (entry.FileType == Ufs2Constants.DtDir)
                {
                    FindRecursive(entry.Inode, entryPath, namePattern, typeFilter, results);
                }
            }
        }

        /// <summary>
        /// Represents a single result from a filesystem disk usage calculation.
        /// </summary>
        public sealed class DiskUsageEntry(string path, long bytes, long blocks, bool isDirectory)
        {
            /// <summary>Full path within the filesystem (e.g., "/dir").</summary>
            public string Path { get; } = path;

            /// <summary>Logical size in bytes (sum of file sizes).</summary>
            public long Bytes { get; } = bytes;

            /// <summary>Disk blocks consumed (512-byte units, matching du(1) convention).</summary>
            public long Blocks { get; } = blocks;

            /// <summary>Whether this entry is a directory.</summary>
            public bool IsDirectory { get; } = isDirectory;
        }

        /// <summary>
        /// Calculate disk usage for a path within the filesystem, similar to du(1).
        /// Returns a list of entries with their accumulated sizes.
        /// </summary>
        /// <param name="fsPath">Filesystem path to measure (default: "/").</param>
        /// <param name="maxDepth">Maximum directory depth to report (-1 for unlimited).</param>
        /// <param name="summaryOnly">If true, only report the top-level total.</param>
        /// <returns>List of disk usage entries sorted by path.</returns>
        public List<DiskUsageEntry> DiskUsage(string fsPath = "/", int maxDepth = -1, bool summaryOnly = false)
        {
            uint startInode = ResolvePath(fsPath);
            Ufs2Inode inode = ReadInode(startInode);
            string basePath = fsPath == "/" ? "/" : fsPath.TrimEnd('/');

            var results = new List<DiskUsageEntry>();

            if (inode.IsDirectory)
            {
                DiskUsageRecursive(startInode, basePath, 0, maxDepth, summaryOnly, results);
            }
            else
            {
                // Single file: report its usage directly
                long blocks = inode.Blocks;
                results.Add(new DiskUsageEntry(basePath, inode.Size, blocks, false));
            }

            return results;
        }

        /// <summary>
        /// Recursively calculate disk usage for a directory tree.
        /// Returns the total (bytes, blocks) consumed by this directory and its contents.
        /// </summary>
        private (long bytes, long blocks) DiskUsageRecursive(
            uint dirInodeNumber, string currentPath, int currentDepth,
            int maxDepth, bool summaryOnly, List<DiskUsageEntry> results)
        {
            var entries = ListDirectory(dirInodeNumber);
            var dirInode = ReadInode(dirInodeNumber);

            // Start with the directory's own block usage
            long totalBytes = 0;
            long totalBlocks = dirInode.Blocks;

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                string entryPath = currentPath == "/" ? "/" + entry.Name : currentPath + "/" + entry.Name;
                var entryInode = ReadInode(entry.Inode);

                if (entry.FileType == Ufs2Constants.DtDir)
                {
                    var (childBytes, childBlocks) = DiskUsageRecursive(
                        entry.Inode, entryPath, currentDepth + 1,
                        maxDepth, summaryOnly, results);
                    totalBytes += childBytes;
                    totalBlocks += childBlocks;
                }
                else
                {
                    totalBytes += entryInode.Size;
                    totalBlocks += entryInode.Blocks;
                }
            }

            // Decide whether to add this directory to results
            if (summaryOnly)
            {
                // Only the top-level directory (depth 0) is added by the caller check below
                if (currentDepth == 0)
                    results.Add(new DiskUsageEntry(currentPath, totalBytes, totalBlocks, true));
            }
            else if (maxDepth < 0 || currentDepth <= maxDepth)
            {
                results.Add(new DiskUsageEntry(currentPath, totalBytes, totalBlocks, true));
            }

            return (totalBytes, totalBlocks);
        }

        /// <summary>
        /// Match a string against a glob pattern supporting '*' (any sequence) and '?' (single character).
        /// </summary>
        private static bool GlobMatch(string pattern, string text)
        {
            int pi = 0, ti = 0;
            int starPi = -1, starTi = -1;

            while (ti < text.Length)
            {
                if (pi < pattern.Length && (pattern[pi] == '?' || char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(text[ti])))
                {
                    pi++;
                    ti++;
                }
                else if (pi < pattern.Length && pattern[pi] == '*')
                {
                    starPi = pi;
                    starTi = ti;
                    pi++;
                }
                else if (starPi >= 0)
                {
                    pi = starPi + 1;
                    starTi++;
                    ti = starTi;
                }
                else
                {
                    return false;
                }
            }

            while (pi < pattern.Length && pattern[pi] == '*')
                pi++;

            return pi == pattern.Length;
        }

        /// <summary>
        /// Format an inode mode value as a symbolic permission string (e.g., "drwxr-xr-x").
        /// </summary>
        private static string FormatSymbolicMode(ushort mode)
        {
            char typeChar = (mode & 0xF000) switch
            {
                Ufs2Constants.IfDir => 'd',
                Ufs2Constants.IfReg => '-',
                Ufs2Constants.IfLnk => 'l',
                0x1000 => 'p',
                0x2000 => 'c',
                0x6000 => 'b',
                0xC000 => 's',
                _ => '?'
            };

            return string.Create(10, (typeChar, mode), static (span, state) =>
            {
                span[0] = state.typeChar;
                span[1] = (state.mode & 0x100) != 0 ? 'r' : '-';
                span[2] = (state.mode & 0x080) != 0 ? 'w' : '-';
                span[3] = (state.mode & 0x040) != 0 ? 'x' : '-';
                span[4] = (state.mode & 0x020) != 0 ? 'r' : '-';
                span[5] = (state.mode & 0x010) != 0 ? 'w' : '-';
                span[6] = (state.mode & 0x008) != 0 ? 'x' : '-';
                span[7] = (state.mode & 0x004) != 0 ? 'r' : '-';
                span[8] = (state.mode & 0x002) != 0 ? 'w' : '-';
                span[9] = (state.mode & 0x001) != 0 ? 'x' : '-';
            });
        }

        /// <summary>
        /// Resolve a filesystem path (e.g., "/subdir/file.txt") to its inode number.
        /// Returns the inode number, or throws if not found.
        /// </summary>
        public uint ResolvePath(string path)
        {
            // Normalize: strip leading/trailing slashes, handle empty = root
            path = path.Trim('/');
            if (string.IsNullOrEmpty(path))
                return Ufs2Constants.RootInode;

            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            uint currentInode = Ufs2Constants.RootInode;

            foreach (string part in parts)
            {
                var entries = ListDirectory(currentInode);
                var match = entries.Find(e => e.Name == part) ?? throw new FileNotFoundException($"Path component '{part}' not found in inode {currentInode}.");
                currentInode = match.Inode;
            }

            return currentInode;
        }

        /// <summary>
        /// Extract a single file from the filesystem to a host path.
        /// </summary>
        public void ExtractFile(uint inodeNumber, string outputPath)
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024);
            ReadFile(inodeNumber, output);
        }

        /// <summary>
        /// Recursively extract the contents of a directory inode to a host directory.
        /// </summary>
        public void ExtractToDirectory(uint dirInodeNumber, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            var entries = ListDirectory(dirInodeNumber);

            foreach (var entry in entries)
            {
                // Skip "." and ".." to avoid infinite recursion
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                string destPath = Path.Combine(outputDir, entry.Name);

                if (entry.FileType == Ufs2Constants.DtDir)
                {
                    ExtractToDirectory(entry.Inode, destPath);
                }
                else if (entry.FileType == Ufs2Constants.DtReg)
                {
                    ExtractFile(entry.Inode, destPath);
                }
                else if (entry.FileType == Ufs2Constants.DtLnk)
                {
                    // Read symlink target from inode data blocks
                    var inode = ReadInode(entry.Inode);
                    if (inode.Size > 0 && inode.Size < Superblock.BSize)
                    {
                        byte[] linkData = ReadFileBytes(entry.Inode);
                        string target = System.Text.Encoding.UTF8.GetString(linkData).TrimEnd('\0');
                        // Write symlink target as a text file (symlinks may not be supported on host OS)
                        File.WriteAllText(destPath, target);
                    }
                }
                // Other file types (DtFifo, DtChr, DtBlk, DtSock) are skipped
            }
        }

        /// <summary>
        /// Extract an item at the given filesystem path. If it is a directory,
        /// recursively extract all contents. If it is a file, extract it.
        /// </summary>
        public void Extract(string fsPath, string outputDir)
        {
            uint inodeNumber = ResolvePath(fsPath);
            var inode = ReadInode(inodeNumber);

            if (inode.IsDirectory)
            {
                ExtractToDirectory(inodeNumber, outputDir);
            }
            else if (inode.IsRegularFile)
            {
                string trimmedPath = fsPath.TrimEnd('/');
                string fileName = Path.GetFileName(trimmedPath);
                if (string.IsNullOrEmpty(fileName))
                    throw new InvalidOperationException("Cannot determine file name from the given path.");
                string destPath = Path.Combine(outputDir, fileName);
                Directory.CreateDirectory(outputDir);
                ExtractFile(inodeNumber, destPath);
            }
            else
            {
                throw new InvalidOperationException($"Cannot extract inode {inodeNumber}: unsupported file type (mode=0x{inode.Mode:X4}).");
            }
        }

        // --- Replace / Write support ---

        /// <summary>
        /// Write a UFS2 inode back to disk at the given inode number.
        /// For UFS1 images, the inode is converted from the unified UFS2 format.
        /// </summary>
        public void WriteInode(uint inodeNumber, Ufs2Inode inode)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot write inode: image is opened read-only.");

            long totalInodes = (long)Superblock.NumCylGroups * Superblock.InodesPerGroup;
            if (inodeNumber >= totalInodes)
                throw new ArgumentOutOfRangeException(nameof(inodeNumber),
                    $"Inode number {inodeNumber} exceeds total inodes ({totalInodes}).");

            int cgIndex = (int)(inodeNumber / Superblock.InodesPerGroup);
            int inodeIndexInCg = (int)(inodeNumber % Superblock.InodesPerGroup);

            int inodeSize = Superblock.IsUfs1
                ? Ufs2Constants.Ufs1InodeSize : Ufs2Constants.Ufs2InodeSize;

            long cgStart = (long)cgIndex * Superblock.CylGroupSize * Superblock.FSize;
            long inodeTableStart = cgStart + (long)Superblock.IblkNo * Superblock.FSize;
            long inodeOffset = inodeTableStart + (long)inodeIndexInCg * inodeSize;

            _stream.Position = inodeOffset;

            if (Superblock.IsUfs1)
            {
                var ufs1 = ConvertUfs2ToUfs1Inode(inode);
                ufs1.WriteTo(Writer);
            }
            else
            {
                inode.WriteTo(Writer);
            }
            Writer.Flush();
        }

        /// <summary>
        /// Convert a UFS2 inode back to UFS1 format for writing.
        /// </summary>
        private static Ufs1Inode ConvertUfs2ToUfs1Inode(Ufs2Inode ufs2)
        {
            // Validate that values fit within UFS1 32-bit limits
            if (ufs2.Blocks > int.MaxValue)
                throw new OverflowException(
                    $"Block count {ufs2.Blocks} exceeds UFS1 32-bit limit.");

            for (int i = 0; i < Ufs2Constants.NDirect; i++)
            {
                if (ufs2.DirectBlocks[i] > int.MaxValue)
                    throw new OverflowException(
                        $"Direct block pointer {ufs2.DirectBlocks[i]} exceeds UFS1 32-bit limit.");
            }
            for (int i = 0; i < Ufs2Constants.NIndirect; i++)
            {
                if (ufs2.IndirectBlocks[i] > int.MaxValue)
                    throw new OverflowException(
                        $"Indirect block pointer {ufs2.IndirectBlocks[i]} exceeds UFS1 32-bit limit.");
            }

            var inode = new Ufs1Inode
            {
                Mode = ufs2.Mode,
                NLink = ufs2.NLink,
                Uid = ufs2.Uid,
                Gid = ufs2.Gid,
                Size = ufs2.Size,
                Blocks = (int)ufs2.Blocks,
                AccessTime = (int)ufs2.AccessTime,
                ModTime = (int)ufs2.ModTime,
                ChangeTime = (int)ufs2.ChangeTime,
                ATimeNsec = ufs2.ATimeNsec,
                MTimeNsec = ufs2.MTimeNsec,
                CTimeNsec = ufs2.CTimeNsec,
                Generation = ufs2.Generation,
                Flags = ufs2.Flags,
                DirDepth = ufs2.DirDepth
            };

            for (int i = 0; i < Ufs2Constants.NDirect; i++)
                inode.DirectBlocks[i] = (int)ufs2.DirectBlocks[i];
            for (int i = 0; i < Ufs2Constants.NIndirect; i++)
                inode.IndirectBlocks[i] = (int)ufs2.IndirectBlocks[i];

            return inode;
        }

        /// <summary>
        /// Write new data to a file's existing data blocks, following the same
        /// block pointer structure (direct, single-indirect, double-indirect, triple-indirect).
        /// Returns the number of bytes written to existing blocks.
        /// </summary>
        private long WriteToExistingBlocks(Ufs2Inode inode, byte[] newData, long newSize)
        {
            long offset = 0;

            // Write to direct blocks
            for (int i = 0; i < Ufs2Constants.NDirect && offset < newSize; i++)
            {
                if (inode.DirectBlocks[i] == 0) break;

                long blockOffset = inode.DirectBlocks[i] * Superblock.FSize;
                _stream.Position = blockOffset;

                int toWrite = (int)Math.Min(Superblock.BSize, newSize - offset);
                byte[] writeBuffer = new byte[Superblock.BSize];
                Array.Copy(newData, offset, writeBuffer, 0, toWrite);
                _stream.Write(writeBuffer, 0, writeBuffer.Length);
                offset += toWrite;
            }

            // Write to single indirect blocks
            if (offset < newSize && inode.IndirectBlocks[0] != 0)
            {
                offset = WriteIndirectBlock(inode.IndirectBlocks[0], newData, offset, newSize);
            }

            // Write to double indirect blocks
            if (offset < newSize && inode.IndirectBlocks[1] != 0)
            {
                offset = WriteDoubleIndirectBlock(inode.IndirectBlocks[1], newData, offset, newSize);
            }

            // Write to triple indirect blocks
            if (offset < newSize && inode.IndirectBlocks[2] != 0)
            {
                offset = WriteTripleIndirectBlock(inode.IndirectBlocks[2], newData, offset, newSize);
            }

            return offset;
        }

        private long WriteIndirectBlock(long indirectBlockFrag, byte[] data, long offset, long dataSize)
        {
            long blockOffset = indirectBlockFrag * Superblock.FSize;
            _stream.Position = blockOffset;

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;

            // Read all pointers first
            long[] pointers = new long[pointersPerBlock];
            for (int i = 0; i < pointersPerBlock; i++)
                pointers[i] = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();

            for (int i = 0; i < pointersPerBlock && offset < dataSize; i++)
            {
                if (pointers[i] == 0) break;

                long dataOffset = pointers[i] * Superblock.FSize;
                _stream.Position = dataOffset;

                int toWrite = (int)Math.Min(Superblock.BSize, dataSize - offset);
                byte[] writeBuffer = new byte[Superblock.BSize];
                Array.Copy(data, offset, writeBuffer, 0, toWrite);
                _stream.Write(writeBuffer, 0, writeBuffer.Length);
                offset += toWrite;
            }

            return offset;
        }

        private long WriteDoubleIndirectBlock(long doubleIndirectBlockFrag, byte[] data, long offset, long dataSize)
        {
            long blockOffset = doubleIndirectBlockFrag * Superblock.FSize;
            _stream.Position = blockOffset;

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;

            long[] pointers = new long[pointersPerBlock];
            for (int i = 0; i < pointersPerBlock; i++)
                pointers[i] = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();

            for (int i = 0; i < pointersPerBlock && offset < dataSize; i++)
            {
                if (pointers[i] == 0) break;
                offset = WriteIndirectBlock(pointers[i], data, offset, dataSize);
            }

            return offset;
        }

        private long WriteTripleIndirectBlock(long tripleIndirectBlockFrag, byte[] data, long offset, long dataSize)
        {
            long blockOffset = tripleIndirectBlockFrag * Superblock.FSize;
            _stream.Position = blockOffset;

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;

            long[] pointers = new long[pointersPerBlock];
            for (int i = 0; i < pointersPerBlock; i++)
                pointers[i] = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();

            for (int i = 0; i < pointersPerBlock && offset < dataSize; i++)
            {
                if (pointers[i] == 0) break;
                offset = WriteDoubleIndirectBlock(pointers[i], data, offset, dataSize);
            }

            return offset;
        }

        /// <summary>
        /// Count the total number of data blocks allocated to a file (following all block pointers).
        /// </summary>
        private long CountAllocatedBlocks(Ufs2Inode inode)
        {
            long count = 0;

            for (int i = 0; i < Ufs2Constants.NDirect; i++)
            {
                if (inode.DirectBlocks[i] != 0) count++;
            }

            if (inode.IndirectBlocks[0] != 0)
                count += CountIndirectBlocks(inode.IndirectBlocks[0]);
            if (inode.IndirectBlocks[1] != 0)
                count += CountDoubleIndirectBlocks(inode.IndirectBlocks[1]);
            if (inode.IndirectBlocks[2] != 0)
                count += CountTripleIndirectBlocks(inode.IndirectBlocks[2]);

            return count;
        }

        private long CountIndirectBlocks(long indirectBlockFrag)
        {
            long blockOffset = indirectBlockFrag * Superblock.FSize;
            _stream.Position = blockOffset;

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;
            long count = 0;

            for (int i = 0; i < pointersPerBlock; i++)
            {
                long ptr = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
                if (ptr != 0) count++;
            }

            return count;
        }

        private long CountDoubleIndirectBlocks(long doubleIndirectBlockFrag)
        {
            long blockOffset = doubleIndirectBlockFrag * Superblock.FSize;
            _stream.Position = blockOffset;

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;
            long count = 0;

            long[] pointers = new long[pointersPerBlock];
            for (int i = 0; i < pointersPerBlock; i++)
                pointers[i] = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();

            for (int i = 0; i < pointersPerBlock; i++)
            {
                if (pointers[i] != 0)
                    count += CountIndirectBlocks(pointers[i]);
            }

            return count;
        }

        private long CountTripleIndirectBlocks(long tripleIndirectBlockFrag)
        {
            long blockOffset = tripleIndirectBlockFrag * Superblock.FSize;
            _stream.Position = blockOffset;

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;
            long count = 0;

            long[] pointers = new long[pointersPerBlock];
            for (int i = 0; i < pointersPerBlock; i++)
                pointers[i] = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();

            for (int i = 0; i < pointersPerBlock; i++)
            {
                if (pointers[i] != 0)
                    count += CountDoubleIndirectBlocks(pointers[i]);
            }

            return count;
        }

        /// <summary>
        /// Allocate a data block from a cylinder group's free fragment bitmap.
        /// Returns the absolute fragment number of the allocated block, or -1 if no space.
        /// </summary>
        private long AllocateBlock(int preferredCg = -1)
        {
            int fragsPerBlock = Superblock.FragsPerBlock;
            int fragsPerGroup = Superblock.CylGroupSize;
            int startCg = preferredCg >= 0 ? preferredCg : 0;

            for (int attempt = 0; attempt < Superblock.NumCylGroups; attempt++)
            {
                int cgIndex = (startCg + attempt) % Superblock.NumCylGroups;
                long cgStartFrag = (long)cgIndex * fragsPerGroup;
                long cgHeaderOffset = cgStartFrag * Superblock.FSize + (long)Superblock.CblkNo * Superblock.FSize;

                byte[] cgData = ReadCgBlock(cgHeaderOffset);
                int freeoff = ReadCgInt32(cgData, 0x60);
                int cgNdblk = ReadCgInt32(cgData, 0x14);
                int cgNbfree = ReadCgInt32(cgData, 0x1C);

                if (cgNbfree <= 0) continue;

                // Search for a free block (fragsPerBlock consecutive free fragments)
                int totalBlocks = cgNdblk / fragsPerBlock;
                for (int blk = Superblock.DblkNo / fragsPerBlock; blk < totalBlocks; blk++)
                {
                    int fragStart = blk * fragsPerBlock;
                    bool allFree = true;
                    for (int f = 0; f < fragsPerBlock; f++)
                    {
                        int byteIdx = freeoff + (fragStart + f) / 8;
                        int bitIdx = (fragStart + f) % 8;
                        if (byteIdx >= cgData.Length || (cgData[byteIdx] & (1 << bitIdx)) == 0)
                        {
                            allFree = false;
                            break;
                        }
                    }

                    if (allFree)
                    {
                        // Mark block as used
                        for (int f = 0; f < fragsPerBlock; f++)
                            ClearFragBit(cgData, freeoff, fragStart + f);

                        // Update CG free block count
                        WriteCgInt32(cgData, 0x1C, cgNbfree - 1);

                        // Update cluster bitmap
                        if (Superblock.ContigSumSize > 0)
                        {
                            ClearClusterBit(cgData, blk);
                            RecomputeClusterSummary(cgData, Superblock.ContigSumSize,
                                cgNdblk / fragsPerBlock);
                        }

                        // Write CG back
                        _stream.Position = cgHeaderOffset;
                        _stream.Write(cgData, 0, cgData.Length);

                        // Update superblock free counts
                        Superblock.FreeBlocks--;

                        return cgStartFrag + fragStart;
                    }
                }
            }

            return -1; // No free block found
        }

        /// <summary>
        /// Allocate a new indirect pointer block filled with the given data block pointers.
        /// Returns the fragment number of the allocated indirect block.
        /// </summary>
        private long AllocateIndirectBlock(long[] pointers, int count, int preferredCg = -1)
        {
            long indFrag = AllocateBlock(preferredCg);
            if (indFrag < 0)
                throw new InvalidOperationException("No free blocks available for indirect block allocation.");

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            byte[] indBlock = new byte[Superblock.BSize];
            using var ms = new MemoryStream(indBlock);
            using var w = new BinaryWriter(ms);
            for (int i = 0; i < count; i++)
            {
                if (Superblock.IsUfs1)
                    w.Write((int)pointers[i]);
                else
                    w.Write(pointers[i]);
            }

            _stream.Position = indFrag * Superblock.FSize;
            _stream.Write(indBlock, 0, indBlock.Length);

            return indFrag;
        }

        /// <summary>
        /// Replace the contents of a regular file at the given filesystem path.
        /// The new data is written to the file's existing blocks where possible;
        /// additional blocks are allocated from free space if the new data is larger.
        /// </summary>
        /// <param name="fsPath">Path to the file in the UFS filesystem (e.g., "/path/to/file.txt").</param>
        /// <param name="newData">The new file content.</param>
        public void ReplaceFileContent(string fsPath, byte[] newData)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot replace: image is opened read-only.");

            uint inodeNumber = ResolvePath(fsPath);
            var inode = ReadInode(inodeNumber);

            if (!inode.IsRegularFile)
                throw new InvalidOperationException($"Path '{fsPath}' is not a regular file.");

            long newSize = newData.Length;
            long existingBlocks = CountAllocatedBlocks(inode);
            long existingCapacity = existingBlocks * Superblock.BSize;
            long blocksNeeded = newSize == 0 ? 0 : (newSize + Superblock.BSize - 1) / Superblock.BSize;

            // Write data to existing blocks
            long written = 0;
            if (existingBlocks > 0 && newSize > 0)
            {
                long writeSize = Math.Min(newSize, existingCapacity);
                written = WriteToExistingBlocks(inode, newData, writeSize);
            }

            // If new data is larger, allocate additional blocks
            if (written < newSize)
            {
                long additionalBlocksNeeded = blocksNeeded - existingBlocks;
                int ptrSize = Superblock.IsUfs1 ? 4 : 8;
                int pointersPerBlock = Superblock.BSize / ptrSize;

                // Determine CG of the inode for preferred allocation
                int cgIndex = (int)(inodeNumber / Superblock.InodesPerGroup);

                // Find the next available slot in direct blocks
                int nextDirectSlot = 0;
                for (int i = 0; i < Ufs2Constants.NDirect; i++)
                {
                    if (inode.DirectBlocks[i] == 0) { nextDirectSlot = i; break; }
                    if (i == Ufs2Constants.NDirect - 1) nextDirectSlot = Ufs2Constants.NDirect;
                }

                long offset = written;

                // Allocate additional direct blocks
                while (nextDirectSlot < Ufs2Constants.NDirect && offset < newSize)
                {
                    long newBlock = AllocateBlock(cgIndex);
                    if (newBlock < 0)
                        throw new InvalidOperationException("No free blocks available for file replacement.");

                    inode.DirectBlocks[nextDirectSlot] = newBlock;
                    nextDirectSlot++;

                    // Write data
                    int toWrite = (int)Math.Min(Superblock.BSize, newSize - offset);
                    byte[] writeBuffer = new byte[Superblock.BSize];
                    Array.Copy(newData, offset, writeBuffer, 0, toWrite);
                    _stream.Position = newBlock * Superblock.FSize;
                    _stream.Write(writeBuffer, 0, writeBuffer.Length);
                    offset += toWrite;
                }

                // Allocate single-indirect block if needed
                if (offset < newSize && inode.IndirectBlocks[0] == 0)
                {
                    int count = (int)Math.Min(
                        (newSize - offset + Superblock.BSize - 1) / Superblock.BSize,
                        pointersPerBlock);

                    long[] dataBlocks = new long[count];
                    for (int i = 0; i < count && offset < newSize; i++)
                    {
                        long newBlock = AllocateBlock(cgIndex);
                        if (newBlock < 0)
                            throw new InvalidOperationException("No free blocks available for file replacement.");

                        dataBlocks[i] = newBlock;

                        int toWrite = (int)Math.Min(Superblock.BSize, newSize - offset);
                        byte[] writeBuffer = new byte[Superblock.BSize];
                        Array.Copy(newData, offset, writeBuffer, 0, toWrite);
                        _stream.Position = newBlock * Superblock.FSize;
                        _stream.Write(writeBuffer, 0, writeBuffer.Length);
                        offset += toWrite;
                    }

                    inode.IndirectBlocks[0] = AllocateIndirectBlock(dataBlocks, count, cgIndex);
                }

                // Allocate double-indirect block if needed
                if (offset < newSize && inode.IndirectBlocks[1] == 0)
                {
                    int remaining = (int)((newSize - offset + Superblock.BSize - 1) / Superblock.BSize);
                    int sindCount = (remaining + pointersPerBlock - 1) / pointersPerBlock;
                    long[] sindPointers = new long[sindCount];

                    for (int s = 0; s < sindCount && offset < newSize; s++)
                    {
                        int count = Math.Min(remaining, pointersPerBlock);
                        long[] dataBlocks = new long[count];

                        for (int i = 0; i < count && offset < newSize; i++)
                        {
                            long newBlock = AllocateBlock(cgIndex);
                            if (newBlock < 0)
                                throw new InvalidOperationException("No free blocks available for file replacement.");

                            dataBlocks[i] = newBlock;

                            int toWrite = (int)Math.Min(Superblock.BSize, newSize - offset);
                            byte[] writeBuffer = new byte[Superblock.BSize];
                            Array.Copy(newData, offset, writeBuffer, 0, toWrite);
                            _stream.Position = newBlock * Superblock.FSize;
                            _stream.Write(writeBuffer, 0, writeBuffer.Length);
                            offset += toWrite;
                        }

                        sindPointers[s] = AllocateIndirectBlock(dataBlocks, count, cgIndex);
                        remaining -= count;
                    }

                    inode.IndirectBlocks[1] = AllocateIndirectBlock(sindPointers, sindCount, cgIndex);
                }
            }

            // Update inode metadata
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            inode.Size = newSize;
            inode.ModTime = now;
            inode.ChangeTime = now;

            // Calculate block count (in 512-byte sectors)
            long dataFrags = blocksNeeded * Superblock.FragsPerBlock;
            inode.Blocks = dataFrags * (Superblock.FSize / 512);
            // Add metadata blocks (indirect pointers)
            if (inode.IndirectBlocks[0] != 0)
                inode.Blocks += Superblock.FragsPerBlock * (Superblock.FSize / 512);
            if (inode.IndirectBlocks[1] != 0)
            {
                // Double indirect: 1 dind block + N sind blocks
                int ptrSize = Superblock.IsUfs1 ? 4 : 8;
                int pointersPerBlock = Superblock.BSize / ptrSize;
                long sindBlocks = (blocksNeeded - Ufs2Constants.NDirect - pointersPerBlock
                    + pointersPerBlock - 1) / pointersPerBlock;
                if (sindBlocks < 0) sindBlocks = 0;
                inode.Blocks += (1 + sindBlocks) * Superblock.FragsPerBlock * (Superblock.FSize / 512);
            }

            WriteInode(inodeNumber, inode);

            // Update and write superblock
            Superblock.Time = now;
            WriteSuperblock();
            _stream.Flush();
        }

        /// <summary>
        /// Replace a file or directory in the filesystem. If the target is a file,
        /// its content is replaced with the source file. If the target is a directory,
        /// files in the source directory replace matching files in the target directory.
        /// </summary>
        /// <param name="fsPath">Path in the UFS filesystem to replace.</param>
        /// <param name="sourcePath">Path on the host filesystem to read replacement data from.</param>
        public void Replace(string fsPath, string sourcePath)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot replace: image is opened read-only.");

            uint inodeNumber = ResolvePath(fsPath);
            var inode = ReadInode(inodeNumber);

            if (inode.IsRegularFile)
            {
                byte[] newData = File.ReadAllBytes(sourcePath);
                ReplaceFileContent(fsPath, newData);
            }
            else if (inode.IsDirectory)
            {
                if (!Directory.Exists(sourcePath))
                    throw new DirectoryNotFoundException(
                        $"Source directory '{sourcePath}' not found.");

                ReplaceDirectoryContents(fsPath, sourcePath, inodeNumber);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cannot replace: unsupported file type at '{fsPath}' (mode=0x{inode.Mode:X4}).");
            }
        }

        /// <summary>
        /// Recursively replace files in a UFS directory with files from a host directory.
        /// Only files that exist in both the UFS directory and the source directory are replaced.
        /// </summary>
        private void ReplaceDirectoryContents(string fsDir, string sourceDir, uint dirInodeNumber)
        {
            var entries = ListDirectory(dirInodeNumber);

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                string fsEntryPath = fsDir.TrimEnd('/') + "/" + entry.Name;
                string sourceEntryPath = Path.Combine(sourceDir, entry.Name);

                if (entry.FileType == Ufs2Constants.DtReg && File.Exists(sourceEntryPath))
                {
                    byte[] newData = File.ReadAllBytes(sourceEntryPath);
                    ReplaceFileContent(fsEntryPath, newData);
                }
                else if (entry.FileType == Ufs2Constants.DtDir && Directory.Exists(sourceEntryPath))
                {
                    ReplaceDirectoryContents(fsEntryPath, sourceEntryPath, entry.Inode);
                }
            }
        }

        // --- Add / Delete support ---

        /// <summary>
        /// Allocate a free inode from a cylinder group's inode bitmap.
        /// Returns the global inode number, or -1 if no free inode found.
        /// </summary>
        private long AllocateInode(int preferredCg = 0)
        {
            int inodesPerGroup = Superblock.InodesPerGroup;
            int fragsPerGroup = Superblock.CylGroupSize;

            for (int attempt = 0; attempt < Superblock.NumCylGroups; attempt++)
            {
                int cgIndex = (preferredCg + attempt) % Superblock.NumCylGroups;
                long cgStartFrag = (long)cgIndex * fragsPerGroup;
                long cgHeaderOffset = cgStartFrag * Superblock.FSize + (long)Superblock.CblkNo * Superblock.FSize;

                byte[] cgData = ReadCgBlock(cgHeaderOffset);
                int iusedoff = ReadCgInt32(cgData, 0x5C);
                int cgNifree = ReadCgInt32(cgData, 0x20);

                if (cgNifree <= 0) continue;

                for (int i = 0; i < inodesPerGroup; i++)
                {
                    uint globalIno = (uint)((long)cgIndex * inodesPerGroup + i);
                    // Skip reserved inodes 0 and 1
                    if (globalIno <= 1) continue;
                    // Skip root inode (inode 2 in CG 0)
                    if (cgIndex == 0 && globalIno == Ufs2Constants.RootInode) continue;

                    int byteIdx = iusedoff + i / 8;
                    int bitIdx = i % 8;
                    if (byteIdx >= cgData.Length) break;

                    // Bit 0 = free, bit 1 = used
                    if ((cgData[byteIdx] & (1 << bitIdx)) == 0)
                    {
                        // Mark as used
                        cgData[byteIdx] |= (byte)(1 << bitIdx);

                        // Update CG free inode count
                        WriteCgInt32(cgData, 0x20, cgNifree - 1);

                        // Write CG back
                        _stream.Position = cgHeaderOffset;
                        _stream.Write(cgData, 0, cgData.Length);

                        // Update superblock
                        Superblock.FreeInodes--;

                        return globalIno;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Free an inode by clearing its bit in the CG inode bitmap and zeroing it on disk.
        /// </summary>
        private void FreeInode(uint inodeNumber)
        {
            int inodesPerGroup = Superblock.InodesPerGroup;
            int fragsPerGroup = Superblock.CylGroupSize;
            int cgIndex = (int)(inodeNumber / inodesPerGroup);
            int localIndex = (int)(inodeNumber % inodesPerGroup);

            long cgStartFrag = (long)cgIndex * fragsPerGroup;
            long cgHeaderOffset = cgStartFrag * Superblock.FSize + (long)Superblock.CblkNo * Superblock.FSize;

            byte[] cgData = ReadCgBlock(cgHeaderOffset);
            int iusedoff = ReadCgInt32(cgData, 0x5C);

            // Read inode to check if it was a directory
            var inode = ReadInode(inodeNumber);
            bool wasDirectory = inode.IsDirectory;

            // Clear the bit (mark as free)
            int byteIdx = iusedoff + localIndex / 8;
            int bitIdx = localIndex % 8;
            if (byteIdx < cgData.Length)
                cgData[byteIdx] &= (byte)~(1 << bitIdx);

            // Zero out the inode on disk
            var zeroInode = new Ufs2Inode();
            WriteInode(inodeNumber, zeroInode);

            // Update CG counters
            int cgNifree = ReadCgInt32(cgData, 0x20);
            WriteCgInt32(cgData, 0x20, cgNifree + 1);

            if (wasDirectory)
            {
                int cgNdir = ReadCgInt32(cgData, 0x18);
                WriteCgInt32(cgData, 0x18, cgNdir - 1);
            }

            // Write CG back
            _stream.Position = cgHeaderOffset;
            _stream.Write(cgData, 0, cgData.Length);

            // Update superblock
            Superblock.FreeInodes++;
            if (wasDirectory)
                Superblock.Directories--;
        }

        /// <summary>
        /// Free a full block (all fragments) by marking them as free in the CG fragment bitmap.
        /// </summary>
        private void FreeBlock(long fragNumber)
        {
            int fragsPerBlock = Superblock.FragsPerBlock;
            int fragsPerGroup = Superblock.CylGroupSize;
            int cgIndex = (int)(fragNumber / fragsPerGroup);
            int localFrag = (int)(fragNumber % fragsPerGroup);

            long cgStartFrag = (long)cgIndex * fragsPerGroup;
            long cgHeaderOffset = cgStartFrag * Superblock.FSize + (long)Superblock.CblkNo * Superblock.FSize;

            byte[] cgData = ReadCgBlock(cgHeaderOffset);
            int freeoff = ReadCgInt32(cgData, 0x60);

            // Mark all frags in the block as free (set bits)
            for (int f = 0; f < fragsPerBlock; f++)
                SetFragBit(cgData, freeoff, localFrag + f);

            // Update CG free block count
            int cgNbfree = ReadCgInt32(cgData, 0x1C);
            WriteCgInt32(cgData, 0x1C, cgNbfree + 1);

            // Update cluster bitmap if ContigSumSize > 0
            if (Superblock.ContigSumSize > 0)
            {
                int blockIndex = localFrag / fragsPerBlock;
                SetClusterBit(cgData, blockIndex);
                int cgNdblk = ReadCgInt32(cgData, 0x14);
                RecomputeClusterSummary(cgData, Superblock.ContigSumSize,
                    cgNdblk / fragsPerBlock);
            }

            // Write CG back
            _stream.Position = cgHeaderOffset;
            _stream.Write(cgData, 0, cgData.Length);

            // Update superblock
            Superblock.FreeBlocks++;
        }

        /// <summary>
        /// Free all data blocks held by an inode (direct, single/double/triple indirect).
        /// </summary>
        private void FreeFileBlocks(Ufs2Inode inode)
        {
            // Free direct blocks
            for (int i = 0; i < Ufs2Constants.NDirect; i++)
            {
                if (inode.DirectBlocks[i] != 0)
                {
                    FreeBlock(inode.DirectBlocks[i]);
                    inode.DirectBlocks[i] = 0;
                }
            }

            // Free single-indirect
            if (inode.IndirectBlocks[0] != 0)
            {
                FreeIndirectBlockChain(inode.IndirectBlocks[0], 1);
                inode.IndirectBlocks[0] = 0;
            }

            // Free double-indirect
            if (inode.IndirectBlocks[1] != 0)
            {
                FreeIndirectBlockChain(inode.IndirectBlocks[1], 2);
                inode.IndirectBlocks[1] = 0;
            }

            // Free triple-indirect
            if (inode.IndirectBlocks[2] != 0)
            {
                FreeIndirectBlockChain(inode.IndirectBlocks[2], 3);
                inode.IndirectBlocks[2] = 0;
            }
        }

        /// <summary>
        /// Recursively free an indirect block chain at the given depth
        /// (1=single, 2=double, 3=triple).
        /// </summary>
        private void FreeIndirectBlockChain(long indirectFrag, int depth)
        {
            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;

            long blockOffset = indirectFrag * Superblock.FSize;
            _stream.Position = blockOffset;

            long[] pointers = new long[pointersPerBlock];
            for (int i = 0; i < pointersPerBlock; i++)
                pointers[i] = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();

            for (int i = 0; i < pointersPerBlock; i++)
            {
                if (pointers[i] == 0) continue;

                if (depth == 1)
                    FreeBlock(pointers[i]);
                else
                    FreeIndirectBlockChain(pointers[i], depth - 1);
            }

            // Free the indirect block itself
            FreeBlock(indirectFrag);
        }

        /// <summary>
        /// Add a directory entry to a directory inode.
        /// </summary>
        private void AddDirectoryEntry(uint dirInodeNumber, string name, uint entryInodeNumber, byte fileType)
        {
            var dirInode = ReadInode(dirInodeNumber);
            if (!dirInode.IsDirectory)
                throw new InvalidOperationException($"Inode {dirInodeNumber} is not a directory.");

            ushort newEntryMinLen = Ufs2DirectoryEntry.CalculateRecordLength(name.Length, Superblock.IsUfs2);

            // Find the last data block of the directory
            long dirSize = dirInode.Size;
            int blockSize = Superblock.BSize;

            if (dirSize <= 0)
                throw new InvalidOperationException($"Directory inode {dirInodeNumber} has no data (size={dirSize}).");

            int lastBlockIndex = (int)((dirSize - 1) / blockSize);

            long lastBlockFrag = 0;
            if (lastBlockIndex < Ufs2Constants.NDirect)
            {
                lastBlockFrag = dirInode.DirectBlocks[lastBlockIndex];
            }
            else
            {
                // Read from indirect blocks
                lastBlockFrag = ReadBlockPointerAt(dirInode, lastBlockIndex);
            }

            if (lastBlockFrag == 0)
                throw new InvalidOperationException("Directory has no data blocks.");

            long lastBlockOffset = lastBlockFrag * Superblock.FSize;
            byte[] blockData = new byte[blockSize];
            _stream.Position = lastBlockOffset;
            ReadFully(blockData, 0, blockSize);

            // Try to find space in the last DIRBLKSIZ-sized region of the last block
            int dirBlkSize = Ufs2Constants.DirBlockSize;
            long bytesInLastBlock = dirSize - (long)lastBlockIndex * blockSize;
            if (bytesInLastBlock <= 0) bytesInLastBlock = blockSize;

            // Scan the last DIRBLKSIZ region in the last block
            int lastDirBlkStart = ((int)bytesInLastBlock - 1) / dirBlkSize * dirBlkSize;
            int pos = lastDirBlkStart;
            int prevPos = -1;
            int prevRecLen = 0;

            while (pos < lastDirBlkStart + dirBlkSize && pos < blockSize)
            {
                using var ms = new MemoryStream(blockData, pos, blockData.Length - pos);
                using var r = new BinaryReader(ms);
                var entry = Ufs2DirectoryEntry.ReadFrom(r);
                if (entry.RecordLength == 0) break;

                int nextPos = pos + entry.RecordLength;
                if (nextPos >= lastDirBlkStart + dirBlkSize || nextPos >= (int)bytesInLastBlock)
                {
                    // This is the last entry in this DIRBLKSIZ region
                    ushort actualLen = Ufs2DirectoryEntry.CalculateRecordLength(entry.NameLength, Superblock.IsUfs2);
                    int slack = entry.RecordLength - actualLen;

                    if (slack >= newEntryMinLen)
                    {
                        // Split: shrink current entry, add new entry in slack
                        entry.RecordLength = actualLen;

                        // Write shrunk entry
                        using (var wms = new MemoryStream(blockData, pos, actualLen))
                        using (var w = new BinaryWriter(wms))
                        {
                            entry.WriteTo(w);
                        }

                        // Write new entry
                        var newEntry = new Ufs2DirectoryEntry
                        {
                            Inode = entryInodeNumber,
                            RecordLength = (ushort)slack,
                            FileType = fileType,
                            NameLength = (byte)name.Length,
                            Name = name
                        };

                        using (var wms = new MemoryStream(blockData, pos + actualLen, slack))
                        using (var w = new BinaryWriter(wms))
                        {
                            newEntry.WriteTo(w);
                        }

                        // Write block back
                        _stream.Position = lastBlockOffset;
                        _stream.Write(blockData, 0, blockSize);
                        return;
                    }

                    break;
                }

                prevPos = pos;
                prevRecLen = entry.RecordLength;
                pos = nextPos;
            }

            // No space in last DIRBLKSIZ region.
            // Check if the current block has room for another DIRBLKSIZ region.
            if (bytesInLastBlock > 0 && bytesInLastBlock + dirBlkSize <= blockSize)
            {
                // Grow within existing block: initialize next DIRBLKSIZ region
                int newRegionStart = (int)bytesInLastBlock;
                var newEntry = new Ufs2DirectoryEntry
                {
                    Inode = entryInodeNumber,
                    RecordLength = (ushort)dirBlkSize,
                    FileType = fileType,
                    NameLength = (byte)name.Length,
                    Name = name
                };
                using (var wms = new MemoryStream(blockData, newRegionStart, dirBlkSize))
                using (var w = new BinaryWriter(wms))
                {
                    newEntry.WriteTo(w);
                }

                _stream.Position = lastBlockOffset;
                _stream.Write(blockData, 0, blockSize);

                dirInode.Size += dirBlkSize;
                dirInode.ModTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                dirInode.ChangeTime = dirInode.ModTime;
                WriteInode(dirInodeNumber, dirInode);
                return;
            }

            // No space in existing blocks — allocate a new block
            int cgIndex = (int)(dirInodeNumber / Superblock.InodesPerGroup);
            long newBlockFrag = AllocateBlock(cgIndex);
            if (newBlockFrag < 0)
                throw new InvalidOperationException("No free blocks available for directory expansion.");

            // Write the new entry as the sole entry in the first DIRBLKSIZ region
            byte[] newBlockData = new byte[blockSize];
            for (int db = 0; db < blockSize; db += dirBlkSize)
            {
                if (db == 0)
                {
                    // First DIRBLKSIZ has our new entry
                    var newEntry = new Ufs2DirectoryEntry
                    {
                        Inode = entryInodeNumber,
                        RecordLength = (ushort)dirBlkSize,
                        FileType = fileType,
                        NameLength = (byte)name.Length,
                        Name = name
                    };
                    using var wms = new MemoryStream(newBlockData, 0, dirBlkSize);
                    using var w = new BinaryWriter(wms);
                    newEntry.WriteTo(w);
                }
                // Remaining DIRBLKSIZ regions stay zeroed
            }

            _stream.Position = newBlockFrag * Superblock.FSize;
            _stream.Write(newBlockData, 0, blockSize);

            // Update directory inode: add block pointer, update size and blocks
            int nextSlot = (int)(dirInode.Size / blockSize);
            if (nextSlot < Ufs2Constants.NDirect)
            {
                dirInode.DirectBlocks[nextSlot] = newBlockFrag;
            }
            else
            {
                // Need indirect block support for large directories
                int indirectIndex = nextSlot - Ufs2Constants.NDirect;
                int ptrSize = Superblock.IsUfs1 ? 4 : 8;
                int pointersPerBlock = Superblock.BSize / ptrSize;

                if (indirectIndex < pointersPerBlock)
                {
                    if (dirInode.IndirectBlocks[0] == 0)
                    {
                        long[] ptrs = [newBlockFrag];
                        dirInode.IndirectBlocks[0] = AllocateIndirectBlock(ptrs, 1, cgIndex);
                    }
                    else
                    {
                        // Write pointer into existing indirect block
                        long indBlockOffset = dirInode.IndirectBlocks[0] * Superblock.FSize;
                        _stream.Position = indBlockOffset + (long)indirectIndex * ptrSize;
                        if (Superblock.IsUfs1)
                            Writer.Write((int)newBlockFrag);
                        else
                            Writer.Write(newBlockFrag);
                    }
                }
            }

            dirInode.Size += blockSize;
            dirInode.Blocks += Superblock.FragsPerBlock * (Superblock.FSize / 512);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            dirInode.ModTime = now;
            dirInode.ChangeTime = now;
            WriteInode(dirInodeNumber, dirInode);
        }

        /// <summary>
        /// Read a block pointer at a given logical block index from an inode
        /// (supports direct, single-indirect, double-indirect, and triple-indirect).
        /// </summary>
        private long ReadBlockPointerAt(Ufs2Inode inode, int blockIndex)
        {
            if (blockIndex < Ufs2Constants.NDirect)
                return inode.DirectBlocks[blockIndex];

            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;
            int remaining = blockIndex - Ufs2Constants.NDirect;

            // Single indirect
            if (remaining < pointersPerBlock)
            {
                if (inode.IndirectBlocks[0] == 0) return 0;
                long indBlockOffset = inode.IndirectBlocks[0] * Superblock.FSize;
                _stream.Position = indBlockOffset + (long)remaining * ptrSize;
                return Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
            }
            remaining -= pointersPerBlock;

            // Double indirect
            long ppb = (long)pointersPerBlock * pointersPerBlock;
            if (remaining < ppb)
            {
                if (inode.IndirectBlocks[1] == 0) return 0;
                int idx1 = remaining / pointersPerBlock;
                int idx2 = remaining % pointersPerBlock;

                long dindBlockOffset = inode.IndirectBlocks[1] * Superblock.FSize;
                _stream.Position = dindBlockOffset + (long)idx1 * ptrSize;
                long indPtr = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
                if (indPtr == 0) return 0;

                _stream.Position = indPtr * Superblock.FSize + (long)idx2 * ptrSize;
                return Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
            }
            remaining -= (int)ppb;

            // Triple indirect
            if (inode.IndirectBlocks[2] == 0) return 0;
            int tIdx1 = (int)(remaining / ppb);
            int tRemainder = (int)(remaining % ppb);
            int tIdx2 = tRemainder / pointersPerBlock;
            int tIdx3 = tRemainder % pointersPerBlock;

            long tindBlockOffset = inode.IndirectBlocks[2] * Superblock.FSize;
            _stream.Position = tindBlockOffset + (long)tIdx1 * ptrSize;
            long dindPtr = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
            if (dindPtr == 0) return 0;

            _stream.Position = dindPtr * Superblock.FSize + (long)tIdx2 * ptrSize;
            long indPtr2 = Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
            if (indPtr2 == 0) return 0;

            _stream.Position = indPtr2 * Superblock.FSize + (long)tIdx3 * ptrSize;
            return Superblock.IsUfs1 ? _reader.ReadInt32() : _reader.ReadInt64();
        }

        /// <summary>
        /// Remove a directory entry by name from a directory inode.
        /// </summary>
        private void RemoveDirectoryEntry(uint dirInodeNumber, string name)
        {
            var dirInode = ReadInode(dirInodeNumber);
            if (!dirInode.IsDirectory)
                throw new InvalidOperationException($"Inode {dirInodeNumber} is not a directory.");

            long dirSize = dirInode.Size;
            int blockSize = Superblock.BSize;
            int dirBlkSize = Ufs2Constants.DirBlockSize;
            int numBlocks = (int)((dirSize + blockSize - 1) / blockSize);

            for (int bi = 0; bi < numBlocks; bi++)
            {
                long blockFrag;
                if (bi < Ufs2Constants.NDirect)
                    blockFrag = dirInode.DirectBlocks[bi];
                else
                    blockFrag = ReadBlockPointerAt(dirInode, bi);

                if (blockFrag == 0) continue;

                long blockOffset = blockFrag * Superblock.FSize;
                byte[] blockData = new byte[blockSize];
                _stream.Position = blockOffset;
                ReadFully(blockData, 0, blockSize);

                long bytesInBlock = Math.Min(blockSize, dirSize - (long)bi * blockSize);

                // Scan through DIRBLKSIZ regions
                for (int dbStart = 0; dbStart < (int)bytesInBlock; dbStart += dirBlkSize)
                {
                    int pos = dbStart;
                    int prevEntryPos = -1;

                    while (pos < dbStart + dirBlkSize && pos < (int)bytesInBlock)
                    {
                        using var ms = new MemoryStream(blockData, pos, blockData.Length - pos);
                        using var r = new BinaryReader(ms);
                        var entry = Ufs2DirectoryEntry.ReadFrom(r);
                        if (entry.RecordLength == 0) break;

                        if (entry.Inode != 0 && entry.Name == name)
                        {
                            if (prevEntryPos >= 0)
                            {
                                // Merge into previous entry
                                ushort prevRecLen = BitConverter.ToUInt16(blockData, prevEntryPos + 4);
                                int mergedLen = prevRecLen + entry.RecordLength;
                                if (mergedLen > ushort.MaxValue)
                                    throw new InvalidOperationException("Merged record length exceeds maximum.");
                                ushort newRecLen = (ushort)mergedLen;
                                byte[] recLenBytes = BitConverter.GetBytes(newRecLen);
                                blockData[prevEntryPos + 4] = recLenBytes[0];
                                blockData[prevEntryPos + 5] = recLenBytes[1];
                            }
                            else
                            {
                                // First entry in DIRBLKSIZ region: zero the inode number
                                blockData[pos] = 0;
                                blockData[pos + 1] = 0;
                                blockData[pos + 2] = 0;
                                blockData[pos + 3] = 0;
                            }

                            // Write block back
                            _stream.Position = blockOffset;
                            _stream.Write(blockData, 0, blockSize);
                            return;
                        }

                        if (entry.Inode != 0)
                            prevEntryPos = pos;

                        pos += entry.RecordLength;
                    }
                }
            }

            throw new FileNotFoundException($"Entry '{name}' not found in directory inode {dirInodeNumber}.");
        }

        /// <summary>
        /// Add a file from the local filesystem into the UFS image.
        /// </summary>
        public void AddFile(string parentFsPath, string localFilePath, string? fsName = null)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot add file: image is opened read-only.");

            string fileName = fsName ?? Path.GetFileName(localFilePath);
            uint parentInode = ResolvePath(parentFsPath);
            int cgIndex = (int)(parentInode / Superblock.InodesPerGroup);

            // Allocate inode
            long newIno = AllocateInode(cgIndex);
            if (newIno < 0)
                throw new InvalidOperationException("No free inodes available.");

            byte[] fileData = File.ReadAllBytes(localFilePath);
            long fileSize = fileData.Length;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var inode = new Ufs2Inode
            {
                Mode = Ufs2Constants.IfReg | 0x1A4, // rw-r--r--
                NLink = 1,
                Size = fileSize,
                BlkSize = (uint)Superblock.BSize,
                AccessTime = now,
                ModTime = now,
                ChangeTime = now,
                CreateTime = now,
                Generation = Random.Shared.Next(1, int.MaxValue)
            };

            // Allocate blocks and write data
            long offset = 0;
            int nextDirect = 0;
            int ptrSize = Superblock.IsUfs1 ? 4 : 8;
            int pointersPerBlock = Superblock.BSize / ptrSize;

            // Allocate direct blocks
            while (nextDirect < Ufs2Constants.NDirect && offset < fileSize)
            {
                long blk = AllocateBlock(cgIndex);
                if (blk < 0)
                    throw new InvalidOperationException("No free blocks available for file data.");

                inode.DirectBlocks[nextDirect++] = blk;

                int toWrite = (int)Math.Min(Superblock.BSize, fileSize - offset);
                byte[] writeBuffer = new byte[Superblock.BSize];
                Array.Copy(fileData, offset, writeBuffer, 0, toWrite);
                _stream.Position = blk * Superblock.FSize;
                _stream.Write(writeBuffer, 0, writeBuffer.Length);
                offset += toWrite;
            }

            // Allocate single-indirect if needed
            if (offset < fileSize)
            {
                int count = (int)Math.Min(
                    (fileSize - offset + Superblock.BSize - 1) / Superblock.BSize,
                    pointersPerBlock);

                long[] dataBlocks = new long[count];
                for (int i = 0; i < count && offset < fileSize; i++)
                {
                    long blk = AllocateBlock(cgIndex);
                    if (blk < 0)
                        throw new InvalidOperationException("No free blocks available for file data.");

                    dataBlocks[i] = blk;

                    int toWrite = (int)Math.Min(Superblock.BSize, fileSize - offset);
                    byte[] writeBuffer = new byte[Superblock.BSize];
                    Array.Copy(fileData, offset, writeBuffer, 0, toWrite);
                    _stream.Position = blk * Superblock.FSize;
                    _stream.Write(writeBuffer, 0, writeBuffer.Length);
                    offset += toWrite;
                }

                inode.IndirectBlocks[0] = AllocateIndirectBlock(dataBlocks, count, cgIndex);
            }

            // Allocate double-indirect if needed
            if (offset < fileSize)
            {
                int remaining = (int)((fileSize - offset + Superblock.BSize - 1) / Superblock.BSize);
                int sindCount = (remaining + pointersPerBlock - 1) / pointersPerBlock;
                long[] sindPointers = new long[sindCount];

                for (int s = 0; s < sindCount && offset < fileSize; s++)
                {
                    int count = Math.Min(remaining, pointersPerBlock);
                    long[] dataBlocks = new long[count];

                    for (int i = 0; i < count && offset < fileSize; i++)
                    {
                        long blk = AllocateBlock(cgIndex);
                        if (blk < 0)
                            throw new InvalidOperationException("No free blocks available for file data.");

                        dataBlocks[i] = blk;

                        int toWrite = (int)Math.Min(Superblock.BSize, fileSize - offset);
                        byte[] writeBuffer = new byte[Superblock.BSize];
                        Array.Copy(fileData, offset, writeBuffer, 0, toWrite);
                        _stream.Position = blk * Superblock.FSize;
                        _stream.Write(writeBuffer, 0, writeBuffer.Length);
                        offset += toWrite;
                    }

                    sindPointers[s] = AllocateIndirectBlock(dataBlocks, count, cgIndex);
                    remaining -= count;
                }

                inode.IndirectBlocks[1] = AllocateIndirectBlock(sindPointers, sindCount, cgIndex);
            }

            // Calculate block count (in 512-byte sectors)
            long blocksNeeded = fileSize == 0 ? 0 : (fileSize + Superblock.BSize - 1) / Superblock.BSize;
            long dataFrags = blocksNeeded * Superblock.FragsPerBlock;
            inode.Blocks = dataFrags * (Superblock.FSize / 512);
            if (inode.IndirectBlocks[0] != 0)
                inode.Blocks += Superblock.FragsPerBlock * (Superblock.FSize / 512);
            if (inode.IndirectBlocks[1] != 0)
            {
                long sindBlocks = (blocksNeeded - Ufs2Constants.NDirect - pointersPerBlock
                    + pointersPerBlock - 1) / pointersPerBlock;
                if (sindBlocks < 0) sindBlocks = 0;
                inode.Blocks += (1 + sindBlocks) * Superblock.FragsPerBlock * (Superblock.FSize / 512);
            }

            WriteInode((uint)newIno, inode);

            // Add directory entry in parent
            AddDirectoryEntry(parentInode, fileName, (uint)newIno, Ufs2Constants.DtReg);

            // Update superblock
            Superblock.Time = now;
            WriteSuperblock();
            _stream.Flush();
        }

        /// <summary>
        /// Create a new directory in the UFS image.
        /// </summary>
        public void AddDirectory(string parentFsPath, string name)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot add directory: image is opened read-only.");

            uint parentInode = ResolvePath(parentFsPath);
            int cgIndex = (int)(parentInode / Superblock.InodesPerGroup);

            // Allocate inode
            long newIno = AllocateInode(cgIndex);
            if (newIno < 0)
                throw new InvalidOperationException("No free inodes available.");

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int dirBlkSize = Ufs2Constants.DirBlockSize;

            // Allocate a block for the directory
            long blk = AllocateBlock(cgIndex);
            if (blk < 0)
                throw new InvalidOperationException("No free blocks available for directory.");

            var inode = new Ufs2Inode
            {
                Mode = Ufs2Constants.IfDir | 0x1ED, // rwxr-xr-x
                NLink = 2, // . and entry from parent
                Size = dirBlkSize,
                BlkSize = (uint)Superblock.BSize,
                AccessTime = now,
                ModTime = now,
                ChangeTime = now,
                CreateTime = now,
                Generation = Random.Shared.Next(1, int.MaxValue)
            };
            inode.DirectBlocks[0] = blk;
            inode.Blocks = Superblock.FragsPerBlock * (Superblock.FSize / 512);

            // Write "." and ".." entries
            byte[] blockData = new byte[Superblock.BSize];
            ushort dotLen = Ufs2DirectoryEntry.CalculateRecordLength(1, Superblock.IsUfs2);
            var dotEntry = new Ufs2DirectoryEntry
            {
                Inode = (uint)newIno,
                RecordLength = dotLen,
                FileType = Ufs2Constants.DtDir,
                NameLength = 1,
                Name = "."
            };
            var dotDotEntry = new Ufs2DirectoryEntry
            {
                Inode = parentInode,
                RecordLength = (ushort)(dirBlkSize - dotLen),
                FileType = Ufs2Constants.DtDir,
                NameLength = 2,
                Name = ".."
            };

            using (var ms = new MemoryStream(blockData, 0, dirBlkSize))
            using (var w = new BinaryWriter(ms))
            {
                dotEntry.WriteTo(w);
                dotDotEntry.WriteTo(w);
            }

            _stream.Position = blk * Superblock.FSize;
            _stream.Write(blockData, 0, Superblock.BSize);

            WriteInode((uint)newIno, inode);

            // Add directory entry in parent
            AddDirectoryEntry(parentInode, name, (uint)newIno, Ufs2Constants.DtDir);

            // Update parent's NLink for ".." backlink
            var parentInodeData = ReadInode(parentInode);
            parentInodeData.NLink++;
            parentInodeData.ChangeTime = now;
            WriteInode(parentInode, parentInodeData);

            // Update CG directory count
            int newCg = (int)((uint)newIno / Superblock.InodesPerGroup);
            long newCgStartFrag = (long)newCg * Superblock.CylGroupSize;
            long newCgHeaderOffset = newCgStartFrag * Superblock.FSize + (long)Superblock.CblkNo * Superblock.FSize;
            byte[] cgData = ReadCgBlock(newCgHeaderOffset);
            int cgNdir = ReadCgInt32(cgData, 0x18);
            WriteCgInt32(cgData, 0x18, cgNdir + 1);
            _stream.Position = newCgHeaderOffset;
            _stream.Write(cgData, 0, cgData.Length);

            // Update superblock
            Superblock.Directories++;
            Superblock.Time = now;
            WriteSuperblock();
            _stream.Flush();
        }

        /// <summary>
        /// Add a file or directory from the local filesystem into the UFS image
        /// at the specified path.
        /// </summary>
        /// <param name="fsPath">Destination path in the UFS filesystem.</param>
        /// <param name="sourcePath">Path on the host filesystem to add from.</param>
        public void Add(string fsPath, string sourcePath)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot add: image is opened read-only.");

            if (File.Exists(sourcePath))
            {
                // Source is a file — use the fs path's filename for the entry name
                string parentPath = GetParentPath(fsPath);
                string fsName = GetLastComponent(fsPath);
                Output?.WriteLine($"  Adding file: {fsPath}");
                AddFile(parentPath, sourcePath, fsName);
            }
            else if (Directory.Exists(sourcePath))
            {
                // Source is a directory
                string parentPath = GetParentPath(fsPath);
                string dirName = GetLastComponent(fsPath);
                Output?.WriteLine($"  Adding directory: {fsPath}");
                AddDirectory(parentPath, dirName);

                // Recursively add contents
                AddDirectoryContentsRecursive(fsPath, sourcePath);
            }
            else
            {
                throw new FileNotFoundException($"Source path '{sourcePath}' not found.");
            }
        }

        /// <summary>
        /// Recursively add the contents of a local directory into a UFS directory.
        /// </summary>
        private void AddDirectoryContentsRecursive(string fsDir, string sourceDir)
        {
            foreach (string filePath in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(filePath);
                string fsFilePath = fsDir.TrimEnd('/') + "/" + fileName;
                Output?.WriteLine($"  Adding file: {fsFilePath}");
                AddFile(fsDir, filePath);
            }

            foreach (string subDirPath in Directory.GetDirectories(sourceDir))
            {
                string subDirName = Path.GetFileName(subDirPath);
                string fsDirPath = fsDir.TrimEnd('/') + "/" + subDirName;
                Output?.WriteLine($"  Adding directory: {fsDirPath}");
                AddDirectory(fsDir, subDirName);
                AddDirectoryContentsRecursive(fsDirPath, subDirPath);
            }
        }

        /// <summary>
        /// Delete a file or directory at the given filesystem path.
        /// If it is a directory, all contents are deleted recursively first.
        /// </summary>
        /// <param name="fsPath">Path in the UFS filesystem to delete.</param>
        public void Delete(string fsPath)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot delete: image is opened read-only.");

            string parentPath = GetParentPath(fsPath);
            string entryName = GetLastComponent(fsPath);

            uint parentInode = ResolvePath(parentPath);
            uint targetInode = ResolvePath(fsPath);
            var inode = ReadInode(targetInode);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (inode.IsRegularFile)
            {
                FreeFileBlocks(inode);
                RemoveDirectoryEntry(parentInode, entryName);
                FreeInode(targetInode);
            }
            else if (inode.IsDirectory)
            {
                // Recursively delete contents first
                DeleteDirectoryContents(targetInode);

                // Free the directory's own blocks
                FreeFileBlocks(inode);
                RemoveDirectoryEntry(parentInode, entryName);
                FreeInode(targetInode);

                // Update parent's NLink for removed ".." backlink
                var parentInodeData = ReadInode(parentInode);
                parentInodeData.NLink--;
                parentInodeData.ChangeTime = now;
                WriteInode(parentInode, parentInodeData);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cannot delete: unsupported file type at '{fsPath}' (mode=0x{inode.Mode:X4}).");
            }

            Superblock.Time = now;
            WriteSuperblock();
            _stream.Flush();
        }

        /// <summary>
        /// Delete all contents of a directory (files and subdirectories),
        /// skipping "." and ".." entries.
        /// </summary>
        private void DeleteDirectoryContents(uint dirInodeNumber)
        {
            var entries = ListDirectory(dirInodeNumber);

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                var entryInode = ReadInode(entry.Inode);

                if (entryInode.IsDirectory)
                {
                    // Recursively delete subdirectory contents
                    DeleteDirectoryContents(entry.Inode);
                    FreeFileBlocks(entryInode);
                    RemoveDirectoryEntry(dirInodeNumber, entry.Name);
                    FreeInode(entry.Inode);

                    // Update parent NLink for ".." removal
                    var dirInode = ReadInode(dirInodeNumber);
                    dirInode.NLink--;
                    dirInode.ChangeTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    WriteInode(dirInodeNumber, dirInode);
                }
                else
                {
                    FreeFileBlocks(entryInode);
                    RemoveDirectoryEntry(dirInodeNumber, entry.Name);
                    FreeInode(entry.Inode);
                }
            }
        }

        // --- Rename support ---

        /// <summary>
        /// Rename a file or directory at the given filesystem path.
        /// The entry stays in the same parent directory; only its name is changed.
        /// </summary>
        /// <param name="fsPath">Path of the file or directory to rename (e.g., "/path/to/old.txt").</param>
        /// <param name="newName">New name for the entry (just the name, not a full path).</param>
        public void Rename(string fsPath, string newName)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot rename: image is opened read-only.");

            if (string.IsNullOrEmpty(newName))
                throw new ArgumentException("New name cannot be empty.", nameof(newName));

            if (newName.Contains('/'))
                throw new ArgumentException("New name cannot contain path separators.", nameof(newName));

            string parentPath = GetParentPath(fsPath);
            string oldName = GetLastComponent(fsPath);

            if (oldName == newName)
                return; // Nothing to do

            uint parentInode = ResolvePath(parentPath);
            uint targetInode = ResolvePath(fsPath);
            var inode = ReadInode(targetInode);

            // Determine file type for the directory entry
            byte fileType;
            if (inode.IsDirectory)
                fileType = Ufs2Constants.DtDir;
            else if (inode.IsRegularFile)
                fileType = Ufs2Constants.DtReg;
            else if (inode.IsSymlink)
                fileType = Ufs2Constants.DtLnk;
            else
                fileType = 0;

            // Remove old entry and add new one with the same inode
            RemoveDirectoryEntry(parentInode, oldName);
            AddDirectoryEntry(parentInode, newName, targetInode, fileType);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            inode.ChangeTime = now;
            WriteInode(targetInode, inode);

            Superblock.Time = now;
            WriteSuperblock();
            _stream.Flush();
        }

        // --- Chmod support ---

        /// <summary>
        /// Change the permission bits of a file or directory at the given filesystem path.
        /// Only the lower 12 bits (permissions) of the mode are changed; the file type bits are preserved.
        /// </summary>
        /// <param name="fsPath">Path in the UFS filesystem (e.g., "/path/to/file").</param>
        /// <param name="mode">New permission mode (octal, e.g., 0x1FF for 0777).</param>
        public void Chmod(string fsPath, ushort mode)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot chmod: image is opened read-only.");

            uint inodeNumber = ResolvePath(fsPath);
            var inode = ReadInode(inodeNumber);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Preserve file type bits (upper 4 bits) and replace permission bits (lower 12 bits)
            inode.Mode = (ushort)((inode.Mode & 0xF000) | (mode & 0x0FFF));
            inode.ChangeTime = now;
            WriteInode(inodeNumber, inode);

            Superblock.Time = now;
            WriteSuperblock();
            _stream.Flush();
        }

        /// <summary>
        /// Recursively change the permission bits of all files and directories
        /// in the entire filesystem image starting from the root.
        /// </summary>
        /// <param name="fileMode">Permission mode for regular files (e.g., 0x1A4 for 0644).</param>
        /// <param name="dirMode">Permission mode for directories (e.g., 0x1ED for 0755).</param>
        public void ChmodAll(ushort fileMode, ushort dirMode)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Cannot chmod: image is opened read-only.");

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Apply dirMode to root directory itself
            var rootInode = ReadInode(Ufs2Constants.RootInode);
            rootInode.Mode = (ushort)((rootInode.Mode & 0xF000) | (dirMode & 0x0FFF));
            rootInode.ChangeTime = now;
            WriteInode(Ufs2Constants.RootInode, rootInode);

            ChmodRecursive(Ufs2Constants.RootInode, fileMode, dirMode, now);

            Superblock.Time = now;
            WriteSuperblock();
            _stream.Flush();
        }

        /// <summary>
        /// Recursively change permissions of all entries in a directory.
        /// </summary>
        private void ChmodRecursive(uint dirInodeNumber, ushort fileMode, ushort dirMode, long now)
        {
            var entries = ListDirectory(dirInodeNumber);

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                var inode = ReadInode(entry.Inode);

                if (inode.IsDirectory)
                {
                    inode.Mode = (ushort)((inode.Mode & 0xF000) | (dirMode & 0x0FFF));
                    inode.ChangeTime = now;
                    WriteInode(entry.Inode, inode);
                    ChmodRecursive(entry.Inode, fileMode, dirMode, now);
                }
                else
                {
                    inode.Mode = (ushort)((inode.Mode & 0xF000) | (fileMode & 0x0FFF));
                    inode.ChangeTime = now;
                    WriteInode(entry.Inode, inode);
                }
            }
        }

        /// <summary>
        /// Get the parent path component from a filesystem path.
        /// </summary>
        private static string GetParentPath(string fsPath)
        {
            string trimmed = fsPath.TrimEnd('/');
            int lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash <= 0) return "/";
            return trimmed[..lastSlash];
        }

        /// <summary>
        /// Get the last component (file or directory name) from a filesystem path.
        /// </summary>
        private static string GetLastComponent(string fsPath)
        {
            string trimmed = fsPath.TrimEnd('/');
            int lastSlash = trimmed.LastIndexOf('/');
            if (lastSlash < 0) return trimmed;
            return trimmed[(lastSlash + 1)..];
        }

        /// <summary>
        /// Grow the filesystem to a new size, equivalent to FreeBSD's growfs(8).
        /// Extends the image file, adds new cylinder groups, updates the last
        /// (joining) CG, relocates the CG summary if needed, and writes back
        /// all metadata (superblock, backup superblocks, CG headers).
        /// </summary>
        /// <param name="newSizeBytes">New total size in bytes. Must be larger than
        /// current filesystem size and fragment-aligned.</param>
        /// <param name="dryRun">If true, print parameters but do not modify the image.</param>
        public void GrowFs(long newSizeBytes, bool dryRun = false)
        {
            if (IsReadOnly && !dryRun)
                throw new InvalidOperationException("Cannot grow filesystem: image is opened read-only.");

            var sb = Superblock;
            int fragSize = sb.FSize;
            int blockSize = sb.BSize;
            int fragsPerBlock = sb.FragsPerBlock;
            int fragsPerGroup = sb.CylGroupSize;
            int inodesPerGroup = sb.InodesPerGroup;
            int sblkno = sb.SuperblockLocation;
            int cblkno = sb.CblkNo;
            int iblkno = sb.IblkNo;
            int dblkno = sb.DblkNo;

            long oldTotalFrags = sb.TotalBlocks;
            long oldSizeBytes = oldTotalFrags * fragSize;

            // Align new size down to fragment boundary
            newSizeBytes -= newSizeBytes % fragSize;
            long newTotalFrags = newSizeBytes / fragSize;

            if (newTotalFrags <= oldTotalFrags)
                throw new InvalidOperationException(
                    $"New size ({newSizeBytes} bytes, {newTotalFrags} frags) must be larger than " +
                    $"current size ({oldSizeBytes} bytes, {oldTotalFrags} frags).");

            int oldNumCg = sb.NumCylGroups;
            int newNumCg = (int)((newTotalFrags + fragsPerGroup - 1) / fragsPerGroup);
            if (newNumCg < 1) newNumCg = 1;

            // CG summary sizes
            int oldCsSize = sb.CsSize;
            int newCsSize = AlignUp(newNumCg * Ufs2Constants.CsumStructSize, fragSize);
            int oldCsFrags = (oldCsSize + fragSize - 1) / fragSize;
            int newCsFrags = (newCsSize + fragSize - 1) / fragSize;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Print parameters
            double mbFactor = 1.0 / (1024.0 * 1024.0);
            Console.WriteLine($"growfs: {newTotalFrags * fragSize * mbFactor:F1}MB " +
                $"({newTotalFrags} frags) " +
                $"block size {blockSize}, fragment size {fragSize}");
            Console.WriteLine($"\tusing {newNumCg} cylinder groups of " +
                $"{(double)fragsPerGroup * fragSize * mbFactor:F2}MB, " +
                $"{fragsPerGroup / fragsPerBlock} blks, {inodesPerGroup} inodes.");
            Console.WriteLine($"\tgrowing from {oldNumCg} to {newNumCg} cylinder groups.");

            if (dryRun)
            {
                Console.WriteLine("(dry run — no changes written)");
                return;
            }

            // Extend the image file
            _stream.SetLength(newSizeBytes);

            // Build new superblock values
            sb.TotalBlocks = newTotalFrags;
            sb.NumCylGroups = newNumCg;
            sb.CsSize = newCsSize;
            sb.ProviderSize = newTotalFrags;
            sb.Time = now;

            // Read existing CG summary data
            byte[] oldCsSummary = new byte[oldCsSize];
            _stream.Position = sb.CsAddr * fragSize;
            ReadFully(oldCsSummary, 0, oldCsSize);

            // Prepare new CG summary array (block-aligned for writes)
            int newCsFragsBlk = ((newCsSize + blockSize - 1) / blockSize) * fragsPerBlock;
            byte[] newCsSummary = new byte[newCsFragsBlk * fragSize];
            Array.Copy(oldCsSummary, 0, newCsSummary, 0, Math.Min(oldCsSize, newCsSummary.Length));

            // --- Phase 1: Update the last (joining) cylinder group ---
            int joinCg = oldNumCg - 1;
            long joinCgStartFrag = (long)joinCg * fragsPerGroup;
            long oldDmax = oldTotalFrags;
            long newDmax = Math.Min(joinCgStartFrag + fragsPerGroup, newTotalFrags);

            int oldNdblk = (int)(oldDmax - joinCgStartFrag);
            int newNdblk = (int)(newDmax - joinCgStartFrag);

            if (newNdblk > oldNdblk)
            {
                // Read existing CG header
                long joinCgHeaderOffset = joinCgStartFrag * fragSize + (long)cblkno * fragSize;
                byte[] cgData = ReadCgBlock(joinCgHeaderOffset);

                // Update cg_ndblk
                WriteCgInt32(cgData, 0x14, newNdblk);

                // Update cg_nclusterblks if contig sum enabled
                if (sb.ContigSumSize > 0)
                    WriteCgInt32(cgData, 0x70, newNdblk / fragsPerBlock);

                // Update cg_time
                WriteCgInt32(cgData, 0x08, (int)(now & 0xFFFFFFFF));
                WriteCgInt64(cgData, 0x88, now);

                // Get bitmap offsets from the CG header
                int freeoff = ReadCgInt32(cgData, 0x60);

                // Mark new fragments as free in the fragment bitmap
                // New fragments are from oldNdblk to newNdblk
                long addedFreeBlocks = 0;
                long addedFreeFrags = 0;

                // Handle partial block at old end
                int oldTailFrags = oldNdblk % fragsPerBlock;
                int startFrag = oldNdblk;

                if (oldTailFrags > 0 && newNdblk >= ((oldNdblk / fragsPerBlock) + 1) * fragsPerBlock)
                {
                    // Old end had a partial block; fill it up
                    int blockEnd = ((oldNdblk / fragsPerBlock) + 1) * fragsPerBlock;
                    // Remove old frsum entry
                    SubtractFrsum(cgData, oldTailFrags);

                    for (int f = oldNdblk; f < blockEnd && f < newNdblk; f++)
                        SetFragBit(cgData, freeoff, f);

                    // Check if we completed the block
                    if (blockEnd <= newNdblk)
                    {
                        // Completed block: count as free block, subtract the individual frags
                        int totalFragsInBlock = fragsPerBlock;
                        addedFreeBlocks++;
                        // The old partial frags were free fragments; converting to block
                        addedFreeFrags -= oldTailFrags;
                        // Update cluster bitmap
                        if (sb.ContigSumSize > 0)
                            SetClusterBit(cgData, blockEnd / fragsPerBlock - 1);
                    }
                    else
                    {
                        // Still partial — update frsum
                        int newPartial = newNdblk % fragsPerBlock;
                        if (newPartial > 0)
                            AddFrsum(cgData, newPartial);
                        addedFreeFrags += (newNdblk - oldNdblk);
                    }
                    startFrag = blockEnd;
                }
                else if (oldTailFrags > 0)
                {
                    // Growing within the same partial block
                    SubtractFrsum(cgData, oldTailFrags);
                    for (int f = oldNdblk; f < newNdblk; f++)
                        SetFragBit(cgData, freeoff, f);
                    int newPartial = newNdblk % fragsPerBlock;
                    if (newPartial > 0)
                        AddFrsum(cgData, newPartial);
                    else
                    {
                        // Completed the block
                        addedFreeBlocks++;
                        addedFreeFrags -= oldTailFrags;
                        if (sb.ContigSumSize > 0)
                            SetClusterBit(cgData, (newNdblk / fragsPerBlock) - 1);
                    }
                    addedFreeFrags += (newNdblk - oldNdblk);
                    startFrag = newNdblk;
                }

                // Handle complete new blocks
                for (int f = startFrag; f + fragsPerBlock <= newNdblk; f += fragsPerBlock)
                {
                    for (int ff = 0; ff < fragsPerBlock; ff++)
                        SetFragBit(cgData, freeoff, f + ff);
                    addedFreeBlocks++;
                    if (sb.ContigSumSize > 0)
                        SetClusterBit(cgData, f / fragsPerBlock);
                }

                // Handle trailing partial block
                int lastFullBlock = startFrag + ((newNdblk - startFrag) / fragsPerBlock) * fragsPerBlock;
                if (lastFullBlock < newNdblk)
                {
                    int tailSize = newNdblk - lastFullBlock;
                    for (int f = lastFullBlock; f < newNdblk; f++)
                        SetFragBit(cgData, freeoff, f);
                    addedFreeFrags += tailSize;
                    AddFrsum(cgData, tailSize);
                }

                // Update CG cs fields
                int cgNbfree = ReadCgInt32(cgData, 0x1C);
                int cgNffree = ReadCgInt32(cgData, 0x24);
                cgNbfree += (int)addedFreeBlocks;
                cgNffree += (int)addedFreeFrags;
                WriteCgInt32(cgData, 0x1C, cgNbfree);
                WriteCgInt32(cgData, 0x24, cgNffree);

                // Recompute cluster summary if enabled
                if (sb.ContigSumSize > 0)
                    RecomputeClusterSummary(cgData, sb.ContigSumSize, newNdblk / fragsPerBlock);

                // Update superblock dsize
                sb.TotalDataBlocks += (newNdblk - oldNdblk);

                // Update superblock free totals
                sb.FreeBlocks += addedFreeBlocks;
                sb.FreeFragments += addedFreeFrags;

                // Write CG summary entry for this CG
                int cgDirs = ReadCgInt32(cgData, 0x18);
                int cgNifree = ReadCgInt32(cgData, 0x20);
                WriteCsumEntry(newCsSummary, joinCg, cgDirs, cgNbfree, cgNifree, cgNffree);

                // Write updated CG back to disk
                _stream.Position = joinCgHeaderOffset;
                _stream.Write(cgData, 0, cgData.Length);
            }

            // --- Phase 2: Create new cylinder groups ---
            if (newNumCg > oldNumCg)
            {
                Console.Write("super-block backups (for fsck_ffs -b #) at:\n");
                string line = "";

                for (int cg = oldNumCg; cg < newNumCg; cg++)
                {
                    long cgStartFrag = (long)cg * fragsPerGroup;
                    long dmax = Math.Min(cgStartFrag + fragsPerGroup, newTotalFrags);
                    int ndblk = (int)(dmax - cgStartFrag);

                    // Initialize the new CG
                    byte[] cgData = InitNewCylinderGroup(cg, ndblk, inodesPerGroup,
                        now, fragsPerBlock, fragsPerGroup, sblkno, cblkno, iblkno, dblkno,
                        sb.ContigSumSize, sb.CgSize, sb.Magic);

                    // Write CG header
                    long cgHeaderOffset = cgStartFrag * fragSize + (long)cblkno * fragSize;
                    _stream.Position = cgHeaderOffset;
                    _stream.Write(cgData, 0, cgData.Length);

                    // Write inode table (zeroed with random di_gen)
                    WriteNewInodeTable(cg, cgStartFrag, inodesPerGroup, iblkno, now);

                    // Read back the CG stats to update totals
                    int cgDirs = ReadCgInt32(cgData, 0x18);
                    int cgNbfree = ReadCgInt32(cgData, 0x1C);
                    int cgNifree = ReadCgInt32(cgData, 0x20);
                    int cgNffree = ReadCgInt32(cgData, 0x24);

                    // Update superblock totals
                    sb.FreeBlocks += cgNbfree;
                    sb.FreeFragments += cgNffree;
                    sb.FreeInodes += cgNifree;
                    sb.Directories += cgDirs;

                    // fs_dsize: add data area + boot area (for CG > 0)
                    sb.TotalDataBlocks += (ndblk - dblkno) + sblkno;

                    // Write CG summary entry
                    WriteCsumEntry(newCsSummary, cg, cgDirs, cgNbfree, cgNifree, cgNffree);

                    // Write backup superblock
                    long backupSbOffset = cgStartFrag * fragSize + (long)sblkno * fragSize;
                    // Backup SB will be written in final pass

                    // Print backup superblock location
                    long sectorLoc = (cgStartFrag + sblkno) * fragSize / 512;
                    string sep = cg < newNumCg - 1 ? "," : "";
                    string entry = $" {sectorLoc}{sep}";
                    if (line.Length + entry.Length > 76)
                    {
                        Console.WriteLine(line);
                        line = "";
                    }
                    line += entry;
                }
                if (line.Length > 0)
                    Console.WriteLine(line);
            }

            // --- Phase 3: Relocate CG summary if it grew ---
            if (newCsFrags > oldCsFrags && newNumCg - oldNumCg >= 2)
            {
                // Free old CG summary space in the CG that contained it
                int oldCsCg = (int)(sb.CsAddr / fragsPerGroup);
                long oldCsCgStart = (long)oldCsCg * fragsPerGroup;
                long oldCsCgHeaderOffset = oldCsCgStart * fragSize + (long)cblkno * fragSize;
                byte[] oldCsCgData = ReadCgBlock(oldCsCgHeaderOffset);

                int freeoffOld = ReadCgInt32(oldCsCgData, 0x60);

                // Free the old CG summary fragments
                long csAddrInCg = sb.CsAddr - oldCsCgStart;
                for (long f = csAddrInCg; f < csAddrInCg + oldCsFrags && f < fragsPerGroup; f++)
                    SetFragBit(oldCsCgData, freeoffOld, (int)f);

                // Update free counts: add freed blocks/frags
                int freedBlocks = oldCsFrags / fragsPerBlock;
                int freedFrags = oldCsFrags % fragsPerBlock;
                int tmpNbfree = ReadCgInt32(oldCsCgData, 0x1C) + freedBlocks;
                int tmpNffree = ReadCgInt32(oldCsCgData, 0x24) + freedFrags;
                WriteCgInt32(oldCsCgData, 0x1C, tmpNbfree);
                WriteCgInt32(oldCsCgData, 0x24, tmpNffree);
                sb.FreeBlocks += freedBlocks;
                sb.FreeFragments += freedFrags;

                // Update cluster bitmap for freed blocks
                if (sb.ContigSumSize > 0)
                {
                    for (long f = csAddrInCg; f + fragsPerBlock <= csAddrInCg + oldCsFrags; f += fragsPerBlock)
                        SetClusterBit(oldCsCgData, (int)(f / fragsPerBlock));
                    int oldCsCgNdblk = ReadCgInt32(oldCsCgData, 0x14);
                    RecomputeClusterSummary(oldCsCgData, sb.ContigSumSize, oldCsCgNdblk / fragsPerBlock);
                }

                // Update CG summary for old CG
                int oldCsDirs = ReadCgInt32(oldCsCgData, 0x18);
                int oldCsNifree = ReadCgInt32(oldCsCgData, 0x20);
                WriteCsumEntry(newCsSummary, oldCsCg, oldCsDirs, tmpNbfree, oldCsNifree, tmpNffree);

                _stream.Position = oldCsCgHeaderOffset;
                _stream.Write(oldCsCgData, 0, oldCsCgData.Length);

                // Relocate to first new full CG: cgdmin of the first new CG
                long newCsAddr = (long)oldNumCg * fragsPerGroup + dblkno;
                sb.CsAddr = newCsAddr;

                // Allocate new CG summary space in the new CG
                int newCsCg = (int)(newCsAddr / fragsPerGroup);
                long newCsCgStart = (long)newCsCg * fragsPerGroup;
                long newCsCgHeaderOffset = newCsCgStart * fragSize + (long)cblkno * fragSize;
                byte[] newCsCgData = ReadCgBlock(newCsCgHeaderOffset);

                int freeoffNew = ReadCgInt32(newCsCgData, 0x60);

                // Allocate (clear) CG summary fragments in new CG
                long newCsAddrInCg = newCsAddr - newCsCgStart;
                for (long f = newCsAddrInCg; f < newCsAddrInCg + newCsFrags && f < fragsPerGroup; f++)
                    ClearFragBit(newCsCgData, freeoffNew, (int)f);

                // Update free counts
                int allocBlocks = newCsFrags / fragsPerBlock;
                int allocFrags = newCsFrags % fragsPerBlock;
                int newCgNbfree = ReadCgInt32(newCsCgData, 0x1C) - allocBlocks;
                int newCgNffree = ReadCgInt32(newCsCgData, 0x24) - allocFrags;

                // Handle partial block at end of CS area
                if (allocFrags > 0)
                {
                    newCgNbfree--;
                    newCgNffree += fragsPerBlock;
                }

                WriteCgInt32(newCsCgData, 0x1C, newCgNbfree);
                WriteCgInt32(newCsCgData, 0x24, newCgNffree);
                sb.FreeBlocks -= allocBlocks + (allocFrags > 0 ? 1 : 0);
                sb.FreeFragments -= allocFrags - (allocFrags > 0 ? fragsPerBlock : 0);
                sb.TotalDataBlocks -= newCsFrags;

                // Update cluster bitmap for allocated blocks
                if (sb.ContigSumSize > 0)
                {
                    for (long f = newCsAddrInCg; f + fragsPerBlock <= newCsAddrInCg + newCsFrags; f += fragsPerBlock)
                        ClearClusterBit(newCsCgData, (int)(f / fragsPerBlock));
                    if (allocFrags > 0)
                        ClearClusterBit(newCsCgData, (int)((newCsAddrInCg + newCsFrags) / fragsPerBlock));
                    int newCsCgNdblk = ReadCgInt32(newCsCgData, 0x14);
                    RecomputeClusterSummary(newCsCgData, sb.ContigSumSize, newCsCgNdblk / fragsPerBlock);
                }

                // Update fragment summary for new CG
                if (allocFrags > 0)
                    AddFrsum(newCsCgData, fragsPerBlock - allocFrags);

                // Update CG summary for new CG
                int newCsDirs = ReadCgInt32(newCsCgData, 0x18);
                int newCsNifree = ReadCgInt32(newCsCgData, 0x20);
                WriteCsumEntry(newCsSummary, newCsCg, newCsDirs, newCgNbfree, newCsNifree, newCgNffree);

                _stream.Position = newCsCgHeaderOffset;
                _stream.Write(newCsCgData, 0, newCsCgData.Length);
            }
            else if (newCsFrags > oldCsFrags)
            {
                // CG summary grew but not enough new CGs to relocate —
                // just grow in place (adjust dsize)
                sb.TotalDataBlocks -= (newCsFrags - oldCsFrags);
            }

            // Write CG summary area
            _stream.Position = sb.CsAddr * fragSize;
            _stream.Write(newCsSummary, 0, Math.Min(newCsSummary.Length, newCsSize));

            // Clear mount point per FreeBSD growfs convention
            sb.MountPoint = "";

            // Write primary superblock
            _stream.Position = Ufs2Constants.SuperblockOffset;
            sb.WriteTo(Writer);
            Writer.Flush();

            // Write backup superblocks to all CGs
            for (int cg = 1; cg < newNumCg; cg++)
            {
                long cgStartFrag = (long)cg * fragsPerGroup;
                long backupSbOffset = cgStartFrag * fragSize + (long)sblkno * fragSize;
                int usable = fragsPerGroup;
                if (cg == newNumCg - 1)
                    usable = (int)(newTotalFrags - cgStartFrag);
                if (backupSbOffset + Ufs2Constants.SuperblockSize <=
                    cgStartFrag * fragSize + (long)usable * fragSize)
                {
                    _stream.Position = backupSbOffset;
                    sb.WriteTo(Writer);
                }
            }
            Writer.Flush();

            // Re-read to update in-memory state
            ReadSuperblock();

            Console.WriteLine($"growfs: filesystem grown to {newSizeBytes / (1024 * 1024)} MB " +
                $"({newTotalFrags} frags, {newNumCg} cylinder groups).");
        }

        // --- GrowFs helper methods ---

        private byte[] ReadCgBlock(long offset)
        {
            int cgSize = Superblock.CgSize;
            byte[] data = new byte[cgSize];
            _stream.Position = offset;
            ReadFully(data, 0, cgSize);
            return data;
        }

        private static int ReadCgInt32(byte[] data, int offset)
        {
            return BitConverter.ToInt32(data, offset);
        }

        private static void WriteCgInt32(byte[] data, int offset, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, data, offset, 4);
        }

        private static void WriteCgInt64(byte[] data, int offset, long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, data, offset, 8);
        }

        private static void SetFragBit(byte[] cgData, int freeoff, int fragIndex)
        {
            int byteIdx = freeoff + fragIndex / 8;
            int bitIdx = fragIndex % 8;
            if (byteIdx < cgData.Length)
                cgData[byteIdx] |= (byte)(1 << bitIdx);
        }

        private static void ClearFragBit(byte[] cgData, int freeoff, int fragIndex)
        {
            int byteIdx = freeoff + fragIndex / 8;
            int bitIdx = fragIndex % 8;
            if (byteIdx < cgData.Length)
                cgData[byteIdx] &= (byte)~(1 << bitIdx);
        }

        private static void SetClusterBit(byte[] cgData, int blockIndex)
        {
            int clusteroff = ReadCgInt32(cgData, 0x6C);
            if (clusteroff == 0) return;
            int byteIdx = clusteroff + blockIndex / 8;
            int bitIdx = blockIndex % 8;
            if (byteIdx < cgData.Length)
                cgData[byteIdx] |= (byte)(1 << bitIdx);
        }

        private static void ClearClusterBit(byte[] cgData, int blockIndex)
        {
            int clusteroff = ReadCgInt32(cgData, 0x6C);
            if (clusteroff == 0) return;
            int byteIdx = clusteroff + blockIndex / 8;
            int bitIdx = blockIndex % 8;
            if (byteIdx < cgData.Length)
                cgData[byteIdx] &= (byte)~(1 << bitIdx);
        }

        private static void AddFrsum(byte[] cgData, int size)
        {
            if (size <= 0 || size >= 8) return;
            // cg_frsum is at offset 0x34, 8 x int32
            int offset = 0x34 + size * 4;
            int val = ReadCgInt32(cgData, offset);
            WriteCgInt32(cgData, offset, val + 1);
        }

        private static void SubtractFrsum(byte[] cgData, int size)
        {
            if (size <= 0 || size >= 8) return;
            int offset = 0x34 + size * 4;
            int val = ReadCgInt32(cgData, offset);
            if (val > 0)
                WriteCgInt32(cgData, offset, val - 1);
        }

        private static void RecomputeClusterSummary(byte[] cgData, int contigSumSize, int nclusterblks)
        {
            int clustersumoff = ReadCgInt32(cgData, 0x68);
            int clusteroff = ReadCgInt32(cgData, 0x6C);
            if (clustersumoff == 0 || clusteroff == 0) return;

            // Clear existing cluster summary (entries 1..contigSumSize)
            for (int i = 1; i <= contigSumSize; i++)
                WriteCgInt32(cgData, clustersumoff + i * 4, 0);

            // Recompute from cluster bitmap
            int run = 0;
            for (int blk = 0; blk < nclusterblks; blk++)
            {
                int byteIdx = clusteroff + blk / 8;
                int bitIdx = blk % 8;
                bool isFree = byteIdx < cgData.Length && (cgData[byteIdx] & (1 << bitIdx)) != 0;
                if (isFree)
                {
                    run++;
                }
                else if (run > 0)
                {
                    int idx = Math.Min(run, contigSumSize);
                    int val = ReadCgInt32(cgData, clustersumoff + idx * 4);
                    WriteCgInt32(cgData, clustersumoff + idx * 4, val + 1);
                    run = 0;
                }
            }
            if (run > 0)
            {
                int idx = Math.Min(run, contigSumSize);
                int val = ReadCgInt32(cgData, clustersumoff + idx * 4);
                WriteCgInt32(cgData, clustersumoff + idx * 4, val + 1);
            }
        }

        private static void WriteCsumEntry(byte[] csSummary, int cgIndex,
            int dirs, int nbfree, int nifree, int nffree)
        {
            int offset = cgIndex * Ufs2Constants.CsumStructSize;
            if (offset + Ufs2Constants.CsumStructSize > csSummary.Length) return;
            WriteCgInt32(csSummary, offset, dirs);
            WriteCgInt32(csSummary, offset + 4, nbfree);
            WriteCgInt32(csSummary, offset + 8, nifree);
            WriteCgInt32(csSummary, offset + 12, nffree);
        }

        /// <summary>
        /// Initialize a new cylinder group for growfs.
        /// Based on FreeBSD growfs.c initcg() — creates a CG that is NOT CG 0
        /// (no root directory, no CG summary area).
        /// </summary>
        private byte[] InitNewCylinderGroup(int cgIndex, int ndblk, int inodesPerGroup,
            long timestamp, int fragsPerBlock, int fragsPerGroup, int sblkno, int cblkno,
            int iblkno, int dblkno, int contigSumSize, int cgSize, int magic)
        {
            int fragSize = Superblock.FSize;
            int blockSize = Superblock.BSize;
            int inodeSize = Superblock.IsUfs2 ? Ufs2Constants.Ufs2InodeSize : Ufs2Constants.Ufs1InodeSize;
            int cgFixedHeader = Ufs2Constants.CgHeaderBaseSize;

            int inodeBitmapBytes = (inodesPerGroup + 7) / 8;
            int fragBitmapBytes = (fragsPerGroup + 7) / 8;

            int inodeBitmapOff;
            if (magic == Ufs2Constants.Ufs2Magic)
            {
                inodeBitmapOff = cgFixedHeader;
            }
            else
            {
                // UFS1: old btot/b arrays
                int oldBtotOff = cgFixedHeader;
                int oldBoff = cgFixedHeader + 1 * 4;
                inodeBitmapOff = oldBoff + 1 * 2;
            }

            int fragBitmapOff = inodeBitmapOff + inodeBitmapBytes;
            int nextFreeOff = fragBitmapOff + fragBitmapBytes;

            int nclusterblks = 0;
            int clustersumoff = 0;
            int clusteroff = 0;
            if (contigSumSize > 0)
            {
                nclusterblks = ndblk / fragsPerBlock;
                int rawEnd = fragBitmapOff + (fragsPerGroup + 7) / 8;
                clustersumoff = AlignUp(rawEnd, 4) - 4;
                clusteroff = clustersumoff + (contigSumSize + 1) * 4;
                int blocksForClusterBitmap = fragsPerGroup / fragsPerBlock;
                nextFreeOff = clusteroff + (blocksForClusterBitmap + 7) / 8;
            }

            int totalCgSize = AlignUp(nextFreeOff, fragSize);
            totalCgSize = Math.Max(totalCgSize, cgSize);
            byte[] cgData = new byte[totalCgSize];

            // CG header
            WriteCgInt32(cgData, 0x04, Ufs2Constants.CgMagic);
            WriteCgInt32(cgData, 0x08, (int)(timestamp & 0xFFFFFFFF));
            WriteCgInt32(cgData, 0x0C, cgIndex);
            WriteCgInt32(cgData, 0x14, ndblk);

            int freeInodes = inodesPerGroup;
            // For CG > 0: boot area [0, sblkno) is free
            int dlower = sblkno; // equivalent to cgsblock - cbase
            int dupper = dblkno;

            // Count free blocks and fragments
            int totalFreeFragsInCg = (ndblk - dupper) + dlower;
            int freeBlocks = totalFreeFragsInCg / fragsPerBlock;
            int freeFragsRem = totalFreeFragsInCg % fragsPerBlock;

            // cs_ndir
            WriteCgInt32(cgData, 0x18, 0);
            // cs_nbfree
            WriteCgInt32(cgData, 0x1C, freeBlocks);
            // cs_nifree
            WriteCgInt32(cgData, 0x20, freeInodes);
            // cs_nffree
            WriteCgInt32(cgData, 0x24, freeFragsRem);

            // cg_iusedoff, cg_freeoff, cg_nextfreeoff
            WriteCgInt32(cgData, 0x5C, inodeBitmapOff);
            WriteCgInt32(cgData, 0x60, fragBitmapOff);
            WriteCgInt32(cgData, 0x64, nextFreeOff);
            WriteCgInt32(cgData, 0x68, clustersumoff);
            WriteCgInt32(cgData, 0x6C, clusteroff);
            WriteCgInt32(cgData, 0x70, nclusterblks);

            // cg_niblk
            WriteCgInt32(cgData, 0x74, inodesPerGroup);

            // cg_initediblk
            int inodesPerBlk = blockSize / inodeSize;
            int initediblk = (magic == Ufs2Constants.Ufs2Magic)
                ? Math.Min(inodesPerGroup, 2 * inodesPerBlk) : 0;
            WriteCgInt32(cgData, 0x78, initediblk);

            // cg_time (64-bit)
            WriteCgInt64(cgData, 0x88, timestamp);

            // UFS1 old fields
            if (magic == Ufs2Constants.Ufs1Magic)
            {
                WriteCgInt32(cgData, 0x54, cgFixedHeader); // old_btotoff
                WriteCgInt32(cgData, 0x58, cgFixedHeader + 4); // old_boff
            }

            // Fragment bitmap: mark boot area [0, sblkno) as free (for CG > 0)
            for (int f = 0; f < sblkno && f < ndblk; f++)
                SetFragBit(cgData, fragBitmapOff, f);

            // Mark data area [dupper, ndblk) as free
            for (int f = dupper; f < ndblk; f++)
                SetFragBit(cgData, fragBitmapOff, f);

            // Fragment summary for trailing partial block
            int dataTail = totalFreeFragsInCg % fragsPerBlock;
            if (dataTail > 0 && dataTail < fragsPerBlock)
            {
                // frsum at 0x34 + dataTail*4
                int offset = 0x34 + dataTail * 4;
                WriteCgInt32(cgData, offset, ReadCgInt32(cgData, offset) + 1);
            }

            // Cluster bitmap and summary
            if (contigSumSize > 0)
            {
                byte[] clusterBitmap = new byte[(nclusterblks + 7) / 8];
                for (int blk = 0; blk < nclusterblks; blk++)
                {
                    int fragBase = blk * fragsPerBlock;
                    bool allFree = true;
                    for (int ff = 0; ff < fragsPerBlock; ff++)
                    {
                        int f = fragBase + ff;
                        if (f >= ndblk) { allFree = false; break; }
                        int byteIdx = fragBitmapOff + f / 8;
                        int bitIdx = f % 8;
                        if (byteIdx >= cgData.Length || (cgData[byteIdx] & (1 << bitIdx)) == 0)
                        { allFree = false; break; }
                    }
                    if (allFree)
                        clusterBitmap[blk / 8] |= (byte)(1 << (blk % 8));
                }

                // Write cluster bitmap
                for (int bi = 0; bi < clusterBitmap.Length && clusteroff + bi < cgData.Length; bi++)
                    cgData[clusteroff + bi] = clusterBitmap[bi];

                // Compute cluster summary
                int run = 0;
                for (int blk = 0; blk < nclusterblks; blk++)
                {
                    bool isFree = (clusterBitmap[blk / 8] & (1 << (blk % 8))) != 0;
                    if (isFree) run++;
                    else if (run > 0)
                    {
                        int idx = Math.Min(run, contigSumSize);
                        int soff = clustersumoff + idx * 4;
                        WriteCgInt32(cgData, soff, ReadCgInt32(cgData, soff) + 1);
                        run = 0;
                    }
                }
                if (run > 0)
                {
                    int idx = Math.Min(run, contigSumSize);
                    int soff = clustersumoff + idx * 4;
                    WriteCgInt32(cgData, soff, ReadCgInt32(cgData, soff) + 1);
                }
            }

            return cgData;
        }

        /// <summary>
        /// Write an inode table for a new CG during growfs.
        /// Initializes inodes with random di_gen values per FreeBSD convention.
        /// </summary>
        private void WriteNewInodeTable(int cgIndex, long cgStartFrag, int inodesPerGroup,
            int iblkno, long timestamp)
        {
            var sb = Superblock;
            int inodeSize = sb.IsUfs2 ? Ufs2Constants.Ufs2InodeSize : Ufs2Constants.Ufs1InodeSize;
            int blockSize = sb.BSize;
            int fragSize = sb.FSize;

            int inodesPerBlock = blockSize / inodeSize;
            int initedInodes = sb.IsUfs2
                ? Math.Min(inodesPerGroup, 2 * inodesPerBlock)
                : inodesPerGroup;

            long inodeTableOffset = cgStartFrag * fragSize + (long)iblkno * fragSize;

            // UFS2 di_gen at offset 0x50, UFS1 di_gen at offset 0x6C
            int genOffset = sb.IsUfs2 ? 0x50 : 0x6C;

            var rng = new Random(cgIndex + 1);
            int totalBytes = inodesPerGroup * inodeSize;
            byte[] inodeTable = new byte[totalBytes];

            for (int i = 0; i < inodesPerGroup; i++)
            {
                if (i < initedInodes)
                {
                    int gen = rng.Next(1, int.MaxValue);
                    int off = i * inodeSize + genOffset;
                    inodeTable[off] = (byte)(gen & 0xFF);
                    inodeTable[off + 1] = (byte)((gen >> 8) & 0xFF);
                    inodeTable[off + 2] = (byte)((gen >> 16) & 0xFF);
                    inodeTable[off + 3] = (byte)((gen >> 24) & 0xFF);
                }
            }

            _stream.Position = inodeTableOffset;
            _stream.Write(inodeTable, 0, inodeTable.Length);
        }

        private static int AlignUp(int value, int alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        // --- fsck_ufs implementation ---

        /// <summary>
        /// Result of a filesystem consistency check, modeled after FreeBSD fsck_ffs(8) output.
        /// </summary>
        public class FsckResult
        {
            public bool Clean { get; set; } = true;
            public bool Modified { get; set; }
            public int Files { get; set; }
            public int Directories { get; set; }
            public long UsedBlocks { get; set; }
            public long FreeBlocks { get; set; }
            public long UsedInodes { get; set; }
            public long FreeInodes { get; set; }
            public double Fragmentation { get; set; }
            public List<string> Messages { get; } = [];
            public List<string> Warnings { get; } = [];
            public List<string> Errors { get; } = [];
        }

        /// <summary>
        /// Perform a filesystem consistency check, modeled after FreeBSD fsck_ffs(8)/fsck_ufs(8).
        /// Checks performed across five phases:
        ///   Phase 1 — Check Blocks and Sizes
        ///   Phase 2 — Check Pathnames
        ///   Phase 3 — Check Connectivity
        ///   Phase 4 — Check Reference Counts
        ///   Phase 5 — Check Cylinder Groups
        /// </summary>
        /// <param name="preen">Preen mode: only fix safe inconsistencies (unreferenced inodes,
        /// link count errors, missing blocks in free map, blocks in free map also in files,
        /// wrong superblock counts).</param>
        /// <param name="debug">Print detailed diagnostic information.</param>
        /// <returns>An <see cref="FsckResult"/> containing findings and statistics.</returns>
        public FsckResult FsckUfs(bool preen = false, bool debug = false)
        {
            var result = new FsckResult();
            var sb = Superblock;

            result.Messages.Add($"** Checking {(sb.IsUfs1 ? "UFS1" : "UFS2")} filesystem image: {ImagePath}");

            // Quick superblock sanity checks
            if (!sb.IsValid)
            {
                result.Errors.Add("BAD SUPER BLOCK: MAGIC NUMBER WRONG");
                result.Clean = false;
                return result;
            }

            if (sb.BSize <= 0 || sb.FSize <= 0 || sb.FragsPerBlock <= 0)
            {
                result.Errors.Add("BAD SUPER BLOCK: INVALID BLOCK/FRAGMENT SIZE");
                result.Clean = false;
                return result;
            }

            if (sb.NumCylGroups <= 0 || sb.InodesPerGroup <= 0 || sb.CylGroupSize <= 0)
            {
                result.Errors.Add("BAD SUPER BLOCK: INVALID CYLINDER GROUP PARAMETERS");
                result.Clean = false;
                return result;
            }

            if (sb.TotalBlocks <= 0)
            {
                result.Errors.Add("BAD SUPER BLOCK: INVALID TOTAL SIZE");
                result.Clean = false;
                return result;
            }

            long totalInodesLong = (long)sb.NumCylGroups * sb.InodesPerGroup;
            if (totalInodesLong > int.MaxValue)
                throw new InvalidOperationException(
                    $"Filesystem has too many inodes ({totalInodesLong}) for fsck to handle.");
            int totalInodes = (int)totalInodesLong;
            int inodeSize = sb.IsUfs1 ? Ufs2Constants.Ufs1InodeSize : Ufs2Constants.Ufs2InodeSize;
            long totalFrags = sb.TotalBlocks;
            int fragsPerBlock = sb.FragsPerBlock;

            // Per-inode state tracking
            // inodeState: 0=unallocated, 1=regular file, 2=directory, 3=other allocated
            byte[] inodeState = new byte[totalInodes];
            // linkCounts: observed reference count from directory entries
            int[] observedLinks = new int[totalInodes];
            // storedLinks: link count stored in the inode
            short[] storedLinks = new short[totalInodes];
            // blockMap: tracks which inode uses each block (fragment), 0 = free
            // Using a dictionary for sparse storage (large images would exhaust memory with an array)
            var blockOwner = new Dictionary<long, uint>();

            // ================================================================
            // Phase 1 — Check Blocks and Sizes
            // ================================================================
            result.Messages.Add("** Phase 1 - Check Blocks and Sizes");
            int fileCount = 0;
            int dirCount = 0;
            long blocksInUse = 0;

            for (uint ino = 0; ino < (uint)totalInodes; ino++)
            {
                Ufs2Inode inode;
                try
                {
                    inode = ReadInode(ino);
                }
                catch
                {
                    if (debug)
                        result.Messages.Add($"  Cannot read inode {ino}");
                    continue;
                }

                if (inode.Mode == 0)
                {
                    inodeState[ino] = 0; // unallocated
                    continue;
                }

                storedLinks[ino] = inode.NLink;

                if (inode.IsDirectory)
                {
                    inodeState[ino] = 2;
                    dirCount++;

                    // Directory size must be a multiple of DIRBLKSIZ
                    if (inode.Size % Ufs2Constants.DirBlockSize != 0)
                    {
                        result.Warnings.Add(
                            $"DIRECTORY INODE {ino}: SIZE {inode.Size} NOT MULTIPLE OF DIRBLKSIZ ({Ufs2Constants.DirBlockSize})");
                        result.Clean = false;
                    }
                }
                else if (inode.IsRegularFile)
                {
                    inodeState[ino] = 1;
                    fileCount++;
                }
                else
                {
                    inodeState[ino] = 3; // symlink, device, etc.
                    fileCount++;
                }

                // Validate block pointers
                for (int bi = 0; bi < Ufs2Constants.NDirect; bi++)
                {
                    long blk = inode.DirectBlocks[bi];
                    if (blk == 0) continue;

                    if (blk < 0 || blk >= totalFrags)
                    {
                        result.Errors.Add(
                            $"INODE {ino}: DIRECT BLOCK {bi} OUT OF RANGE ({blk}, max {totalFrags - 1})");
                        result.Clean = false;
                        continue;
                    }

                    // Check for duplicate block claims
                    if (blockOwner.TryGetValue(blk, out uint existingOwner))
                    {
                        result.Errors.Add(
                            $"BLOCK {blk} CLAIMED BY INODE {existingOwner} AND INODE {ino} (DUP)");
                        result.Clean = false;
                    }
                    else
                    {
                        blockOwner[blk] = ino;
                    }
                    blocksInUse++;
                }

                // Validate indirect block pointers (check that the pointers themselves are in range)
                for (int ii = 0; ii < Ufs2Constants.NIndirect; ii++)
                {
                    long iblk = inode.IndirectBlocks[ii];
                    if (iblk == 0) continue;

                    if (iblk < 0 || iblk >= totalFrags)
                    {
                        result.Errors.Add(
                            $"INODE {ino}: INDIRECT BLOCK {ii} OUT OF RANGE ({iblk}, max {totalFrags - 1})");
                        result.Clean = false;
                        continue;
                    }

                    if (blockOwner.TryGetValue(iblk, out uint existOwner))
                    {
                        result.Errors.Add(
                            $"BLOCK {iblk} CLAIMED BY INODE {existOwner} AND INODE {ino} (DUP)");
                        result.Clean = false;
                    }
                    else
                    {
                        blockOwner[iblk] = ino;
                    }
                    blocksInUse++;
                }

                // Check for bad inode format: allocated inode with zero link count
                if (inode.NLink <= 0 && inode.Mode != 0)
                {
                    result.Warnings.Add($"INODE {ino}: ALLOCATED WITH ZERO LINK COUNT");
                    result.Clean = false;
                }
            }

            if (debug)
                result.Messages.Add($"  Phase 1: {fileCount} files, {dirCount} dirs, {blocksInUse} blocks in use");

            // ================================================================
            // Phase 2 — Check Pathnames (directory tree walk)
            // ================================================================
            result.Messages.Add("** Phase 2 - Check Pathnames");
            // Track which inodes are referenced by directory entries
            bool[] referenced = new bool[totalInodes];
            referenced[Ufs2Constants.RootInode] = true; // root is always referenced

            if (inodeState[Ufs2Constants.RootInode] != 2)
            {
                result.Errors.Add("ROOT INODE IS NOT A DIRECTORY");
                result.Clean = false;
            }
            else
            {
                // Walk directory tree from root, checking entries
                var visited = new HashSet<uint>();
                var dirQueue = new Queue<uint>();
                dirQueue.Enqueue(Ufs2Constants.RootInode);
                visited.Add(Ufs2Constants.RootInode);

                while (dirQueue.Count > 0)
                {
                    uint dirIno = dirQueue.Dequeue();
                    List<Ufs2DirectoryEntry> entries;
                    try
                    {
                        entries = ListDirectory(dirIno);
                    }
                    catch
                    {
                        result.Warnings.Add($"CANNOT READ DIRECTORY INODE {dirIno}");
                        result.Clean = false;
                        continue;
                    }

                    // Check "." and ".." are present and correct
                    bool hasDot = false, hasDotDot = false;
                    foreach (var entry in entries)
                    {
                        if (entry.Name == ".") hasDot = true;
                        if (entry.Name == "..") hasDotDot = true;
                    }
                    if (!hasDot)
                    {
                        result.Warnings.Add($"DIRECTORY INODE {dirIno}: MISSING '.' ENTRY");
                        result.Clean = false;
                    }
                    if (!hasDotDot)
                    {
                        result.Warnings.Add($"DIRECTORY INODE {dirIno}: MISSING '..' ENTRY");
                        result.Clean = false;
                    }

                    // Validate "." points to self
                    var dotEntry = entries.Find(e => e.Name == ".");
                    if (dotEntry != null && dotEntry.Inode != dirIno)
                    {
                        result.Warnings.Add(
                            $"DIRECTORY INODE {dirIno}: '.' POINTS TO INODE {dotEntry.Inode} (SHOULD BE {dirIno})");
                        result.Clean = false;
                    }

                    foreach (var entry in entries)
                    {
                        if (entry.Name == "." || entry.Name == "..")
                        {
                            // Count link references for . and ..
                            if (entry.Inode < (uint)totalInodes)
                                observedLinks[entry.Inode]++;
                            continue;
                        }

                        uint childIno = entry.Inode;

                        // Inode number out of range
                        if (childIno >= (uint)totalInodes)
                        {
                            result.Errors.Add(
                                $"DIRECTORY INODE {dirIno}: ENTRY '{entry.Name}' INODE {childIno} OUT OF RANGE");
                            result.Clean = false;
                            continue;
                        }

                        // File pointing to unallocated inode
                        if (inodeState[childIno] == 0)
                        {
                            result.Warnings.Add(
                                $"DIRECTORY INODE {dirIno}: ENTRY '{entry.Name}' REFERENCES UNALLOCATED INODE {childIno}");
                            result.Clean = false;
                            continue;
                        }

                        referenced[childIno] = true;
                        observedLinks[childIno]++;

                        // Recurse into subdirectories (avoiding cycles)
                        if (inodeState[childIno] == 2 && !visited.Contains(childIno))
                        {
                            visited.Add(childIno);
                            dirQueue.Enqueue(childIno);
                        }
                    }
                }
            }

            // ================================================================
            // Phase 3 — Check Connectivity (find orphaned inodes)
            // ================================================================
            result.Messages.Add("** Phase 3 - Check Connectivity");
            int orphanCount = 0;
            for (uint ino = Ufs2Constants.RootInode; ino < (uint)totalInodes; ino++)
            {
                if (inodeState[ino] != 0 && !referenced[ino])
                {
                    orphanCount++;
                    result.Warnings.Add($"UNREF INODE {ino} (MODE=0x{ReadInode(ino).Mode:X4})");
                    result.Clean = false;
                    if (preen)
                        result.Messages.Add($"  RECONNECT? (inode {ino} would go to lost+found)");
                }
            }
            if (debug)
                result.Messages.Add($"  Phase 3: {orphanCount} orphaned inodes");

            // ================================================================
            // Phase 4 — Check Reference Counts
            // ================================================================
            result.Messages.Add("** Phase 4 - Check Reference Counts");
            int linkErrors = 0;
            for (uint ino = Ufs2Constants.RootInode; ino < (uint)totalInodes; ino++)
            {
                if (inodeState[ino] == 0) continue;

                int observed = observedLinks[ino];
                int stored = storedLinks[ino];

                if (observed != stored)
                {
                    linkErrors++;
                    result.Warnings.Add(
                        $"INODE {ino}: LINK COUNT {stored}, SHOULD BE {observed}");
                    result.Clean = false;
                }
            }
            if (debug)
                result.Messages.Add($"  Phase 4: {linkErrors} link count errors");

            // ================================================================
            // Phase 5 — Check Cylinder Groups
            // ================================================================
            result.Messages.Add("** Phase 5 - Check Cylinder Groups");

            long totalFreeBlocksCg = 0;
            long totalFreeInodesCg = 0;
            long totalFreeFragsCg = 0;
            long totalDirsCg = 0;

            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStartFrag = (long)cg * sb.CylGroupSize;
                long cgHeaderOffset = cgStartFrag * sb.FSize + (long)sb.CblkNo * sb.FSize;

                byte[] cgData;
                try
                {
                    cgData = ReadCgBlock(cgHeaderOffset);
                }
                catch
                {
                    result.Errors.Add($"CG {cg}: CANNOT READ CYLINDER GROUP HEADER");
                    result.Clean = false;
                    continue;
                }

                // Check CG magic number
                int cgMagic = ReadCgInt32(cgData, 0x04);
                if (cgMagic != Ufs2Constants.CgMagic)
                {
                    result.Errors.Add(
                        $"CG {cg}: BAD MAGIC NUMBER 0x{cgMagic:X} (expected 0x{Ufs2Constants.CgMagic:X})");
                    result.Clean = false;
                    continue;
                }

                // Check CG index
                int cgIndex = ReadCgInt32(cgData, 0x0C);
                if (cgIndex != cg)
                {
                    result.Warnings.Add(
                        $"CG {cg}: CG INDEX MISMATCH ({cgIndex} != {cg})");
                    result.Clean = false;
                }

                // Read CG summary
                int cgDirs = ReadCgInt32(cgData, 0x18);
                int cgNbfree = ReadCgInt32(cgData, 0x1C);
                int cgNifree = ReadCgInt32(cgData, 0x20);
                int cgNffree = ReadCgInt32(cgData, 0x24);

                totalDirsCg += cgDirs;
                totalFreeBlocksCg += cgNbfree;
                totalFreeInodesCg += cgNifree;
                totalFreeFragsCg += cgNffree;

                // Verify inode bitmap vs actual inode allocation
                int iusedoff = ReadCgInt32(cgData, 0x5C);
                int actualUsed = 0;
                int actualFree = 0;
                for (int i = 0; i < sb.InodesPerGroup; i++)
                {
                    uint globalIno = (uint)((long)cg * sb.InodesPerGroup + i);
                    if (globalIno >= (uint)totalInodes) break;

                    int byteIdx = iusedoff + i / 8;
                    int bitIdx = i % 8;
                    bool bitmapUsed = byteIdx < cgData.Length && (cgData[byteIdx] & (1 << bitIdx)) != 0;
                    bool actuallyUsed = inodeState[globalIno] != 0;

                    if (bitmapUsed && !actuallyUsed)
                    {
                        // Bitmap says used but inode is empty
                        if (debug)
                            result.Messages.Add($"  CG {cg}: INODE {globalIno} BITMAP USED BUT INODE UNALLOCATED");
                        actualUsed++;
                    }
                    else if (bitmapUsed)
                    {
                        actualUsed++;
                    }
                    else if (!bitmapUsed && actuallyUsed)
                    {
                        result.Warnings.Add(
                            $"CG {cg}: INODE {globalIno} ALLOCATED BUT NOT MARKED IN BITMAP");
                        result.Clean = false;
                        actualUsed++;
                    }
                    else
                    {
                        actualFree++;
                    }
                }

                if (actualFree != cgNifree)
                {
                    result.Warnings.Add(
                        $"CG {cg}: FREE INODE COUNT WRONG ({cgNifree} should be {actualFree})");
                    result.Clean = false;
                }
            }

            // Check superblock free counts against CG totals
            if (totalFreeBlocksCg != sb.FreeBlocks)
            {
                result.Warnings.Add(
                    $"FREE BLOCK COUNT WRONG IN SUPERBLOCK (sb={sb.FreeBlocks}, cg total={totalFreeBlocksCg})");
                result.Clean = false;
            }
            if (totalFreeInodesCg != sb.FreeInodes)
            {
                result.Warnings.Add(
                    $"FREE INODE COUNT WRONG IN SUPERBLOCK (sb={sb.FreeInodes}, cg total={totalFreeInodesCg})");
                result.Clean = false;
            }
            if (totalDirsCg != sb.Directories)
            {
                result.Warnings.Add(
                    $"DIRECTORY COUNT WRONG IN SUPERBLOCK (sb={sb.Directories}, cg total={totalDirsCg})");
                result.Clean = false;
            }

            // Compute statistics
            result.Files = fileCount;
            result.Directories = dirCount;
            long totalDataFrags = sb.TotalDataBlocks;
            result.UsedBlocks = totalDataFrags - sb.FreeBlocks * fragsPerBlock - sb.FreeFragments;
            result.FreeBlocks = sb.FreeBlocks * fragsPerBlock + sb.FreeFragments;
            result.UsedInodes = totalInodes - sb.FreeInodes;
            result.FreeInodes = sb.FreeInodes;

            // Fragmentation: ratio of free fragments to total fragments
            long totalFreeFragments = sb.FreeBlocks * fragsPerBlock + sb.FreeFragments;
            if (totalDataFrags > 0 && totalFreeFragments > 0)
            {
                result.Fragmentation = (double)sb.FreeFragments / totalFreeFragments * 100.0;
            }

            // Summary
            if (result.Clean)
            {
                result.Messages.Add($"** {ImagePath} is clean");
            }
            result.Messages.Add($"{result.Files + result.Directories} files, " +
                $"{result.UsedBlocks} used, {result.FreeBlocks} free " +
                $"({result.Fragmentation:F1}% fragmentation)");

            return result;
        }

        /// <summary>
        /// Read exactly the requested number of bytes from the stream.
        /// Unlike Stream.Read, this loops until all bytes are read.
        /// Throws if end-of-stream is reached before all bytes are read.
        /// </summary>
        private void ReadFully(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = _stream.Read(buffer, offset + totalRead, count - totalRead);
                if (bytesRead == 0)
                    throw new EndOfStreamException(
                        $"Unexpected end of stream: expected {count} bytes at offset {_stream.Position - totalRead}, got {totalRead}.");
                totalRead += bytesRead;
            }
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _stream?.Dispose();
        }
    }
}