// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

using System.Runtime.Versioning;

namespace UFS2Tool
{
    /// <summary>
    /// Creates new UFS1/UFS2 filesystem images — equivalent to FreeBSD's "newfs" command.
    /// Supports both image files and raw devices (\\.\PhysicalDriveN, \\.\X:).
    ///
    /// Implements all newfs(8) flags except -T (disktype), -k (held-for-metadata-blocks),
    /// and -r (reserved).
    /// </summary>
    public class Ufs2ImageCreator
    {
        // FreeBSD newfs constraints (from sbin/newfs/mkfs.c)
        public const int MinBlockSize = 4096;
        public const int MaxBlockSize = 65536;
        public const int MinFragSize = 512;
        public const int MaxFragsPerBlock = 8;

        // Estimated bytes per directory entry: header(8) + average name(16) + padding
        private const int EstimatedBytesPerDirEntry = 24;

        // -O filesystem-format: 1 = UFS1, 2 = UFS2 (default)
        public int FilesystemFormat { get; set; } = 2;

        // -b block-size
        public int BlockSize { get; set; } = Ufs2Constants.DefaultBlockSize;
        // -f frag-size
        public int FragmentSize { get; set; } = Ufs2Constants.DefaultFragSize;
        // -S sector-size
        public int SectorSize { get; set; } = Ufs2Constants.DefaultSectorSize;

        // -i bytes-per-inode (0 = auto-calculate)
        public int BytesPerInode { get; set; } = 0;
        // Inodes per group (computed from BytesPerInode or default)
        public int InodesPerGroup { get; set; } = Ufs2Constants.DefaultInodesPerGroup;

        // -L volname
        public string VolumeName { get; set; } = "";
        // -m free-space (percentage)
        public int MinFreePercent { get; set; } = Ufs2Constants.DefaultMinFreePercent;
        // -o optimization ("time" or "space")
        public string OptimizationPreference { get; set; } = "time";

        // -a maxcontig (0 = auto)
        public int MaxContig { get; set; } = 0;
        // -c blocks-per-cylinder-group (0 = auto)
        public int BlocksPerCylGroup { get; set; } = 0;
        // -d max-extent-size (0 = default)
        public int MaxExtentSize { get; set; } = 0;
        // -e maxbpg (0 = auto)
        public int MaxBpg { get; set; } = 0;
        // -g avgfilesize
        public int AvgFileSize { get; set; } = Ufs2Constants.DefaultAvgFileSize;
        // -h avgfpdir
        public int AvgFilesPerDir { get; set; } = Ufs2Constants.DefaultAvgFilesPerDir;
        // -s size (0 = use device/image size)
        public long SizeOverride { get; set; } = 0;

        // Boolean flags
        // -E: erase (TRIM) device contents before creating filesystem
        public bool EraseContents { get; set; } = false;
        // -J: enable gjournal
        public bool Gjournal { get; set; } = false;
        // -N: dry run — do not create, just print parameters
        public bool DryRun { get; set; } = false;
        // -U: enable soft updates
        public bool SoftUpdates { get; set; } = false;
        // -j: enable soft updates journaling (implies -U)
        public bool SoftUpdatesJournal { get; set; } = false;
        // -l: enable multilabel MAC
        public bool MultilabelMac { get; set; } = false;
        // -n: do not create .snap directory
        public bool NoSnapDir { get; set; } = true;
        // -t: enable TRIM/DISCARD flag
        public bool TrimEnabled { get; set; } = false;

        // -p partition (informational only on Windows)
        public string Partition { get; set; } = "";

        // -D input-directory (populate image with directory contents)
        public string InputDirectory { get; set; } = "";

        /// <summary>
        /// Optional TextWriter for log output. When set, log messages are written here
        /// instead of to Console.Out. This allows GUI applications to capture the output
        /// that would normally go to the console during image creation.
        /// </summary>
        public TextWriter? Output { get; set; }

        /// <summary>
        /// Optional TextWriter for error/warning output. When set, warning messages are
        /// written here instead of to Console.Error.
        /// </summary>
        public TextWriter? ErrorOutput { get; set; }

        /// <summary>
        /// Get the on-disk inode size for the selected filesystem format.
        /// </summary>
        private int InodeSizeForFormat =>
            (FilesystemFormat == 1) ? Ufs2Constants.Ufs1InodeSize : Ufs2Constants.Ufs2InodeSize;

        /// <summary>
        /// Get the magic number for the selected filesystem format.
        /// </summary>
        private int MagicForFormat =>
            (FilesystemFormat == 1) ? Ufs2Constants.Ufs1Magic : Ufs2Constants.Ufs2Magic;

        /// <summary>
        /// Validates the block size and fragment size parameters against FreeBSD newfs constraints.
        /// </summary>
        public void ValidateParameters()
        {
            if (FilesystemFormat != 1 && FilesystemFormat != 2)
                throw new ArgumentException(
                    $"Filesystem format must be 1 (UFS1) or 2 (UFS2). Got: {FilesystemFormat}");

            if (!IsPowerOfTwo(BlockSize))
                throw new ArgumentException(
                    $"Block size must be a power of 2. Got: {BlockSize}");

            if (BlockSize < MinBlockSize || BlockSize > MaxBlockSize)
                throw new ArgumentException(
                    $"Block size must be between {MinBlockSize} and {MaxBlockSize} bytes. Got: {BlockSize}");

            if (!IsPowerOfTwo(FragmentSize))
                throw new ArgumentException(
                    $"Fragment size must be a power of 2. Got: {FragmentSize}");

            if (FragmentSize < MinFragSize)
                throw new ArgumentException(
                    $"Fragment size must be at least {MinFragSize} bytes. Got: {FragmentSize}");

            if (FragmentSize > BlockSize)
                throw new ArgumentException(
                    $"Fragment size ({FragmentSize}) cannot exceed block size ({BlockSize}).");

            int ratio = BlockSize / FragmentSize;
            if (ratio > MaxFragsPerBlock)
                throw new ArgumentException(
                    $"Block size / fragment size ratio must be 1, 2, 4, or 8. " +
                    $"Got: {BlockSize} / {FragmentSize} = {ratio}");

            if (!IsPowerOfTwo(ratio))
                throw new ArgumentException(
                    $"Block size must be an exact multiple (1x, 2x, 4x, or 8x) of fragment size. " +
                    $"Got: {BlockSize} / {FragmentSize} = {ratio}");

            if (!IsPowerOfTwo(SectorSize) || SectorSize < 512)
                throw new ArgumentException(
                    $"Sector size must be a power of 2, at least 512 bytes. Got: {SectorSize}");

            if (FragmentSize < SectorSize)
                throw new ArgumentException(
                    $"Fragment size ({FragmentSize}) cannot be smaller than sector size ({SectorSize}).");

            if (MinFreePercent < 0 || MinFreePercent > 99)
                throw new ArgumentException(
                    $"Minimum free space must be between 0% and 99%. Got: {MinFreePercent}%");

            if (BytesPerInode < 0)
                throw new ArgumentException(
                    $"Bytes per inode must be non-negative. Got: {BytesPerInode}");

            if (AvgFileSize <= 0)
                throw new ArgumentException(
                    $"Average file size must be positive. Got: {AvgFileSize}");

            if (AvgFilesPerDir <= 0)
                throw new ArgumentException(
                    $"Average files per directory must be positive. Got: {AvgFilesPerDir}");
        }

        /// <summary>
        /// Mirror FreeBSD's newfs.c: when the user-specified sector size is not
        /// DEV_BSIZE (512), save it as "realsectorsize" (used only for physical I/O
        /// alignment on raw devices) and normalize the filesystem-layout sectorsize
        /// to DEV_BSIZE. This guarantees that fs_fsbtodb, fs_old_nspf, the on-disk
        /// recovery-block location, and the interpretation of -s size all match the
        /// values that FreeBSD's native newfs(8) would produce — regardless of -S.
        ///
        /// FreeBSD newfs.c (sbin/newfs/newfs.c) does exactly:
        ///     realsectorsize = sectorsize;
        ///     if (sectorsize != DEV_BSIZE) {
        ///         int secperblk = sectorsize / DEV_BSIZE;
        ///         sectorsize = DEV_BSIZE;
        ///         fssize *= secperblk;
        ///     }
        /// </summary>
        /// <returns>The user-specified ("real") sector size, used for raw-device I/O alignment.</returns>
        private int NormalizeSectorSizeForLayout()
        {
            int realSectorSize = SectorSize;
            if (SectorSize != Ufs2Constants.DefaultSectorSize)
            {
                // ValidateParameters() (called by the public CreateImage / CreateOnDevice
                // entry points before this method) guarantees that SectorSize is a power
                // of 2 >= DEV_BSIZE, so the division below is always exact. Assert this
                // invariant defensively in case the call order ever changes.
                System.Diagnostics.Debug.Assert(
                    SectorSize % Ufs2Constants.DefaultSectorSize == 0,
                    $"SectorSize ({SectorSize}) must be a multiple of DEV_BSIZE ({Ufs2Constants.DefaultSectorSize}); " +
                    "ValidateParameters() must run before NormalizeSectorSizeForLayout().");

                int secPerBlk = SectorSize / Ufs2Constants.DefaultSectorSize;
                if (SizeOverride > 0)
                    SizeOverride *= secPerBlk;
                SectorSize = Ufs2Constants.DefaultSectorSize;
            }
            return realSectorSize;
        }

        /// <summary>
        /// Compute the filesystem flags from the boolean options.
        /// </summary>
        private int ComputeFlags()
        {
            int flags = 0;

            // -j implies -U
            if (SoftUpdatesJournal)
                SoftUpdates = true;

            if (SoftUpdates)
                flags |= Ufs2Constants.FsDosoftdep;
            if (SoftUpdatesJournal)
                flags |= Ufs2Constants.FsSuj;
            if (Gjournal)
                flags |= Ufs2Constants.FsGjournal;
            if (MultilabelMac)
                flags |= Ufs2Constants.FsMultilabel;
            if (TrimEnabled)
                flags |= Ufs2Constants.FsTrim;

            return flags;
        }

        /// <summary>
        /// Create a new UFS filesystem on a raw device (e.g., \\.\PhysicalDrive2).
        /// Automatically detects the device size and sector size.
        /// Equivalent to: newfs -b blocksize -f fragsize /dev/sdX
        /// REQUIRES ADMINISTRATOR PRIVILEGES.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public void CreateOnDevice(string devicePath)
        {
            if (!DriveIO.IsDevicePath(devicePath))
                throw new ArgumentException(
                    $"'{devicePath}' is not a valid device path. " +
                    @"Expected format: \\.\PhysicalDriveN or \\.\X:");

            // Query device properties
            long deviceSize = DriveIO.GetDeviceSize(devicePath);
            int physicalSectorSize = DriveIO.GetSectorSize(devicePath);

            // Override sector size with the physical sector size of the device
            if (physicalSectorSize > SectorSize)
            {
                (Output ?? Console.Out).WriteLine(
                    $"Note: Device sector size ({physicalSectorSize}) is larger than " +
                    $"configured ({SectorSize}). Using device sector size.");
                SectorSize = physicalSectorSize;
            }

            (Output ?? Console.Out).WriteLine($"Device:      {devicePath}");
            (Output ?? Console.Out).WriteLine($"Device size: {deviceSize:N0} bytes ({deviceSize / (1024 * 1024)} MB)");
            (Output ?? Console.Out).WriteLine($"Sector size: {SectorSize} bytes");

            ValidateParameters();

            // Mirror FreeBSD newfs(8): the on-disk filesystem layout always uses
            // DEV_BSIZE (512-byte) units. The user-specified -S (or auto-detected
            // physical sector size) is preserved as realSectorSize for raw-device
            // I/O alignment only. Restore the original SectorSize in finally so
            // that callers/tests observe the value they configured.
            int origSectorSize = SectorSize;
            long origSizeOverride = SizeOverride;
            int realSectorSize = NormalizeSectorSizeForLayout();

            // -s size: per FreeBSD, the value is in real-sector units. After
            // NormalizeSectorSizeForLayout(), SizeOverride has already been
            // multiplied by secperblk so that (SizeOverride * DEV_BSIZE) yields
            // the correct byte count.
            long totalSizeBytes;
            if (SizeOverride > 0)
            {
                totalSizeBytes = SizeOverride * Ufs2Constants.DefaultSectorSize;
                if (totalSizeBytes > deviceSize)
                    throw new ArgumentException(
                        $"Specified size ({totalSizeBytes:N0} bytes) exceeds device size ({deviceSize:N0} bytes).");
            }
            else
            {
                totalSizeBytes = deviceSize;
            }

            try
            {

                if (totalSizeBytes < BlockSize * 16)
                    throw new ArgumentException(
                        $"Device too small ({totalSizeBytes:N0} bytes). " +
                        $"Minimum: {BlockSize * 16:N0} bytes (16 blocks).");

                // Align total size down to fragment boundary
                totalSizeBytes = (totalSizeBytes / FragmentSize) * FragmentSize;

                (Output ?? Console.Out).WriteLine($"Usable size: {totalSizeBytes:N0} bytes (aligned to {FragmentSize}-byte fragments)");
                (Output ?? Console.Out).WriteLine();

                PrintNewfsParameters(totalSizeBytes, realSectorSize);

                if (DryRun)
                {
                    (Output ?? Console.Out).WriteLine("Dry run (-N): filesystem was NOT created.");
                    return;
                }

                // Open device with locking. The AlignedStream uses realSectorSize so
                // that all physical writes meet the device's hardware alignment
                // requirements (e.g., 4 KB sectors on AF disks), even though the
                // on-disk filesystem layout is computed in 512-byte units.
                using var deviceStream = DriveIO.OpenDeviceStream(devicePath, readOnly: false, lockVolume: true);
                using var aligned = new AlignedStream(deviceStream, realSectorSize, deviceSize);

                // Erase device if -E flag set
                if (EraseContents)
                {
                    (Output ?? Console.Out).WriteLine("Erasing device contents...");
                    long eraseSize = Math.Min(totalSizeBytes, deviceSize);
                    long eraseAligned = (eraseSize / realSectorSize) * realSectorSize;
                    aligned.WriteZeros(0, eraseAligned);
                    (Output ?? Console.Out).WriteLine("Erase complete.");
                }

                // Build the filesystem structures in memory, then write sector-aligned
                WriteFilesystem(aligned, totalSizeBytes);

                aligned.Flush();
                (Output ?? Console.Out).WriteLine("Filesystem written successfully to device.");
            }
            finally
            {
                SectorSize = origSectorSize;
                SizeOverride = origSizeOverride;
            }
        }

        /// <summary>
        /// Create a new UFS image file with the given total size.
        /// Equivalent to: newfs -b blocksize -f fragsize on a file-backed md device.
        /// </summary>
        public void CreateImage(string imagePath, long totalSizeBytes)
        {
            ValidateParameters();

            // Mirror FreeBSD newfs(8): the on-disk filesystem layout always uses
            // DEV_BSIZE (512-byte) units. The user-specified -S is preserved as
            // realSectorSize but is only relevant for raw-device I/O alignment.
            // For an image file the AlignedStream can use DEV_BSIZE directly.
            // Restore SectorSize/SizeOverride in finally so that callers/tests
            // observe the values they configured.
            int origSectorSize = SectorSize;
            long origSizeOverride = SizeOverride;
            int realSectorSize = NormalizeSectorSizeForLayout();

            try
            {
                // Apply -s size override. After normalization SizeOverride is in
                // DEV_BSIZE units, so (SizeOverride * 512) gives the correct byte
                // count regardless of the user's -S setting.
                if (SizeOverride > 0)
                    totalSizeBytes = SizeOverride * Ufs2Constants.DefaultSectorSize;

                if (totalSizeBytes < BlockSize * 16)
                    throw new ArgumentException(
                        $"Image too small. Minimum size is {BlockSize * 16} bytes (16 blocks of {BlockSize} bytes).");

                if (totalSizeBytes % FragmentSize != 0)
                    throw new ArgumentException(
                        $"Image size ({totalSizeBytes}) must be a multiple of fragment size ({FragmentSize}).");

                PrintNewfsParameters(totalSizeBytes, realSectorSize);

                if (DryRun)
                {
                    (Output ?? Console.Out).WriteLine("Dry run (-N): filesystem was NOT created.");
                    return;
                }

                using var fs = new FileStream(imagePath, FileMode.Create, FileAccess.ReadWrite);
                fs.SetLength(totalSizeBytes);

                using var aligned = new AlignedStream(fs, SectorSize, totalSizeBytes);
                WriteFilesystem(aligned, totalSizeBytes);

                aligned.Flush();
            }
            finally
            {
                SectorSize = origSectorSize;
                SizeOverride = origSizeOverride;
            }
        }

        /// <summary>
        /// Print the computed filesystem parameters (always shown, required for -N dry run).
        /// </summary>
        /// <param name="realSectorSize">
        /// The user-specified ("real") sector size to display, before normalization
        /// to DEV_BSIZE. Defaults to the current SectorSize when not provided.
        /// </param>
        private void PrintNewfsParameters(long totalSizeBytes, int realSectorSize = 0)
        {
            if (realSectorSize <= 0) realSectorSize = SectorSize;
            int fragsPerBlock = BlockSize / FragmentSize;
            long totalFrags = totalSizeBytes / FragmentSize;
            int inodeSize = InodeSizeForFormat;
            int inodesPerGroup = ComputeInodesPerGroup(totalSizeBytes);

            int fragsPerGroup = inodesPerGroup * fragsPerBlock;
            int numCylGroups = (int)((totalFrags + fragsPerGroup - 1) / fragsPerGroup);
            if (numCylGroups < 1) numCylGroups = 1;

            fragsPerGroup = (int)(totalFrags / numCylGroups);
            fragsPerGroup = (fragsPerGroup / fragsPerBlock) * fragsPerBlock;

            string formatStr = (FilesystemFormat == 1) ? "UFS1" : "UFS2";
            (Output ?? Console.Out).WriteLine($"  Format:        {formatStr} (-O {FilesystemFormat})");
            (Output ?? Console.Out).WriteLine($"  Block size:    {BlockSize} bytes (-b)");
            (Output ?? Console.Out).WriteLine($"  Fragment size: {FragmentSize} bytes (-f)");
            (Output ?? Console.Out).WriteLine($"  Sector size:   {realSectorSize} bytes (-S)");
            (Output ?? Console.Out).WriteLine($"  Inode size:    {inodeSize} bytes");
            (Output ?? Console.Out).WriteLine($"  Total size:    {totalSizeBytes:N0} bytes ({totalSizeBytes / (1024 * 1024)} MB)");
            (Output ?? Console.Out).WriteLine($"  Cylinder groups: {numCylGroups}");
            (Output ?? Console.Out).WriteLine($"  Frags/group:   {fragsPerGroup}");
            (Output ?? Console.Out).WriteLine($"  Inodes/group:  {inodesPerGroup}");
            (Output ?? Console.Out).WriteLine($"  Min free:      {MinFreePercent}% (-m)");
            (Output ?? Console.Out).WriteLine($"  Optimization:  {OptimizationPreference} (-o)");

            int flags = ComputeFlags();
            if (flags != 0)
            {
                var flagNames = new System.Collections.Generic.List<string>();
                if ((flags & Ufs2Constants.FsDosoftdep) != 0) flagNames.Add("SOFTUPDATES");
                if ((flags & Ufs2Constants.FsSuj) != 0) flagNames.Add("SUJ");
                if ((flags & Ufs2Constants.FsGjournal) != 0) flagNames.Add("GJOURNAL");
                if ((flags & Ufs2Constants.FsMultilabel) != 0) flagNames.Add("MULTILABEL");
                if ((flags & Ufs2Constants.FsTrim) != 0) flagNames.Add("TRIM");
                (Output ?? Console.Out).WriteLine($"  Flags:         {string.Join(", ", flagNames)}");
            }

            if (!string.IsNullOrEmpty(VolumeName))
                (Output ?? Console.Out).WriteLine($"  Volume name:   {VolumeName} (-L)");

            (Output ?? Console.Out).WriteLine();
        }

        /// <summary>
        /// Compute inodes per group based on BytesPerInode or defaults.
        /// Follows FreeBSD's newfs logic for -i bytes-per-inode.
        /// </summary>
        private int ComputeInodesPerGroup(long totalSizeBytes)
        {
            if (BytesPerInode > 0)
            {
                // User specified -i bytes-per-inode
                long totalInodes = totalSizeBytes / BytesPerInode;
                int fragsPerBlock = BlockSize / FragmentSize;
                int fragsPerGroup = InodesPerGroup * fragsPerBlock;
                long totalFrags = totalSizeBytes / FragmentSize;
                int numCylGroups = (int)((totalFrags + fragsPerGroup - 1) / fragsPerGroup);
                if (numCylGroups < 1) numCylGroups = 1;

                int ipg = (int)(totalInodes / numCylGroups);
                int inodeSize = InodeSizeForFormat;

                // Round up to fill a complete block of inodes
                int inodesPerBlock = BlockSize / inodeSize;
                if (inodesPerBlock > 0)
                    ipg = ((ipg + inodesPerBlock - 1) / inodesPerBlock) * inodesPerBlock;

                return Math.Max(ipg, inodesPerBlock);
            }

            return InodesPerGroup;
        }

        /// <summary>
        /// Core filesystem creation logic — writes UFS1/UFS2 structures to any target
        /// (image file or raw device) via the AlignedStream abstraction.
        /// </summary>
        private void WriteFilesystem(AlignedStream target, long totalSizeBytes)
        {
            int fragsPerBlock = BlockSize / FragmentSize;
            long totalFrags = totalSizeBytes / FragmentSize;
            int inodeSize = InodeSizeForFormat;
            int magic = MagicForFormat;

            int inodesPerGroup = ComputeInodesPerGroup(totalSizeBytes);

            // Calculate cylinder groups
            int fragsPerGroup;
            if (BlocksPerCylGroup > 0)
            {
                fragsPerGroup = BlocksPerCylGroup * fragsPerBlock;
            }
            else
            {
                fragsPerGroup = inodesPerGroup * fragsPerBlock;
            }
            int numCylGroups = (int)((totalFrags + fragsPerGroup - 1) / fragsPerGroup);
            if (numCylGroups < 1) numCylGroups = 1;

            int bShift = Log2(BlockSize);
            int fShift = Log2(FragmentSize);

            // Compute CG layout offsets per FreeBSD mkfs.c formulas
            // fs_sblkno: first fragment after boot area + superblock, block-aligned
            int sblkno = AlignUpInt(
                (Ufs2Constants.SuperblockOffset64K + Ufs2Constants.SuperblockSize + FragmentSize - 1) / FragmentSize,
                fragsPerBlock);
            // fs_cblkno: CG header follows superblock backup area
            int cblkno = sblkno + AlignUpInt(
                (Ufs2Constants.SuperblockSize + FragmentSize - 1) / FragmentSize,
                fragsPerBlock);
            // fs_iblkno: inode blocks follow CG header (1 block)
            int iblkno = cblkno + fragsPerBlock;
            // Inode blocks per CG
            int inopb = BlockSize / inodeSize;
            int inodeblks = ((inodesPerGroup + inopb - 1) / inopb) * fragsPerBlock;
            // fs_dblkno: data blocks follow inode blocks
            int dblkno = iblkno + inodeblks;

            // fs_maxbsize: maximum block size (MAXBSIZE = 65536 per FreeBSD)
            int maxBSize = Ufs2Constants.MaxBSize;
            // fs_maxcontig: default is maxbsize/bsize per FreeBSD mkfs.c
            int maxContig = MaxContig > 0 ? MaxContig : Math.Max(1, maxBSize / BlockSize);

            // fs_cgsize: CG header size rounded up to fragment boundary
            // Matches FreeBSD CGSIZE() macro from sys/ufs/ffs/fs.h
            // CGSIZE = sizeof(struct cg) + fs_old_cpg*sizeof(int32) + fs_old_cpg*sizeof(uint16)
            //        + howmany(fs_ipg,8) + howmany(fs_fpg,8) + sizeof(int32)
            //        + (contigsumsize>0 ? contigsumsize*sizeof(int32) + howmany(fpg/frag,8) : 0)
            int cgFixedHeaderSize = Ufs2Constants.CgHeaderBaseSize;
            // For UFS2, fs_old_cpg = 0 (not used); for UFS1, fs_old_cpg = 1
            int oldCpg = (FilesystemFormat == 1) ? 1 : 0;
            int oldBtotSize = oldCpg * 4; // fs_old_cpg * sizeof(int32_t)
            int oldBoffSize = oldCpg * 2; // fs_old_cpg * sizeof(uint16_t)
            int inodeBitmapSize = (inodesPerGroup + 7) / 8;
            int fragBitmapSize = (fragsPerGroup + 7) / 8;
            int contigSumSize = Math.Min(maxContig, Ufs2Constants.MaxContig);
            int cgSizeRaw = cgFixedHeaderSize + oldBtotSize + oldBoffSize
                          + inodeBitmapSize + fragBitmapSize + 4; // +4 = sizeof(int32_t) per CGSIZE
            if (contigSumSize > 0)
            {
                int blocksPerGroup = fragsPerGroup / fragsPerBlock;
                cgSizeRaw += contigSumSize * 4 + (blocksPerGroup + 7) / 8;
            }
            int cgSize = AlignUpInt(cgSizeRaw, FragmentSize);

            // fs_cssize: cylinder group summary size
            int csumStructSize = Ufs2Constants.CsumStructSize;
            int csSize = AlignUpInt(numCylGroups * csumStructSize, FragmentSize);

            // fs_csaddr: address of CG summary area (first data block of CG 0)
            long csAddr = dblkno;
            // Number of fragments occupied by CG summary data (fragment-aligned).
            // Per FreeBSD mkfs.c: csfrags = howmany(cssize, fsize).
            // fsck_ffs pass1.c marks exactly this many fragments as used for the
            // CG summary area, so the bitmap must match.
            int csFrags = (csSize + FragmentSize - 1) / FragmentSize;
            // Block-aligned CG summary size — subsequent data blocks (root dir)
            // must start at block boundaries.
            int csFragsBlk = ((csSize + BlockSize - 1) / BlockSize) * fragsPerBlock;
            // Tail-free fragments in the CG summary's last partial block.
            // Per FreeBSD initcg(): if dupper (= dblkno + csFrags) is not block-
            // aligned, the remaining fragments to the next block boundary are FREE.
            int csSummaryTailFree = csFragsBlk - csFrags;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int fsFlags = ComputeFlags();
            int optimVal = OptimizationPreference.Equals("space", StringComparison.OrdinalIgnoreCase)
                ? Ufs2Constants.FsOptSpace : Ufs2Constants.FsOptTime;

            // Generate filesystem ID from timestamp
            int fsId0 = (int)(now & 0xFFFFFFFF);
            int fsId1 = (int)((now >> 16) ^ Environment.ProcessId);

            int bmask = ~(BlockSize - 1);
            int fmask = ~(FragmentSize - 1);
            int ptrSize = (magic == Ufs2Constants.Ufs1Magic) ? 4 : 8;
            int maxSymlinkLen = (Ufs2Constants.NDirect + Ufs2Constants.NIndirect) * ptrSize;

            // Compute fs_maxfilesize per FreeBSD mkfs.c
            int nindir = BlockSize / ptrSize;
            long maxFileSize = (long)BlockSize * Ufs2Constants.NDirect - 1;
            long sizepb = BlockSize;
            for (int i = 0; i < Ufs2Constants.NIndirect; i++)
            {
                sizepb *= nindir;
                maxFileSize += sizepb;
            }

            var superblock = new Ufs2Superblock
            {
                Magic = magic,
                BSize = BlockSize,
                FSize = FragmentSize,
                FragsPerBlock = fragsPerBlock,
                NumCylGroups = numCylGroups,
                CylGroupSize = fragsPerGroup,
                InodesPerGroup = inodesPerGroup,
                TotalBlocks = totalFrags,
                // fs_dsize: per FreeBSD mkfs.c:
                // fs_dsize = fs_size - sblkno - ncg*(dblkno - sblkno) - csfrags
                // where csfrags = howmany(cssize, fsize) (fragment-aligned)
                TotalDataBlocks = totalFrags - sblkno -
                                  (long)numCylGroups * (dblkno - sblkno) -
                                  csFrags,
                FreeBlocks = 0,
                FreeFragments = 0,
                // 3 inodes used in CG 0: reserved 0, reserved 1, root inode 2
                FreeInodes = numCylGroups * inodesPerGroup - 3,
                Directories = 1,
                Time = now,
                VolumeName = VolumeName,
                MountPoint = "/",
                BShift = bShift,
                FShift = fShift,
                BMask = bmask,
                FMask = fmask,
                FragShift = Log2(fragsPerBlock),
                SuperblockLocation = sblkno,
                CblkNo = cblkno,
                IblkNo = iblkno,
                DblkNo = dblkno,
                CgSize = cgSize,
                CsSize = csSize,
                CsAddr = csAddr,
                InodeSize = inodeSize,
                SectorSize = SectorSize,
                Flags = fsFlags,
                FsId0 = fsId0,
                FsId1 = fsId1,
                MinFreePercent = MinFreePercent,
                Optimization = optimVal,
                MaxContig = maxContig,
                MaxBpg = MaxBpg > 0 ? MaxBpg : inodesPerGroup,
                AvgFileSize = AvgFileSize,
                AvgFilesPerDir = AvgFilesPerDir,
                MaxSymlinkLen = maxSymlinkLen,
                QBMask = ~((long)bmask),
                QFMask = ~((long)fmask),
                OldInodeFmt = Ufs2Constants.Fs44InodeFmt,
                MaxFileSize = maxFileSize,
                SbBlockLoc = Ufs2Constants.SuperblockOffset64K,
                MaxBSize = maxBSize,
                ContigSumSize = contigSumSize,
                // fs_providersize: size of underlying provider in fragments
                // Per FreeBSD newfs: dbtofsb(mediasize / sectorsize)
                ProviderSize = totalFrags,
                // fs_metaspace: blocks reserved for metadata
                // Per FreeBSD newfs: blknum(fpg * minfree / 200)
                MetaSpace = ((long)fragsPerGroup * MinFreePercent / 200 / fragsPerBlock) * fragsPerBlock,
            };

            // Write primary superblock
            byte[] sbData = SerializeSuperblock(superblock);
            target.WriteAligned(sbData, Ufs2Constants.SuperblockOffset64K);

            // Write UFS2 recovery information block (struct fsrecovery).
            // Per FreeBSD sbin/newfs/mkfs.c: the last 20 bytes of the sector
            // immediately before the superblock contain recovery information
            // that allows fsck to reconstruct a damaged superblock.
            if (FilesystemFormat == 2)
            {
                WriteRecoveryBlock(target, superblock, sblkno);
            }

            // Write cylinder groups and collect per-CG summaries for the CG summary area
            long totalFreeDataFrags = 0;
            long totalFreeBlocks = 0;
            long totalFreeFragRem = 0;
            byte[] csSummaryData = new byte[csFragsBlk * FragmentSize];
            for (int cg = 0; cg < numCylGroups; cg++)
            {
                long cgStartFrag = (long)cg * fragsPerGroup;
                long cgStartByte = cgStartFrag * FragmentSize;

                int usableFragsInCg = fragsPerGroup;
                if (cg == numCylGroups - 1)
                    usableFragsInCg = (int)(totalFrags - cgStartFrag);

                int dataFragsInCg = usableFragsInCg - dblkno;
                // CG 0: CG summary area (block-aligned) + root directory use space
                if (cg == 0)
                    dataFragsInCg -= csFragsBlk + fragsPerBlock;
                totalFreeDataFrags += dataFragsInCg;

                // CG header at cgtod(fs, cg) = cgstart + cblkno
                long cgHeaderOffset = cgStartByte + (long)cblkno * FragmentSize;

                byte[] cgData = SerializeCylinderGroupHeader(
                    cg, usableFragsInCg, dataFragsInCg, inodesPerGroup, now,
                    dblkno, fragsPerBlock, inodeSize, contigSumSize,
                    fragsPerGroup, csFrags, csFragsBlk, sblkno);
                target.WriteAligned(cgData, cgHeaderOffset);

                // Write per-CG summary (struct csum) into the CG summary area
                int usedInodes = (cg == 0) ? 3 : 0;
                int freeInodesInCg = inodesPerGroup - usedInodes;
                int dirs = (cg == 0) ? 1 : 0;
                // Per FreeBSD mkfs.c initcg():
                // For CG > 0: blocks [0, sblkno) are also free (both regions are block-aligned).
                // For CG 0: CG summary tail fragments are free but NOT block-aligned,
                // so they must be counted separately as free fragments, not combined
                // with the data area before computing free blocks.
                long freeBlocks;
                int freeFragsRem;
                if (cg == 0)
                {
                    // Data area starts at block boundary; CG summary tail does NOT
                    freeBlocks = dataFragsInCg / fragsPerBlock;
                    freeFragsRem = csSummaryTailFree + (dataFragsInCg % fragsPerBlock);
                }
                else
                {
                    // Both boot area [0, sblkno) and data area start at block boundaries
                    int totalFreeFragsInCg = dataFragsInCg + sblkno;
                    freeBlocks = totalFreeFragsInCg / fragsPerBlock;
                    freeFragsRem = totalFreeFragsInCg % fragsPerBlock;
                }
                totalFreeBlocks += freeBlocks;
                totalFreeFragRem += freeFragsRem;
                int csOffset = cg * Ufs2Constants.CsumStructSize;
                if (csOffset + Ufs2Constants.CsumStructSize <= csSummaryData.Length)
                {
                    using var csMs = new MemoryStream(csSummaryData, csOffset, Ufs2Constants.CsumStructSize, writable: true);
                    using var csW = new BinaryWriter(csMs);
                    csW.Write(dirs);               // cs_ndir
                    csW.Write((int)freeBlocks);    // cs_nbfree
                    csW.Write(freeInodesInCg);     // cs_nifree
                    csW.Write(freeFragsRem);       // cs_nffree
                }

                // Inode table at cgimin(fs, cg) = cgstart + iblkno
                long inodeTableOffset = cgStartByte + (long)iblkno * FragmentSize;

                byte[] inodeTableData = SerializeInodeTable(
                    cg, inodesPerGroup, now, cgStartFrag + dblkno + csFragsBlk, inodeSize);
                target.WriteAligned(inodeTableData, inodeTableOffset);

                // CG 0: write CG summary area and root directory data
                if (cg == 0)
                {
                    // Write CG summary area at csAddr (= dblkno); data filled below after loop
                    target.WriteAligned(csSummaryData, cgStartByte + (long)dblkno * FragmentSize);

                    // Root directory data block follows block-aligned CG summary
                    long rootDataOffset = (cgStartFrag + dblkno + csFragsBlk) * FragmentSize;
                    byte[] rootDirData = SerializeRootDirectoryBlock();
                    target.WriteAligned(rootDirData, rootDataOffset);
                }

                // Backup superblock at cgsblock(fs, cg) = cgstart + sblkno
                if (cg > 0)
                {
                    long backupSbOffset = cgStartByte + (long)sblkno * FragmentSize;
                    if (backupSbOffset + Ufs2Constants.SuperblockSize <=
                        cgStartByte + (long)usableFragsInCg * FragmentSize)
                    {
                        target.WriteAligned(sbData, backupSbOffset);
                    }
                }

                // Progress feedback for large devices
                if (numCylGroups > 10 && (cg + 1) % (numCylGroups / 10) == 0)
                {
                    int pct = (int)((cg + 1) * 100L / numCylGroups);
                    (Output ?? Console.Out).Write($"\r  Writing cylinder groups... {pct}%");
                }
            }

            if (numCylGroups > 10)
                (Output ?? Console.Out).WriteLine($"\r  Writing cylinder groups... 100%");

            // Write completed CG summary area at CsAddr (CG 0 data region)
            target.WriteAligned(csSummaryData, (long)dblkno * FragmentSize);

            // Update superblock with final free counts (sum of per-CG values)
            superblock.FreeBlocks = totalFreeBlocks;
            superblock.FreeFragments = totalFreeFragRem;
            // Note: NumClusters is intentionally left at 0 in fs_cstotal.
            // FreeBSD's fsck_ffs pass5.c uses memcmp on the entire struct csum_total
            // but only accumulates cs_ndir/cs_nbfree/cs_nifree/cs_nffree — cs_numclusters
            // stays 0. FreeBSD's newfs also does not set cs_numclusters in fs_cstotal.
            sbData = SerializeSuperblock(superblock);
            target.WriteAligned(sbData, Ufs2Constants.SuperblockOffset64K);

            // Update backup superblocks with final counts
            for (int cg = 1; cg < numCylGroups; cg++)
            {
                long cgStartByte = (long)cg * fragsPerGroup * FragmentSize;
                long backupSbOffset = cgStartByte + (long)sblkno * FragmentSize;
                int usable = fragsPerGroup;
                if (cg == numCylGroups - 1)
                    usable = (int)(totalFrags - (long)cg * fragsPerGroup);
                if (backupSbOffset + Ufs2Constants.SuperblockSize <=
                    cgStartByte + (long)usable * FragmentSize)
                {
                    target.WriteAligned(sbData, backupSbOffset);
                }
            }
        }

        // --- Serialization helpers (produce byte arrays for aligned writes) ---

        private byte[] SerializeSuperblock(Ufs2Superblock sb)
        {
            using var ms = new MemoryStream(Ufs2Constants.SuperblockSize);
            using var writer = new BinaryWriter(ms);
            sb.WriteTo(writer);

            // Pad to sector alignment
            byte[] data = ms.ToArray();
            int aligned = AlignUpInt(data.Length, SectorSize);
            if (aligned > data.Length)
                Array.Resize(ref data, aligned);
            return data;
        }

        /// <summary>
        /// Write the UFS2 recovery information block (struct fsrecovery).
        /// Per FreeBSD sbin/newfs/mkfs.c: the last 20 bytes of the sector immediately
        /// before the UFS2 superblock contain key parameters that allow fsck_ffs to
        /// reconstruct a damaged superblock.
        /// struct fsrecovery { fsr_magic, fsr_fpg, fsr_fsbtodb, fsr_sblkno, fsr_ncg }
        /// </summary>
        private void WriteRecoveryBlock(AlignedStream target, Ufs2Superblock sb, int sblkno)
        {
            // The recovery block is in the last sector before SBLOCK_UFS2.
            // struct fsrecovery occupies the last 20 bytes of that sector.
            int recoveryBlockSize = 20; // 5 × int32
            long sectorBeforeSb = Ufs2Constants.SuperblockOffset64K - SectorSize;
            if (sectorBeforeSb < 0) return;

            byte[] sector = new byte[SectorSize];
            // Write struct fsrecovery at the end of the sector
            int offset = SectorSize - recoveryBlockSize;
            using var ms = new MemoryStream(sector);
            ms.Position = offset;
            using var bw = new BinaryWriter(ms);
            bw.Write(sb.Magic);           // fsr_magic
            bw.Write(sb.CylGroupSize);    // fsr_fpg
            // fsr_fsbtodb: log2(fsize / sectorsize), guarding against uninitialized SectorSize
            int sectorSz = sb.SectorSize > 0 ? sb.SectorSize : Ufs2Constants.DefaultSectorSize;
            bw.Write(Log2(sb.FSize / sectorSz)); // fsr_fsbtodb
            bw.Write(sblkno);             // fsr_sblkno
            bw.Write(sb.NumCylGroups);    // fsr_ncg

            target.WriteAligned(sector, sectorBeforeSb);
        }

        private byte[] SerializeCylinderGroupHeader(int cgIndex, int totalFragsInCg,
            int freeDataFrags, int inodesInCg, long timestamp, int dataStartFrag,
            int fragsPerBlock, int inodeSize, int contigSumSize,
            int fragsPerGroup, int csFrags, int csFragsBlk, int sblkno)
        {
            // Compute bitmap sizes
            // Per FreeBSD mkfs.c/fsck_ffs, bitmap sizes always use fs_fpg/fs_ipg
            // (fragsPerGroup/inodesInCg), even for the last CG
            int inodeBitmapBytes = (inodesInCg + 7) / 8;
            int fragBitmapBytes = (fragsPerGroup + 7) / 8;

            // CG fixed header size: computed from struct cg layout
            int cgFixedHeader = Ufs2Constants.CgHeaderBaseSize;

            // Per FreeBSD mkfs.c initcg(): UFS2 places bitmaps directly after
            // the fixed header (cg_iusedoff = sizeof(struct cg)), while UFS1
            // includes old btot/b rotation summary arrays between the header
            // and bitmaps.
            int oldBtotOff;
            int oldBoff;
            int inodeBitmapOff;
            if (MagicForFormat == Ufs2Constants.Ufs2Magic)
            {
                // UFS2: no old btot/b arrays; inode bitmap starts at sizeof(struct cg)
                oldBtotOff = 0;
                oldBoff = 0;
                inodeBitmapOff = cgFixedHeader;          // 168
            }
            else
            {
                // UFS1: old btot/b arrays follow the fixed header
                oldBtotOff = cgFixedHeader;              // 168
                oldBoff = cgFixedHeader + 1 * 4;         // 172 (fs_old_cpg=1)
                inodeBitmapOff = oldBoff + 1 * 2;        // 174
            }
            int fragBitmapOff = inodeBitmapOff + inodeBitmapBytes;
            int nextFreeOff = fragBitmapOff + fragBitmapBytes;

            // Cluster fields (per FreeBSD mkfs.c and fsck_ffs pass5.c)
            // nclusterblks = cg_ndblk / fs_frag (uses actual CG fragment count)
            int nclusterblks = 0;
            int clustersumoff = 0;
            int clusteroff = 0;
            if (contigSumSize > 0)
            {
                nclusterblks = totalFragsInCg / fragsPerBlock;
                // Per FreeBSD fsck_ffs pass5.c validation:
                // clustersumoff = roundup(freeoff + howmany(fs_fpg, CHAR_BIT), sizeof(uint)) - sizeof(uint)
                // Note: freeoff is cg_freeoff, and the bitmap size uses fs_fpg (not cg_ndblk)
                int rawEnd = fragBitmapOff + (fragsPerGroup + 7) / 8;  // Use fs_fpg, not totalFragsInCg
                clustersumoff = AlignUpInt(rawEnd, 4) - 4;
                clusteroff = clustersumoff + (contigSumSize + 1) * 4;
                // Per fsck_ffs validation: nextfreeoff = clusteroff + howmany(fragstoblks(fs_fpg), CHAR_BIT)
                // Always use nominal fragsPerGroup (fs_fpg) for the bitmap size, not the actual CG frag count.
                int blocksForClusterBitmap = fragsPerGroup / fragsPerBlock;
                nextFreeOff = clusteroff + (blocksForClusterBitmap + 7) / 8;
            }

            int totalCgSize = AlignUpInt(nextFreeOff, FragmentSize);
            totalCgSize = Math.Max(totalCgSize, AlignUpInt(FragmentSize, SectorSize));

            byte[] cgBlock = new byte[AlignUpInt(totalCgSize, SectorSize)];
            using var ms = new MemoryStream(cgBlock);
            using var bw = new BinaryWriter(ms);

            int usedInodes = (cgIndex == 0) ? 3 : 0; // root inode + 2 reserved
            int freeInodes = inodesInCg - usedInodes;
            // Per FreeBSD mkfs.c initcg(): for CG > 0, blocks [0, sblkno) are free
            // (boot/superblock area is only needed in CG 0).
            // For CG 0: tail fragments of the CG summary's last partial block are free
            // (the CG summary only occupies csFrags fragments, not csFragsBlk).
            int csSummaryTailFree = (cgIndex == 0) ? csFragsBlk - csFrags : 0;
            // Compute free blocks and free fragment remainders.
            // CG summary tail fragments are NOT block-aligned, so they must be
            // counted separately as free fragments (never form a complete free block).
            long freeBlocks;
            int freeFrags;
            if (cgIndex == 0)
            {
                freeBlocks = freeDataFrags / fragsPerBlock;
                freeFrags = csSummaryTailFree + (freeDataFrags % fragsPerBlock);
            }
            else
            {
                int totalFreeFrags = freeDataFrags + sblkno;
                freeBlocks = totalFreeFrags / fragsPerBlock;
                freeFrags = totalFreeFrags % fragsPerBlock;
            }
            int dirs = (cgIndex == 0) ? 1 : 0;

            // Fixed CG header fields (struct cg)
            // cg_firstfield (0x00)
            bw.Write(0);
            // cg_magic (0x04)
            bw.Write(Ufs2Constants.CgMagic);
            // cg_old_time (0x08)
            bw.Write((int)(timestamp & 0xFFFFFFFF));
            // cg_cgx (0x0C) — cylinder group index
            bw.Write(cgIndex);
            // cg_old_ncyl (0x10)
            bw.Write((short)0);
            // cg_old_niblk (0x12) — UFS2: 0 (per fsck_ffs pass5.c); UFS1: fs_ipg
            bw.Write((short)(MagicForFormat == Ufs2Constants.Ufs1Magic
                ? inodesInCg / (BlockSize / inodeSize) : 0));
            // cg_ndblk (0x14) — number of data blocks in this CG
            bw.Write(totalFragsInCg);

            // cg_cs (cylinder group summary) at 0x18
            // cs_ndir (0x18)
            bw.Write(dirs);
            // cs_nbfree (0x1C)
            bw.Write((int)freeBlocks);
            // cs_nifree (0x20)
            bw.Write(freeInodes);
            // cs_nffree (0x24)
            bw.Write(freeFrags);

            // cg_rotor (0x28)
            bw.Write(0);
            // cg_frotor (0x2C)
            bw.Write(0);
            // cg_irotor (0x30)
            bw.Write(0);

            // cg_frsum[MAXFRAG=8] (0x34) — fragment summary
            // Counts free fragment runs of each size (1..fragsPerBlock-1) within partial blocks.
            // Per FreeBSD initcg(): CG 0 may have two partial blocks with free fragments:
            //   1. CG summary tail: csSummaryTailFree fragments
            //   2. Trailing data area: freeDataFrags % fragsPerBlock fragments
            // CG > 0 may have one trailing partial block.
            int[] frsum = new int[8];
            if (csSummaryTailFree > 0 && csSummaryTailFree < fragsPerBlock)
                frsum[csSummaryTailFree]++;
            int dataTail = (cgIndex == 0) ? (freeDataFrags % fragsPerBlock) :
                ((freeDataFrags + sblkno) % fragsPerBlock);
            if (dataTail > 0 && dataTail < fragsPerBlock)
                frsum[dataTail]++;
            for (int i = 0; i < 8; i++)
                bw.Write(frsum[i]);

            // cg_old_btotoff (0x54) — per FreeBSD initcg: sizeof(struct cg)
            bw.Write(oldBtotOff);
            // cg_old_boff (0x58) — per FreeBSD initcg: sizeof(struct cg) + fs_old_cpg*sizeof(int32)
            bw.Write(oldBoff);

            // cg_iusedoff (0x5C) — offset to inode used bitmap
            bw.Write(inodeBitmapOff);
            // cg_freeoff (0x60) — offset to free block bitmap
            bw.Write(fragBitmapOff);

            // cg_nextfreeoff (0x64)
            bw.Write(nextFreeOff);

            // cg_clustersumoff (0x68)
            bw.Write(clustersumoff);
            // cg_clusteroff (0x6C)
            bw.Write(clusteroff);
            // cg_nclusterblks (0x70)
            bw.Write(nclusterblks);

            // cg_niblk (0x74) — actual number of inode blocks
            bw.Write(inodesInCg);

            // cg_initediblk (0x78) — number of initialized inodes
            // Per FreeBSD mkfs.c initcg(): UFS2 uses MIN(ipg, 2 * INOPB) for lazy
            // inode initialization; UFS1 sets this to 0 (not used).
            int inodesPerBlk = BlockSize / inodeSize;
            int initediblk = (MagicForFormat == Ufs2Constants.Ufs2Magic)
                ? Math.Min(inodesInCg, 2 * inodesPerBlk)
                : 0;
            bw.Write(initediblk);

            // cg_unrefs (0x7C)
            bw.Write(0);

            // cg_sparecon32[1] (0x80)
            bw.Write(0);

            // cg_ckhash (0x84)
            bw.Write(0);

            // cg_time (0x88) — 64-bit time
            bw.Write(timestamp);

            // cg_sparecon64[3] (0x90)
            bw.Write(0L);
            bw.Write(0L);
            bw.Write(0L);

            // Now write bitmaps at their declared offsets

            // Inode used bitmap (1 = used, 0 = free)
            ms.Position = inodeBitmapOff;
            byte[] inodeBitmap = new byte[inodeBitmapBytes];
            if (cgIndex == 0)
            {
                // Mark inodes 0, 1, 2 as used (reserved + root)
                if (inodeBitmapBytes > 0) inodeBitmap[0] = 0x07; // bits 0, 1, 2
            }
            bw.Write(inodeBitmap);

            // Free fragment bitmap (1 = free, 0 = used)
            ms.Position = fragBitmapOff;
            byte[] fragBitmap = new byte[fragBitmapBytes];
            // Per FreeBSD mkfs.c initcg(): for CG > 0, blocks [0, sblkno) are free
            if (cgIndex > 0)
            {
                for (int f = 0; f < sblkno && f < totalFragsInCg; f++)
                {
                    int byteIdx = f / 8;
                    int bitIdx = f % 8;
                    if (byteIdx < fragBitmapBytes)
                        fragBitmap[byteIdx] |= (byte)(1 << bitIdx);
                }
            }
            // Mark data fragments as free
            // CG 0: skip CG summary area (fragment-aligned) + root directory's data block
            //   CG summary occupies [dataStartFrag, dataStartFrag + csFrags) — USED
            //   Tail of CG summary's last block [dataStartFrag + csFrags, dataStartFrag + csFragsBlk) — FREE
            //   Root dir block [dataStartFrag + csFragsBlk, dataStartFrag + csFragsBlk + fragsPerBlock) — USED
            //   Data area [dataStartFrag + csFragsBlk + fragsPerBlock, end) — FREE
            int firstFreeFrag = dataStartFrag;
            if (cgIndex == 0)
            {
                // Mark CG summary tail fragments as free
                for (int f = dataStartFrag + csFrags; f < dataStartFrag + csFragsBlk; f++)
                {
                    int byteIdx = f / 8;
                    int bitIdx = f % 8;
                    if (byteIdx < fragBitmapBytes)
                        fragBitmap[byteIdx] |= (byte)(1 << bitIdx);
                }
                // Data area starts after block-aligned CG summary + root dir block
                firstFreeFrag = dataStartFrag + csFragsBlk + fragsPerBlock;
            }
            for (int f = firstFreeFrag; f < totalFragsInCg; f++)
            {
                int byteIdx = f / 8;
                int bitIdx = f % 8;
                if (byteIdx < fragBitmapBytes)
                    fragBitmap[byteIdx] |= (byte)(1 << bitIdx);
            }
            // Mark metadata fragments (CG header, inode table) as used (bit = 0, already 0)
            bw.Write(fragBitmap);

            // Cluster free bitmap and cluster summary (per FreeBSD mkfs.c initcg())
            if (contigSumSize > 0)
            {
                byte[] clusterBitmap = new byte[(nclusterblks + 7) / 8];
                // Mark each complete free block in the cluster bitmap
                for (int blk = 0; blk < nclusterblks; blk++)
                {
                    int fragBase = blk * fragsPerBlock;
                    // Check if all fragments in this block are free
                    bool allFree = true;
                    for (int ff = 0; ff < fragsPerBlock; ff++)
                    {
                        int f = fragBase + ff;
                        if (f >= totalFragsInCg)
                        {
                            allFree = false;
                            break;
                        }
                        int byteIdx = f / 8;
                        int bitIdx = f % 8;
                        if (byteIdx >= fragBitmapBytes || (fragBitmap[byteIdx] & (1 << bitIdx)) == 0)
                        {
                            allFree = false;
                            break;
                        }
                    }
                    if (allFree)
                        clusterBitmap[blk / 8] |= (byte)(1 << (blk % 8));
                }

                // Write cluster bitmap
                ms.Position = clusteroff;
                bw.Write(clusterBitmap);

                // Compute and write cluster summary (count runs of contiguous free blocks)
                int[] clusterSum = new int[contigSumSize + 1];
                int run = 0;
                for (int blk = 0; blk < nclusterblks; blk++)
                {
                    bool isFree = (clusterBitmap[blk / 8] & (1 << (blk % 8))) != 0;
                    if (isFree)
                    {
                        run++;
                    }
                    else if (run != 0)
                    {
                        int idx = Math.Min(run, contigSumSize);
                        clusterSum[idx]++;
                        run = 0;
                    }
                }
                if (run != 0)
                {
                    int idx = Math.Min(run, contigSumSize);
                    clusterSum[idx]++;
                }

                // Write cluster summary array at clustersumoff.
                // Index 0 overlaps with the fragment bitmap tail (per FreeBSD layout)
                // and must not be overwritten — start writing from index 1.
                ms.Position = clustersumoff + 4;
                for (int i = 1; i < contigSumSize + 1; i++)
                    bw.Write(clusterSum[i]);
            }

            return cgBlock;
        }

        private byte[] SerializeInodeTable(int cgIndex, int inodesInCg,
            long timestamp, long rootDataBlockFrag, int inodeSize)
        {
            int rawSize = inodesInCg * inodeSize;
            int alignedSize = AlignUpInt(rawSize, SectorSize);
            byte[] data = new byte[alignedSize];

            using var ms = new MemoryStream(data);
            using var writer = new BinaryWriter(ms);

            // Per FreeBSD mkfs.c initcg(): initialized inodes get random di_gen values.
            // cg_initediblk = MIN(ipg, 2 * INOPB) for UFS2, inodesInCg for UFS1.
            int inodesPerBlock = BlockSize / inodeSize;
            int initedInodes = (FilesystemFormat == 1)
                ? inodesInCg
                : Math.Min(inodesInCg, 2 * inodesPerBlock);
            // Seed with cgIndex + 1 to avoid seed 0 (which would produce identical
            // sequences for CG 0) and ensure each CG gets different generation numbers.
            var rng = new Random(cgIndex + 1);

            // di_gen field offsets within the on-disk inode structure:
            // UFS1 (128-byte inode): di_gen at offset 0x6C (108)
            // UFS2 (256-byte inode): di_gen at offset 0x50 (80)
            const int Ufs1InodeGenOffset = 0x6C;
            const int Ufs2InodeGenOffset = 0x50;

            for (int i = 0; i < inodesInCg; i++)
            {
                if (cgIndex == 0 && i == Ufs2Constants.RootInode)
                {
                    if (FilesystemFormat == 1)
                        WriteUfs1RootInode(writer, timestamp, rootDataBlockFrag);
                    else
                        WriteUfs2RootInode(writer, timestamp, rootDataBlockFrag);
                }
                else if (i < initedInodes)
                {
                    // Write an inode with only di_gen set (all other fields zero).
                    byte[] inodeBytes = new byte[inodeSize];
                    int genOffset = (FilesystemFormat == 1) ? Ufs1InodeGenOffset : Ufs2InodeGenOffset;
                    int gen = rng.Next(1, int.MaxValue);
                    inodeBytes[genOffset] = (byte)(gen & 0xFF);
                    inodeBytes[genOffset + 1] = (byte)((gen >> 8) & 0xFF);
                    inodeBytes[genOffset + 2] = (byte)((gen >> 16) & 0xFF);
                    inodeBytes[genOffset + 3] = (byte)((gen >> 24) & 0xFF);
                    writer.Write(inodeBytes);
                }
                else
                {
                    writer.Write(new byte[inodeSize]);
                }
            }

            return data;
        }

        private void WriteUfs2RootInode(BinaryWriter writer, long timestamp, long rootDataBlockFrag)
        {
            var rootInode = new Ufs2Inode
            {
                Mode = (ushort)(Ufs2Constants.IfDir | Ufs2Constants.PermDir),
                NLink = 2,
                Uid = 0,
                Gid = 0,
                BlkSize = (uint)BlockSize,
                Size = BlockSize,
                Blocks = BlockSize / Ufs2Constants.DefaultSectorSize,
                AccessTime = timestamp,
                ModTime = timestamp,
                ChangeTime = timestamp,
                CreateTime = timestamp,
                Generation = 1
            };
            rootInode.DirectBlocks[0] = rootDataBlockFrag;
            rootInode.WriteTo(writer);
        }

        private void WriteUfs1RootInode(BinaryWriter writer, long timestamp, long rootDataBlockFrag)
        {
            int time32 = (int)(timestamp & 0xFFFFFFFF);
            var rootInode = new Ufs1Inode
            {
                Mode = (ushort)(Ufs2Constants.IfDir | Ufs2Constants.PermDir),
                NLink = 2,
                Uid = 0,
                Gid = 0,
                Size = BlockSize,
                Blocks = BlockSize / Ufs2Constants.DefaultSectorSize,
                AccessTime = time32,
                ModTime = time32,
                ChangeTime = time32,
                Generation = 1
            };
            rootInode.DirectBlocks[0] = (int)rootDataBlockFrag;
            rootInode.WriteTo(writer);
        }

        private byte[] SerializeRootDirectoryBlock()
        {
            int alignedSize = AlignUpInt(BlockSize, SectorSize);
            byte[] data = new byte[alignedSize];

            using var ms = new MemoryStream(data);
            using var writer = new BinaryWriter(ms);

            // "." entry
            var dotEntry = new Ufs2DirectoryEntry
            {
                Inode = Ufs2Constants.RootInode,
                FileType = Ufs2Constants.DtDir,
                NameLength = 1,
                Name = ".",
                RecordLength = Ufs2DirectoryEntry.CalculateRecordLength(1, FilesystemFormat == 2)
            };
            dotEntry.WriteTo(writer);

            // ".." entry — must fill to end of current DIRBLKSIZ chunk, not end of block.
            // Per FreeBSD ufs_dirbadentry(): d_reclen must not exceed
            // DIRBLKSIZ - (entryoffsetinblock & (DIRBLKSIZ - 1)).
            int dirBlkSiz = Ufs2Constants.DirBlockSize;
            int posInChunk = (int)(ms.Position % dirBlkSiz);
            int remainingInChunk = dirBlkSiz - posInChunk;

            if (!NoSnapDir)
            {
                // ".." entry with room for .snap
                var dotDotEntry = new Ufs2DirectoryEntry
                {
                    Inode = Ufs2Constants.RootInode,
                    FileType = Ufs2Constants.DtDir,
                    NameLength = 2,
                    Name = "..",
                    RecordLength = Ufs2DirectoryEntry.CalculateRecordLength(2, FilesystemFormat == 2)
                };
                dotDotEntry.WriteTo(writer);

                posInChunk = (int)(ms.Position % dirBlkSiz);
                remainingInChunk = dirBlkSiz - posInChunk;

                // .snap directory entry (FreeBSD creates this by default)
                // We write an empty placeholder - actual .snap inode would need allocation
                // For newfs compatibility, we just reserve space like FreeBSD does
                var snapEntry = new Ufs2DirectoryEntry
                {
                    Inode = 0, // No inode allocated - placeholder
                    FileType = Ufs2Constants.DtUnknown,
                    NameLength = 5,
                    Name = ".snap",
                    RecordLength = (ushort)remainingInChunk
                };
                snapEntry.WriteTo(writer);
            }
            else
            {
                // No .snap directory (-n flag)
                var dotDotEntry = new Ufs2DirectoryEntry
                {
                    Inode = Ufs2Constants.RootInode,
                    FileType = Ufs2Constants.DtDir,
                    NameLength = 2,
                    Name = "..",
                    RecordLength = (ushort)remainingInChunk
                };
                dotDotEntry.WriteTo(writer);
            }

            // Fill remaining DIRBLKSIZ chunks with valid padding entries to prevent
            // DIRECTORY CORRUPTED errors in fsck_ufs. FreeBSD's fsck validates that
            // every DIRBLKSIZ chunk within di_size contains valid directory entries
            // (d_reclen > 0). Zero-filled chunks have d_reclen == 0 which is invalid.
            int dirBlkSizPad = Ufs2Constants.DirBlockSize;
            long currentPos = ms.Position;
            while (currentPos + dirBlkSizPad <= alignedSize)
            {
                var paddingEntry = new Ufs2DirectoryEntry
                {
                    Inode = 0,
                    FileType = 0,
                    NameLength = 0,
                    Name = "",
                    RecordLength = (ushort)dirBlkSizPad
                };
                paddingEntry.WriteTo(writer);
                currentPos = ms.Position;
            }

            return data;
        }

        // --- Directory population (-D option) ---

        /// <summary>
        /// Recursively calculate the total size of all files in a directory (including hidden files).
        /// </summary>
        public static long CalculateDirectorySize(string directoryPath)
        {
            var (rawSize, _, _) = CalculateDirectorySizes(directoryPath, 0);
            return rawSize;
        }

        /// <summary>
        /// Calculate both the raw byte size and the on-disk block-aligned size of a
        /// directory's contents in a single traversal.
        /// The block-aligned size rounds each file up to the block size, counts one block
        /// per directory for its entries, and adds an extra block for files needing
        /// indirect pointers (more than NDirect blocks).
        /// </summary>
        public static (long rawSize, long blockAlignedSize, long totalEntries) CalculateDirectorySizes(
            string directoryPath, int blockSize)
        {
            long rawSize = 0;
            long blockAlignedSize = 0;
            long totalEntries = 0; // total files + directories found (the root directory itself is not counted)
            int ptrSize = 8; // UFS2 uses 64-bit block pointers
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                foreach (var entry in dirInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        totalEntries++;
                        if (entry is FileInfo file)
                        {
                            long len = file.Length;
                            rawSize += len;
                            if (blockSize > 0 && len > 0)
                            {
                                long fileBlocks = (len + blockSize - 1) / blockSize;
                                blockAlignedSize += fileBlocks * blockSize;
                                // Account for indirect/double-indirect/triple-indirect metadata blocks
                                int pointersPerBlock = blockSize / ptrSize;
                                if (fileBlocks > Ufs2Constants.NDirect)
                                {
                                    // Single-indirect block
                                    blockAlignedSize += blockSize;
                                    long remaining = fileBlocks - Ufs2Constants.NDirect;
                                    if (remaining > pointersPerBlock)
                                    {
                                        // Double-indirect block + its sub-indirect blocks
                                        remaining -= pointersPerBlock;
                                        long dindEntries = (remaining + pointersPerBlock - 1) / pointersPerBlock;
                                        blockAlignedSize += (1 + dindEntries) * blockSize;
                                        if (remaining > (long)pointersPerBlock * pointersPerBlock)
                                        {
                                            // Triple-indirect block + its sub-blocks
                                            remaining -= (long)pointersPerBlock * pointersPerBlock;
                                            long tindL2 = (remaining + pointersPerBlock - 1) / pointersPerBlock;
                                            long tindL1 = (tindL2 + pointersPerBlock - 1) / pointersPerBlock;
                                            blockAlignedSize += (1 + tindL1 + tindL2) * blockSize;
                                        }
                                    }
                                }
                            }
                        }
                        else if (entry is DirectoryInfo subDir && blockSize > 0)
                        {
                            // Estimate directory blocks needed based on entry count
                            int entryCount = 2; // "." and ".."
                            try
                            {
                                entryCount += subDir.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly).Count();
                            }
                            catch { /* fallback to minimum */ }
                            int estimatedBytes = entryCount * EstimatedBytesPerDirEntry;
                            int dirBlocksNeeded = Math.Max(1, (estimatedBytes + blockSize - 1) / blockSize);
                            blockAlignedSize += (long)dirBlocksNeeded * blockSize;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Warning: Skipping '{entry.FullName}': {ex.Message}");
                    }
                }

                // Root directory: estimate blocks needed based on top-level entry count
                if (blockSize > 0)
                {
                    int rootEntryCount = 2; // "." and ".."
                    try
                    {
                        rootEntryCount += dirInfo.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly).Count();
                    }
                    catch { /* fallback to minimum */ }
                    int rootEstimatedBytes = rootEntryCount * EstimatedBytesPerDirEntry;
                    int rootBlocksNeeded = Math.Max(1, (rootEstimatedBytes + blockSize - 1) / blockSize);
                    blockAlignedSize += (long)rootBlocksNeeded * blockSize;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Warning: Could not enumerate directory '{directoryPath}': {ex.Message}");
            }
            return (rawSize, blockAlignedSize, totalEntries);
        }

        /// <summary>
        /// Calculate directory sizes using FreeBSD 14.0.0 ffs_size_dir ADDSIZE/ADDDIRENT logic.
        /// Uses fragment-level granularity for small files and exact indirect block accounting.
        /// </summary>
        public static (long rawSize, long diskSize, long totalEntries) CalculateDirectorySizes(
            string directoryPath, int blockSize, int fragmentSize, int filesystemFormat)
        {
            long rawSize = 0;
            long diskSize = 0;
            long totalEntries = 0;
            const int DIRBLKSIZ = 512;

            // DIRSIZ: 8 bytes header (d_ino(4)+d_reclen(2)+d_type(1)+d_namlen(1)) + namelen + 1 (null), rounded up to 4
            static int Dirsiz(int namelen)
            {
                return (8 + namelen + 1 + 3) & ~3;
            }

            // ADDSIZE: compute on-disk allocation for a data extent.
            // PopulateFromDirectory allocates full blocks per file, so round up
            // to block size (not fragment size) to match actual allocation.
            void AddSize(long x)
            {
                long ndirectThreshold = (long)Ufs2Constants.NDirect * blockSize;
                if (x < ndirectThreshold)
                {
                    // roundup(x, bsize) — full-block allocation
                    diskSize += ((x + blockSize - 1) / blockSize) * blockSize;
                }
                else
                {
                    // Indirect block overhead: bsize * (howmany(x, NDirect * bsize) - 1)
                    long howmany = (x + ndirectThreshold - 1) / ndirectThreshold;
                    diskSize += (long)blockSize * (howmany - 1);
                    // Data blocks: roundup(x, bsize)
                    diskSize += ((x + blockSize - 1) / blockSize) * blockSize;
                }
            }

            // FreeBSD ADDDIRENT macro: accumulates curdirsize for a directory
            static int AddDirEnt(int curdirsize, int namelen)
            {
                int entSize = Dirsiz(namelen);
                if (entSize + curdirsize > ((curdirsize + DIRBLKSIZ - 1) / DIRBLKSIZ) * DIRBLKSIZ)
                    curdirsize = ((curdirsize + DIRBLKSIZ - 1) / DIRBLKSIZ) * DIRBLKSIZ;
                curdirsize += entSize;
                return curdirsize;
            }

            // Process a single directory's entries and return its cumulative directory size
            int ProcessDirectory(DirectoryInfo dir)
            {
                int curdirsize = 0;
                // "." and ".."
                curdirsize = AddDirEnt(curdirsize, 1); // "."
                curdirsize = AddDirEnt(curdirsize, 2); // ".."

                try
                {
                    foreach (var entry in dir.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
                    {
                        curdirsize = AddDirEnt(curdirsize, entry.Name.Length);
                    }
                }
                catch { /* skip inaccessible entries */ }

                return curdirsize;
            }

            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);

                // Process root directory
                int rootDirSize = ProcessDirectory(dirInfo);
                AddSize(rootDirSize);

                foreach (var entry in dirInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        totalEntries++;
                        if (entry is FileInfo file)
                        {
                            long len = file.Length;
                            rawSize += len;
                            if (len > 0)
                                AddSize(len);
                        }
                        else if (entry is DirectoryInfo subDir)
                        {
                            int subDirSize = ProcessDirectory(subDir);
                            AddSize(subDirSize);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Warning: Skipping '{entry.FullName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Warning: Could not enumerate directory '{directoryPath}': {ex.Message}");
            }
            return (rawSize, diskSize, totalEntries);
        }

        /// <summary>
        /// Calculate the image size from on-disk block-aligned directory size.
        /// Adds 10% overhead for filesystem metadata (cylinder group headers, inode tables,
        /// superblocks) plus a 10 MB safety margin, rounded up to fragment alignment.
        /// </summary>
        public long CalculateImageSize(long blockAlignedDirectorySize)
        {
            long imageSize = (long)(blockAlignedDirectorySize * 1.10) + (10 * 1024 * 1024);
            // Ensure minimum size (16 blocks)
            long minSize = BlockSize * 16;
            if (imageSize < minSize)
                imageSize = minSize;
            // Align up to fragment boundary
            imageSize = AlignUp(imageSize, FragmentSize);
            return imageSize;
        }

        /// <summary>
        /// Create a UFS2 image from a directory in a single integrated operation:
        /// 1. Calculate the required space from the directory contents (block-aligned + overhead)
        /// 2. Create an empty UFS2 filesystem of that size
        /// 3. Recursively add all files and directories (like tar -C input -cf - . | tar -C mount -xpf -)
        /// When <paramref name="totalSizeBytes"/> is positive it is used as the image size;
        /// otherwise the size is auto-calculated from the directory contents.
        /// </summary>
        public long CreateImageFromDirectory(string imagePath, string directoryPath, long totalSizeBytes = 0)
        {
            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException($"Input directory not found: {directoryPath}");

            // Soft updates: if not explicitly enabled, ensure journal is off
            bool origSoftUpdates = SoftUpdates;
            bool origSoftUpdatesJournal = SoftUpdatesJournal;
            if (!SoftUpdates)
                SoftUpdatesJournal = false;

            try
            {
                // Step 1: Calculate required space if not explicitly provided
                if (totalSizeBytes <= 0)
                {
                    var (dirSize, diskSize, totalEntries) = CalculateDirectorySizes(directoryPath, BlockSize);
                    totalSizeBytes = CalculateImageSize(diskSize);

                    // Ensure the image has enough cylinder groups for the required inodes.
                    // Each entry (file or directory) needs one inode, plus 3 reserved inodes
                    // (inode 0, inode 1, and the root directory inode 2).
                    long totalInodesNeeded = totalEntries + 3;
                    int inodesPerGroup = ComputeInodesPerGroup(totalSizeBytes);
                    int fragsPerBlock = BlockSize / FragmentSize;
                    int fragsPerGroup = inodesPerGroup * fragsPerBlock;
                    long totalFrags = totalSizeBytes / FragmentSize;
                    int numCylGroups = Math.Max(1, (int)((totalFrags + fragsPerGroup - 1) / fragsPerGroup));
                    long availableInodes = (long)numCylGroups * inodesPerGroup;

                    if (totalInodesNeeded > availableInodes)
                    {
                        // Need more CGs to hold all inodes
                        int requiredCgs = (int)((totalInodesNeeded + inodesPerGroup - 1) / inodesPerGroup);
                        long requiredFrags = (long)requiredCgs * fragsPerGroup;
                        long requiredBytes = requiredFrags * FragmentSize;
                        if (requiredBytes > totalSizeBytes)
                            totalSizeBytes = AlignUp(requiredBytes, FragmentSize);
                    }

                    (Output ?? Console.Out).WriteLine($"  Input directory: {directoryPath}");
                    (Output ?? Console.Out).WriteLine($"  Directory size:  {dirSize:N0} bytes ({dirSize / (1024.0 * 1024):F2} MB)");
                    (Output ?? Console.Out).WriteLine($"  Auto-sized to:   {totalSizeBytes:N0} bytes ({totalSizeBytes / (1024 * 1024)} MB)");
                    (Output ?? Console.Out).WriteLine();
                }

                // Step 2: Create an empty UFS filesystem
                CreateImage(imagePath, totalSizeBytes);

                // Step 3: Recursively add files and directories
                if (!DryRun)
                {
                    PopulateFromDirectory(imagePath, directoryPath);
                }

                return totalSizeBytes;
            }
            finally
            {
                // Restore original settings in case the creator instance is reused
                SoftUpdates = origSoftUpdates;
                SoftUpdatesJournal = origSoftUpdatesJournal;
            }
        }

        /// <summary>
        /// Create a filesystem image from a directory tree, replicating FreeBSD makefs(8)
        /// ffs_validate/ffs_makefs behavior exactly.
        ///
        /// This is architecturally separate from newfs -D (CreateImageFromDirectory).
        /// makefs calculates size from the directory tree using the FreeBSD algorithm:
        ///   1. Walk tree → compute exact data size + inode count (ffs_size_dir)
        ///   2. Add freeblocks/freefiles slop
        ///   3. Compute per-CG overhead (backup superblocks, CG headers, inode tables,
        ///      CG summary area, minfree%) and derive total image size iteratively
        ///   4. Enforce minsize/maxsize constraints
        ///   5. Round up to block boundary (and optional roundup)
        ///   6. Auto-calculate density and maxbpcg if not set
        /// </summary>
        public long MakeFsImage(string imagePath, string directoryPath,
            long imageSize = 0, long freeblocks = 0, int freeblockpc = 0,
            long freefiles = 0, int freefilepc = 0,
            long minimumSize = 0, long maximumSize = 0,
            long roundup = 0, long makeFsTimestamp = -1)
        {
            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException($"Input directory not found: {directoryPath}");

            // Save original state for restoration after MakeFsImage completes
            int origBytesPerInode = BytesPerInode;
            int origBlocksPerCylGroup = BlocksPerCylGroup;

            try
            {

                // -s sets both minsize and maxsize (exactly like FreeBSD)
                if (imageSize > 0)
                {
                    // Align to block boundary so min==max is achievable
                    long aligned = AlignUp(imageSize, BlockSize);
                    minimumSize = aligned;
                    maximumSize = aligned;
                }

                // Validate minsize/maxsize after rounding up to bsize
                if (maximumSize > 0 && AlignUp(minimumSize, BlockSize) > maximumSize)
                    throw new ArgumentException(
                        $"Minimum size {minimumSize:N0} rounded up to block size {BlockSize} " +
                        $"exceeds maximum size {maximumSize:N0}.");

                // ── Step 1: ffs_size_dir — walk tree, compute exact data size + inodes ──
                var (dirSize, diskSize, totalEntries) = CalculateDirectorySizes(directoryPath, BlockSize, FragmentSize, FilesystemFormat);
                // Each entry needs one inode, plus 3 reserved inodes
                // (inode 0, inode 1, and the root directory inode 2).
                long totalInodes = totalEntries + 3;
                long totalSize = diskSize;

                // ── Step 2: Add requested free blocks/files slop ──
                totalSize += freeblocks;
                totalInodes += freefiles;
                if (freefilepc > 0)
                    totalInodes = totalInodes * (100 + freefilepc) / 100;
                if (freeblockpc > 0)
                    totalSize = totalSize * (100 + freeblockpc) / 100;

                // ── Step 3: Add structural overhead (per-CG metadata + minfree) ──
                // Compute CG layout offsets using the same formulas as WriteFilesystem.
                // Each CG has dblkno fragments of metadata before data starts.
                int inodeSize = InodeSizeForFormat;
                int fragsPerBlock = BlockSize / FragmentSize;
                int inodesPerBlock = BlockSize / inodeSize;

                int sblkno = AlignUpInt(
                    (Ufs2Constants.SuperblockOffset64K + Ufs2Constants.SuperblockSize + FragmentSize - 1) / FragmentSize,
                    fragsPerBlock);
                int cblkno = sblkno + AlignUpInt(
                    (Ufs2Constants.SuperblockSize + FragmentSize - 1) / FragmentSize,
                    fragsPerBlock);
                int iblkno = cblkno + fragsPerBlock;

                // Estimate ipg (inodes per group) to predict the CG layout that
                // WriteFilesystem will produce after step 7 sets BytesPerInode.
                int ipg;
                if (BytesPerInode > 0)
                {
                    ipg = InodesPerGroup;
                }
                else if (totalInodes > 0)
                {
                    // Auto-density: estimate what ComputeInodesPerGroup will compute.
                    int defaultFpg = InodesPerGroup * fragsPerBlock;
                    // Use 20% overhead estimate to approximate the final image size
                    // (minfree 8% + CG metadata ~2-5% + safety margin)
                    long estTotalSize = Math.Max(totalSize * 120 / 100, (long)BlockSize * 16);
                    long estTotalFrags = estTotalSize / FragmentSize;
                    int estNumCg = Math.Max(1, (int)((estTotalFrags + defaultFpg - 1) / defaultFpg));
                    ipg = (int)(totalInodes / estNumCg);
                    ipg = Math.Max(((ipg + inodesPerBlock - 1) / inodesPerBlock) * inodesPerBlock, inodesPerBlock);
                }
                else
                {
                    ipg = InodesPerGroup;
                }

                int inodeblks = ((ipg + inodesPerBlock - 1) / inodesPerBlock) * fragsPerBlock;
                int dblkno = iblkno + inodeblks;
                bool autoMaxBpcg = false;
                if (BlocksPerCylGroup <= 0)
                {
                    // Auto maxbpcg: choose the largest blocks/group that still provides
                    // enough cylinder groups to fit all required inodes.
                    int requiredCgForInodes = 1;
                    if (totalInodes > 0 && ipg > 0)
                        requiredCgForInodes = Math.Max(1, (int)((totalInodes + ipg - 1) / ipg));

                    long minSizeForCalc = Math.Max(totalSize, (long)BlockSize * 16);
                    long totalBlocksEstimate = Math.Max(1, (minSizeForCalc + BlockSize - 1) / BlockSize);
                    long autoBpcg = (totalBlocksEstimate + requiredCgForInodes - 1) / requiredCgForInodes;

                    long maxBpcgByInt = Math.Max(1, int.MaxValue / fragsPerBlock);
                    if (autoBpcg > maxBpcgByInt)
                        autoBpcg = maxBpcgByInt;

                    BlocksPerCylGroup = (int)Math.Max(1, autoBpcg);
                    autoMaxBpcg = true;
                }

                int fragsPerGroup = BlocksPerCylGroup * fragsPerBlock;

                // dataFragsPerCg: usable data fragments per CG (metadata consumes dblkno frags)
                int dataFragsPerCg = fragsPerGroup - dblkno;
                if (dataFragsPerCg < fragsPerBlock)
                    dataFragsPerCg = fragsPerBlock;

                // Apply minfree: the reserved percentage reduces usable data capacity
                long dataSize = totalSize;
                if (MinFreePercent > 0)
                    dataSize = dataSize * (100 + MinFreePercent) / 100;

                // Compute total image size by scaling data with per-CG overhead ratio.
                // Each CG of fragsPerGroup fragments has dataFragsPerCg usable data fragments,
                // so the overhead ratio is fragsPerGroup / dataFragsPerCg.
                long dataFragsNeeded = (dataSize + FragmentSize - 1) / FragmentSize;

                // Estimate number of CGs for CG summary area size calculation
                int estNumCGs = Math.Max(1, (int)((dataFragsNeeded + dataFragsPerCg - 1) / dataFragsPerCg));
                int csSize = AlignUpInt(estNumCGs * Ufs2Constants.CsumStructSize, FragmentSize);

                // Fixed overhead: primary superblock area + CG summary + root directory block
                int sblockOffset = (FilesystemFormat == 1)
                    ? Ufs2Constants.SuperblockSize
                    : Ufs2Constants.SuperblockOffset64K;
                long fixedOverhead = AlignUp(sblockOffset + Ufs2Constants.SuperblockSize, BlockSize)
                                   + csSize
                                   + BlockSize;

                // Scale data by CG overhead ratio: ceil(dataFrags * fpg / dataFragsPerCg) gives
                // the total fragments needed including per-CG metadata, converted to bytes.
                totalSize = ((dataFragsNeeded * fragsPerGroup + dataFragsPerCg - 1) / dataFragsPerCg) * FragmentSize
                          + fixedOverhead;

                // ── Step 4: Enforce minimum size ──
                // The filesystem requires at least 16 blocks to fit superblock, CG, and inodes
                long fsMinSize = (long)BlockSize * 16;
                if (totalSize < fsMinSize)
                    totalSize = fsMinSize;
                if (minimumSize > 0 && totalSize < minimumSize)
                    totalSize = minimumSize;

                // ── Step 5: Round up to block boundary ──
                totalSize = AlignUp(totalSize, BlockSize);

                // ── Step 6: Apply roundup value ──
                if (roundup > 0)
                    totalSize = AlignUp(totalSize, roundup);

                // ── Step 7: Auto-calculate density if not explicitly set ──
                // FreeBSD: density = size / inodes + 1 (when -o density not specified)
                bool autoDensity = false;
                if (BytesPerInode <= 0 && totalInodes > 0)
                {
                    BytesPerInode = (int)Math.Min(totalSize / totalInodes + 1, int.MaxValue);
                    autoDensity = true;
                }

                // ── Step 8: Check maximum size ──
                if (maximumSize > 0 && totalSize > maximumSize)
                    throw new ArgumentException(
                        $"Image size ({totalSize:N0} bytes) exceeds maximum size ({maximumSize:N0} bytes).");

                (Output ?? Console.Out).WriteLine($"  Calculated size of '{imagePath}': {totalSize:N0} bytes, {totalInodes} inodes");

                // ── Step 9: Create image and populate ──
                (Output ?? Console.Out).WriteLine($"  Input directory: {directoryPath}");
                (Output ?? Console.Out).WriteLine($"  Directory size:  {dirSize:N0} bytes ({dirSize / (1024.0 * 1024):F2} MB)");
                (Output ?? Console.Out).WriteLine($"  Image size:      {totalSize:N0} bytes ({totalSize / (1024 * 1024)} MB)");
                if (autoDensity)
                    (Output ?? Console.Out).WriteLine($"  Auto density:    {BytesPerInode:N0} bytes/inode");
                if (autoMaxBpcg)
                    (Output ?? Console.Out).WriteLine($"  Auto maxbpcg:    {BlocksPerCylGroup:N0} blocks/CG");
                (Output ?? Console.Out).WriteLine();

                CreateImage(imagePath, totalSize);

                if (!DryRun)
                {
                    (Output ?? Console.Out).WriteLine($"  Populating '{imagePath}'");
                    PopulateFromDirectory(imagePath, directoryPath);
                    (Output ?? Console.Out).WriteLine($"  Image '{imagePath}' complete");
                }

                return totalSize;
            }
            finally
            {
                // Restore original state in case the creator instance is reused
                BytesPerInode = origBytesPerInode;
                BlocksPerCylGroup = origBlocksPerCylGroup;
            }
        }

        /// <summary>
        /// Populate a freshly created UFS image with the contents of the specified directory.
        /// Walks the input directory tree, allocates inodes/blocks, writes file data,
        /// builds directory blocks, and patches CG bitmaps and superblock counts.
        /// Supports files up to 12 direct blocks + single-indirect block pointers.
        /// </summary>
        public void PopulateFromDirectory(string imagePath, string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException($"Input directory not found: {directoryPath}");

            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite);
            using var reader = new BinaryReader(fs);
            using var writer = new BinaryWriter(fs);

            // Read existing superblock
            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            int fragsPerBlock = sb.BSize / sb.FSize;
            int inodeSize = InodeSizeForFormat;

            // Locate key offsets — use IblkNo from the superblock
            long inodeTableOffset = (long)sb.IblkNo * sb.FSize;

            int dataStartFrag = sb.DblkNo;

            // Track allocation state
            // Next available inode (0,1 reserved, 2 = root)
            uint nextInode = 3;
            // Track per-CG data block allocation (fragment index within CG, starting from data region)
            int currentCg = 0;
            int fragsPerGroup = sb.CylGroupSize;
            // CG 0: CG summary area + root directory are already allocated
            // CG summary occupies csFrags fragments (fragment-aligned), but the root dir
            // starts at the next block boundary (csFragsBlk = block-aligned CG summary).
            int csFragsBlkInCg0 = ((sb.CsSize + sb.BSize - 1) / sb.BSize) * fragsPerBlock;
            int nextDataFragInCg = dataStartFrag + csFragsBlkInCg0 + fragsPerBlock;
            // Track per-CG high-water mark of data allocation (fragment offset within CG)
            // so PatchCgAndSuperblock knows exactly how many frags were used in each CG.
            var perCgHighWater = new int[sb.NumCylGroups];
            perCgHighWater[0] = nextDataFragInCg;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Collect all entries to add to root directory
            var rootEntries = new List<(string name, uint inode, byte fileType)>();
            int dirCount = 0;
            int filesWritten = 0;
            // Track per-CG directory counts (root is in CG 0)
            var dirsPerCg = new int[sb.NumCylGroups];
            dirsPerCg[Ufs2Constants.RootInode / sb.InodesPerGroup] = 1; // root directory

            long totalFrags = sb.TotalBlocks;

            // Track tail-free fragments: absolute fragment numbers of unused tail fragments
            // in the last allocated block of each file. These should be marked free in the bitmap.
            var tailFreeFrags = new HashSet<long>();

            var (totalFiles, totalBytes) = CountFilesAndBytesRecursive(directoryPath);
            long copiedBytes = 0;
            int displayedCompletedFiles = 0;
            int lastProgressPercent = -1;
            void ReportCopyProgress(int completedFiles, long bytesDelta, bool force = false)
            {
                if (bytesDelta > 0)
                {
                    copiedBytes += bytesDelta;
                    if (copiedBytes > totalBytes)
                        copiedBytes = totalBytes;
                }

                if (completedFiles > displayedCompletedFiles)
                    displayedCompletedFiles = completedFiles;

                if (totalFiles <= 0 && totalBytes <= 0)
                    return;

                int percent;
                if (totalBytes > 0)
                {
                    percent = (int)(copiedBytes * 100 / totalBytes);
                }
                else
                {
                    percent = (int)((long)displayedCompletedFiles * 100 / totalFiles);
                }

                if (percent >= 100 && displayedCompletedFiles < totalFiles)
                    percent = 99;

                if (percent > 100)
                    percent = 100;

                if (!force && percent == lastProgressPercent)
                    return;

                (Output ?? Console.Out).Write(
                    $"\r  Adding files to image... {percent,3}% " +
                    $"({displayedCompletedFiles:N0}/{totalFiles:N0} files, {FormatBytes(copiedBytes)}/{FormatBytes(totalBytes)})");
                lastProgressPercent = percent;
            }

            if (totalFiles > 0 || totalBytes > 0)
                ReportCopyProgress(0, 0, force: true);

            // Recursively process directory contents
            ProcessDirectoryContents(fs, writer, reader, directoryPath,
                Ufs2Constants.RootInode, sb, inodeTableOffset, inodeSize,
                fragsPerBlock, fragsPerGroup, dataStartFrag,
                ref nextInode, ref nextDataFragInCg, ref currentCg,
                ref dirCount, ref filesWritten, rootEntries, timestamp: now,
                dirsPerCg: dirsPerCg, perCgHighWater: perCgHighWater,
                totalFrags: totalFrags, tailFreeFrags: tailFreeFrags,
                onCopyProgress: (completedFiles, bytesDelta) => ReportCopyProgress(completedFiles, bytesDelta));

            // Rewrite root directory block(s) to include the new entries
            long rootInodeTablePos = inodeTableOffset + (long)Ufs2Constants.RootInode * inodeSize;
            long rootDataFrag = ReadRootDataBlockFrag(fs, reader, rootInodeTablePos, inodeSize);

            // Calculate how many blocks the root directory needs
            int rootBlocksNeeded = CalculateDirBlocksNeeded(rootEntries, sb.BSize, FilesystemFormat == 2);

            // Collect all root directory block positions (first block already allocated)
            var rootBlocks = new List<long> { rootDataFrag };
            for (int b = 1; b < rootBlocksNeeded; b++)
            {
                var (frag, cg) = AllocateDataBlock(
                    fragsPerBlock, fragsPerGroup, dataStartFrag,
                    ref nextDataFragInCg, ref currentCg, sb.NumCylGroups,
                    perCgHighWater, totalFrags);
                rootBlocks.Add((long)cg * fragsPerGroup + frag);
            }

            // Write root directory block(s)
            WriteDirBlocks(fs, writer, rootBlocks, sb.BSize, sb.FSize,
                Ufs2Constants.RootInode, Ufs2Constants.RootInode, rootEntries);

            // Update root inode with correct size, block count, and block pointers
            long rootTotalSize = (long)rootBlocksNeeded * sb.BSize;

            // Build allocation info; handle indirect blocks for large root directories
            var rootAlloc = BuildDirectoryAllocation(fs, writer, rootBlocks, sb,
                fragsPerBlock, fragsPerGroup, dataStartFrag,
                ref nextDataFragInCg, ref currentCg,
                perCgHighWater, totalFrags);

            // Count immediate subdirectories of root for correct nlink
            int rootSubDirCount = 0;
            foreach (var (name, inode, fileType) in rootEntries)
                if (fileType == Ufs2Constants.DtDir) rootSubDirCount++;

            WriteInodeAtPosition(fs, writer, Ufs2Constants.RootInode, inodeTableOffset,
                inodeSize, sb.InodesPerGroup, fragsPerGroup, sb.FSize,
                sb.BSize, dataStartFrag, true, rootTotalSize, rootAlloc, now,
                nlink: (short)(2 + rootSubDirCount));

            // Patch CG bitmaps and superblock counts
            PatchCgAndSuperblock(fs, reader, writer, sb, nextInode, dirCount,
                inodeSize, fragsPerBlock, fragsPerGroup, dataStartFrag,
                nextDataFragInCg, currentCg, dirsPerCg, perCgHighWater,
                tailFreeFrags);

            fs.Flush();
            if (totalFiles > 0 || totalBytes > 0)
            {
                long remainingBytes = totalBytes - copiedBytes;
                if (remainingBytes < 0)
                    remainingBytes = 0;
                ReportCopyProgress(filesWritten, remainingBytes, force: true);
                (Output ?? Console.Out).WriteLine();
            }

            (Output ?? Console.Out).WriteLine($"  Populated image with {filesWritten} file(s) and {dirCount} directory(ies) from: {directoryPath}");
        }

        /// <summary>
        /// Count regular files and their total byte size recursively for copy progress reporting.
        /// </summary>
        private static (int totalFiles, long totalBytes) CountFilesAndBytesRecursive(string dirPath)
        {
            int totalFiles = 0;
            long totalBytes = 0;

            foreach (var entry in new DirectoryInfo(dirPath).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
            {
                if (entry is DirectoryInfo subDir)
                {
                    var (subFiles, subBytes) = CountFilesAndBytesRecursive(subDir.FullName);
                    totalFiles += subFiles;
                    totalBytes += subBytes;
                }
                else if (entry is FileInfo file)
                {
                    totalFiles++;
                    totalBytes += file.Length;
                }
            }

            return (totalFiles, totalBytes);
        }

        /// <summary>
        /// Format a byte count using binary units for concise progress output.
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            double value = bytes;
            if (value < 0)
                value = 0;

            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            if (unitIndex == 0)
                return $"{value:0} {units[unitIndex]}";

            return $"{value:0.00} {units[unitIndex]}";
        }

        /// <summary>
        /// Recursively process a directory's contents, allocating inodes and blocks
        /// for files and subdirectories.
        /// </summary>
        private void ProcessDirectoryContents(
            FileStream fs, BinaryWriter writer, BinaryReader reader,
            string dirPath, uint parentInode,
            Ufs2Superblock sb, long inodeTableOffset, int inodeSize,
            int fragsPerBlock, int fragsPerGroup, int dataStartFrag,
            ref uint nextInode, ref int nextDataFragInCg, ref int currentCg,
            ref int dirCount, ref int filesWritten,
            List<(string name, uint inode, byte fileType)> parentEntries, long timestamp,
            int[] dirsPerCg, int[]? perCgHighWater = null, long totalFrags = 0,
            HashSet<long>? tailFreeFrags = null, uint parentDirDepth = 0,
            Action<int, long>? onCopyProgress = null)
        {
            uint maxInodes = (uint)((long)sb.NumCylGroups * sb.InodesPerGroup);

            foreach (var entry in new DirectoryInfo(dirPath).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
            {
                if (entry is DirectoryInfo subDir)
                {
                    if (nextInode >= maxInodes)
                        throw new InvalidOperationException(
                            $"Image is too small: no more inodes available (used {nextInode}, max {maxInodes}). " +
                            "Try increasing image size or use fewer files.");

                    // Allocate inode for subdirectory
                    uint dirInode = nextInode++;

                    // Recursively collect subdirectory entries
                    var subEntries = new List<(string name, uint inode, byte fileType)>();
                    uint childDepth = parentDirDepth + 1;
                    ProcessDirectoryContents(fs, writer, reader, subDir.FullName,
                        dirInode, sb, inodeTableOffset, inodeSize,
                        fragsPerBlock, fragsPerGroup, dataStartFrag,
                        ref nextInode, ref nextDataFragInCg, ref currentCg,
                        ref dirCount, ref filesWritten, subEntries, timestamp,
                        dirsPerCg, perCgHighWater, totalFrags, tailFreeFrags,
                        parentDirDepth: childDepth, onCopyProgress: onCopyProgress);

                    // Calculate how many blocks the directory needs
                    int dirBlocksNeeded = CalculateDirBlocksNeeded(subEntries, sb.BSize, FilesystemFormat == 2);

                    // Allocate data blocks for directory entries
                    var dirBlocks = new List<long>();
                    for (int b = 0; b < dirBlocksNeeded; b++)
                    {
                        var (frag, cg) = AllocateDataBlock(
                            fragsPerBlock, fragsPerGroup, dataStartFrag,
                            ref nextDataFragInCg, ref currentCg, sb.NumCylGroups,
                            perCgHighWater, totalFrags);
                        dirBlocks.Add((long)cg * fragsPerGroup + frag);
                    }

                    // Write subdirectory data block(s)
                    WriteDirBlocks(fs, writer, dirBlocks, sb.BSize, sb.FSize,
                        dirInode, parentInode, subEntries);

                    // Directory size is total blocks × block size
                    long dirSize = (long)dirBlocksNeeded * sb.BSize;

                    // Write directory inode with correct nlink (2 + immediate subdirectory count)
                    int subDirCount = 0;
                    foreach (var (name, inode, fileType) in subEntries)
                        if (fileType == Ufs2Constants.DtDir) subDirCount++;

                    // Build allocation info; handle indirect blocks for large directories
                    var dirAlloc = BuildDirectoryAllocation(fs, writer, dirBlocks, sb,
                        fragsPerBlock, fragsPerGroup, dataStartFrag,
                        ref nextDataFragInCg, ref currentCg,
                        perCgHighWater, totalFrags);

                    WriteInodeAtPosition(fs, writer, dirInode, inodeTableOffset,
                        inodeSize, sb.InodesPerGroup, fragsPerGroup, sb.FSize,
                        sb.BSize, dataStartFrag, true, dirSize, dirAlloc, timestamp,
                        nlink: (short)(2 + subDirCount), dirDepth: childDepth);

                    parentEntries.Add((subDir.Name, dirInode, Ufs2Constants.DtDir));
                    dirCount++;
                    int cgForDir = (int)(dirInode / (uint)sb.InodesPerGroup);
                    if (cgForDir < dirsPerCg.Length)
                        dirsPerCg[cgForDir]++;
                }
                else if (entry is FileInfo file)
                {
                    if (nextInode >= maxInodes)
                        throw new InvalidOperationException(
                            $"Image is too small: no more inodes available (used {nextInode}, max {maxInodes}). " +
                            "Try increasing image size or use fewer files.");

                    uint fileInode = nextInode++;
                    long fileSize = file.Length;
                    int completedFilesBeforeCurrent = filesWritten;

                    // Allocate blocks and write file content
                    var fileAlloc = AllocateAndWriteFile(
                        fs, writer, file.FullName, fileSize, sb,
                        fragsPerBlock, fragsPerGroup, dataStartFrag,
                        ref nextDataFragInCg, ref currentCg,
                        perCgHighWater: perCgHighWater, totalFrags: totalFrags,
                        onDataBytesWritten: bytes => onCopyProgress?.Invoke(completedFilesBeforeCurrent, bytes));

                    // Track tail-free fragments for the last block of this file.
                    // In UFS, fragment-level allocation (partial last block) only applies
                    // to files that fit entirely in direct block pointers (≤12 data blocks).
                    // Files using indirect blocks always have full-block allocations, so
                    // their tail fragments remain allocated (not freed).
                    if (tailFreeFrags != null && fileSize > 0 && fileAlloc.LastDataBlockFrag != 0)
                    {
                        long fileBlocksNeeded = (fileSize + sb.BSize - 1) / sb.BSize;
                        if (fileBlocksNeeded <= Ufs2Constants.NDirect)
                        {
                            long usedFrags = (fileSize + sb.FSize - 1) / sb.FSize;
                            long allocatedFrags = fileBlocksNeeded * fragsPerBlock;
                            long tailFragCount = allocatedFrags - usedFrags;
                            if (tailFragCount > 0)
                            {
                                // Mark unused tail fragments in the last allocated block as free
                                long lastBlockFrag = fileAlloc.LastDataBlockFrag;
                                long usedInLastBlock = usedFrags % fragsPerBlock;
                                if (usedInLastBlock == 0) usedInLastBlock = fragsPerBlock;
                                for (long tf = usedInLastBlock; tf < fragsPerBlock; tf++)
                                {
                                    tailFreeFrags.Add(lastBlockFrag + tf);
                                }
                            }
                        }
                    }

                    // Write file inode
                    WriteInodeAtPosition(fs, writer, fileInode, inodeTableOffset,
                        inodeSize, sb.InodesPerGroup, fragsPerGroup, sb.FSize,
                        sb.BSize, dataStartFrag, false, fileSize, fileAlloc, timestamp);

                    parentEntries.Add((file.Name, fileInode, Ufs2Constants.DtReg));
                    filesWritten++;
                    onCopyProgress?.Invoke(filesWritten, 0);
                }
            }
        }

        /// <summary>
        /// Allocate the next available data block (in fragment units) within the current CG,
        /// spilling to the next CG when the current one is full.
        /// Returns (fragOffset within CG, CG index).
        /// </summary>
        private static (int fragOffset, int cgIndex) AllocateDataBlock(
            int fragsPerBlock, int fragsPerGroup, int dataStartFrag,
            ref int nextDataFragInCg, ref int currentCg, int numCylGroups,
            int[]? perCgHighWater = null, long totalFrags = 0)
        {
            // Compute the actual fragment limit for the current CG.
            // The last CG may have fewer usable fragments than fragsPerGroup.
            int cgFragLimit = fragsPerGroup;
            if (totalFrags > 0 && currentCg == numCylGroups - 1)
            {
                long lastCgUsable = totalFrags - (long)currentCg * fragsPerGroup;
                if (lastCgUsable < cgFragLimit)
                    cgFragLimit = (int)lastCgUsable;
            }

            if (nextDataFragInCg + fragsPerBlock > cgFragLimit)
            {
                currentCg++;
                if (currentCg >= numCylGroups)
                    throw new InvalidOperationException("Image is too small: no more cylinder groups available for data.");
                nextDataFragInCg = dataStartFrag;

                // Recompute the fragment limit for the new CG, since the last CG
                // may have fewer usable fragments than a full-sized CG.
                cgFragLimit = fragsPerGroup;
                if (totalFrags > 0 && currentCg == numCylGroups - 1)
                {
                    long lastCgUsable = totalFrags - (long)currentCg * fragsPerGroup;
                    if (lastCgUsable < cgFragLimit)
                        cgFragLimit = (int)lastCgUsable;
                }

                if (nextDataFragInCg + fragsPerBlock > cgFragLimit)
                    throw new InvalidOperationException(
                        $"Image is too small: CG {currentCg} has {cgFragLimit - nextDataFragInCg} free frags " +
                        $"at offset {nextDataFragInCg}, need {fragsPerBlock}.");
            }

            int frag = nextDataFragInCg;
            nextDataFragInCg += fragsPerBlock;
            if (perCgHighWater != null)
                perCgHighWater[currentCg] = nextDataFragInCg;
            return (frag, currentCg);
        }

        /// <summary>
        /// Result of block allocation for a file, separating direct and indirect block pointers.
        /// </summary>
        private struct FileBlockAllocation
        {
            public List<long> DirectBlocks;
            public long IndirectBlockFrag;       // 0 if no single-indirect block
            public long DoubleIndirectBlockFrag; // 0 if no double-indirect block
            public long TripleIndirectBlockFrag; // 0 if no triple-indirect block
            public int MetadataBlockCount;       // Number of metadata blocks (indirect, double-indirect, triple-indirect)
            public long LastDataBlockFrag;       // Fragment number of the last data block (for tail-free tracking)
        }

        /// <summary>
        /// Build a FileBlockAllocation for a directory, handling indirect blocks if the
        /// directory needs more than NDirect (12) data blocks. Data blocks have already
        /// been allocated and written; this method allocates the indirect pointer block
        /// (if needed), writes it, and returns the properly split allocation info.
        /// </summary>
        private FileBlockAllocation BuildDirectoryAllocation(
            FileStream fs, BinaryWriter writer, List<long> allDataBlocks,
            Ufs2Superblock sb, int fragsPerBlock, int fragsPerGroup, int dataStartFrag,
            ref int nextDataFragInCg, ref int currentCg,
            int[]? perCgHighWater = null, long totalFrags = 0)
        {
            var alloc = new FileBlockAllocation
            {
                DirectBlocks = allDataBlocks.Count <= Ufs2Constants.NDirect
                    ? allDataBlocks
                    : allDataBlocks.GetRange(0, Ufs2Constants.NDirect),
                IndirectBlockFrag = 0,
                MetadataBlockCount = 0
            };

            if (allDataBlocks.Count > Ufs2Constants.NDirect)
            {
                // Allocate an indirect pointer block
                var (indFragOffset, indCgIdx) = AllocateDataBlock(
                    fragsPerBlock, fragsPerGroup, dataStartFrag,
                    ref nextDataFragInCg, ref currentCg, sb.NumCylGroups,
                    perCgHighWater, totalFrags);
                long indirectBlockFrag = (long)indCgIdx * fragsPerGroup + indFragOffset;
                alloc.IndirectBlockFrag = indirectBlockFrag;
                alloc.MetadataBlockCount = 1;

                // Write the indirect pointer block with pointers to blocks beyond NDirect
                int ptrSize = (FilesystemFormat == 1) ? 4 : 8;
                byte[] indirectBlock = new byte[sb.BSize];
                using var indMs = new MemoryStream(indirectBlock);
                using var indWriter = new BinaryWriter(indMs);
                for (int i = Ufs2Constants.NDirect; i < allDataBlocks.Count; i++)
                {
                    if (FilesystemFormat == 1)
                        indWriter.Write((int)allDataBlocks[i]);
                    else
                        indWriter.Write(allDataBlocks[i]);
                }
                fs.Position = indirectBlockFrag * sb.FSize;
                writer.Write(indirectBlock);
            }

            return alloc;
        }

        /// <summary>
        /// Allocate a single indirect block: allocates data blocks, writes file data, and writes the pointer block.
        /// Returns the number of data blocks written. Updates lastDataBlockFrag with the last allocated data block.
        /// </summary>
        private int AllocateAndWriteSingleIndirectBlock(
            FileStream fs, BinaryWriter writer, FileStream fileStream,
            byte[] buffer, int count, Ufs2Superblock sb, long indirectBlockFrag,
            int fragsPerBlock, int fragsPerGroup, int dataStartFrag,
            ref int nextDataFragInCg, ref int currentCg,
            ref long lastDataBlockFrag,
            int[]? perCgHighWater = null, long totalFrags = 0,
            Action<long>? onDataBytesWritten = null)
        {
            int ptrSize = (FilesystemFormat == 1) ? 4 : 8;
            byte[] indirectBlock = new byte[sb.BSize];
            using var indMs = new MemoryStream(indirectBlock);
            using var indWriter = new BinaryWriter(indMs);

            for (int i = 0; i < count; i++)
            {
                var (dataFragOffset, dataCgIdx) = AllocateDataBlock(
                    fragsPerBlock, fragsPerGroup, dataStartFrag,
                    ref nextDataFragInCg, ref currentCg, sb.NumCylGroups, perCgHighWater,
                    totalFrags);

                long dataBlockFrag = (long)dataCgIdx * fragsPerGroup + dataFragOffset;
                lastDataBlockFrag = dataBlockFrag;

                if (FilesystemFormat == 1)
                    indWriter.Write((int)dataBlockFrag);
                else
                    indWriter.Write(dataBlockFrag);

                // Write file data to the allocated block
                Array.Clear(buffer, 0, buffer.Length);
                int bytesRead = fileStream.Read(buffer, 0, buffer.Length);
                fs.Position = dataBlockFrag * sb.FSize;
                writer.Write(buffer);
                onDataBytesWritten?.Invoke(bytesRead);
            }

            // Write the indirect pointer block
            fs.Position = indirectBlockFrag * sb.FSize;
            writer.Write(indirectBlock);

            return count;
        }

        /// <summary>
        /// Allocate data blocks for a file and write its content to the image.
        /// Supports direct blocks (up to 12), single-indirect, double-indirect, and triple-indirect blocks.
        /// </summary>
        private FileBlockAllocation AllocateAndWriteFile(
            FileStream fs, BinaryWriter writer, string filePath, long fileSize,
            Ufs2Superblock sb, int fragsPerBlock, int fragsPerGroup, int dataStartFrag,
            ref int nextDataFragInCg, ref int currentCg,
            int[]? perCgHighWater = null, long totalFrags = 0,
            Action<long>? onDataBytesWritten = null)
        {
            var result = new FileBlockAllocation
            {
                DirectBlocks = [],
                IndirectBlockFrag = 0,
                DoubleIndirectBlockFrag = 0,
                TripleIndirectBlockFrag = 0,
                MetadataBlockCount = 0,
                LastDataBlockFrag = 0
            };
            if (fileSize == 0)
                return result;

            long blocksNeeded = (fileSize + sb.BSize - 1) / sb.BSize;
            int directBlocksToUse = (int)Math.Min(blocksNeeded, Ufs2Constants.NDirect);
            int ptrSize = (FilesystemFormat == 1) ? 4 : 8;
            int pointersPerBlock = sb.BSize / ptrSize;

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            byte[] buffer = new byte[sb.BSize];
            long lastDataBlockFrag = 0;

            // Allocate and write direct blocks
            for (int i = 0; i < directBlocksToUse; i++)
            {
                var (fragOffset, cgIdx) = AllocateDataBlock(
                    fragsPerBlock, fragsPerGroup, dataStartFrag,
                    ref nextDataFragInCg, ref currentCg, sb.NumCylGroups, perCgHighWater,
                    totalFrags);

                long blockFrag = (long)cgIdx * fragsPerGroup + fragOffset;
                result.DirectBlocks.Add(blockFrag);
                lastDataBlockFrag = blockFrag;

                // Write file data
                Array.Clear(buffer, 0, buffer.Length);
                int bytesRead = fileStream.Read(buffer, 0, buffer.Length);
                fs.Position = blockFrag * sb.FSize;
                writer.Write(buffer);
                onDataBytesWritten?.Invoke(bytesRead);
            }

            long remaining = blocksNeeded - directBlocksToUse;

            // Handle single-indirect blocks if needed
            if (remaining > 0)
            {
                int singleCount = (int)Math.Min(remaining, pointersPerBlock);

                // Allocate the indirect block itself
                var (indFragOffset, indCgIdx) = AllocateDataBlock(
                    fragsPerBlock, fragsPerGroup, dataStartFrag,
                    ref nextDataFragInCg, ref currentCg, sb.NumCylGroups, perCgHighWater,
                    totalFrags);
                long indirectBlockFrag = (long)indCgIdx * fragsPerGroup + indFragOffset;
                result.IndirectBlockFrag = indirectBlockFrag;
                result.MetadataBlockCount++;

                AllocateAndWriteSingleIndirectBlock(fs, writer, fileStream, buffer,
                    singleCount, sb, indirectBlockFrag,
                    fragsPerBlock, fragsPerGroup, dataStartFrag,
                    ref nextDataFragInCg, ref currentCg,
                    ref lastDataBlockFrag, perCgHighWater, totalFrags,
                    onDataBytesWritten);

                remaining -= singleCount;
            }

            // Handle double-indirect blocks if needed
            if (remaining > 0)
            {
                // Allocate the double-indirect block itself
                var (dindFragOffset, dindCgIdx) = AllocateDataBlock(
                    fragsPerBlock, fragsPerGroup, dataStartFrag,
                    ref nextDataFragInCg, ref currentCg, sb.NumCylGroups, perCgHighWater,
                    totalFrags);
                long doubleIndirectBlockFrag = (long)dindCgIdx * fragsPerGroup + dindFragOffset;
                result.DoubleIndirectBlockFrag = doubleIndirectBlockFrag;
                result.MetadataBlockCount++;

                int secondLevelCount = (int)((remaining + pointersPerBlock - 1) / pointersPerBlock);

                byte[] dindBlock = new byte[sb.BSize];
                using var dindMs = new MemoryStream(dindBlock);
                using var dindWriter = new BinaryWriter(dindMs);

                for (int i = 0; i < secondLevelCount; i++)
                {
                    int thisCount = (int)Math.Min(remaining, pointersPerBlock);

                    // Allocate the single-indirect block for this entry
                    var (sindFragOffset, sindCgIdx) = AllocateDataBlock(
                        fragsPerBlock, fragsPerGroup, dataStartFrag,
                        ref nextDataFragInCg, ref currentCg, sb.NumCylGroups, perCgHighWater,
                        totalFrags);
                    long sindBlockFrag = (long)sindCgIdx * fragsPerGroup + sindFragOffset;
                    result.MetadataBlockCount++;

                    if (FilesystemFormat == 1)
                        dindWriter.Write((int)sindBlockFrag);
                    else
                        dindWriter.Write(sindBlockFrag);

                    AllocateAndWriteSingleIndirectBlock(fs, writer, fileStream, buffer,
                        thisCount, sb, sindBlockFrag,
                        fragsPerBlock, fragsPerGroup, dataStartFrag,
                        ref nextDataFragInCg, ref currentCg,
                        ref lastDataBlockFrag, perCgHighWater, totalFrags,
                        onDataBytesWritten);

                    remaining -= thisCount;
                }

                // Write the double-indirect pointer block
                fs.Position = doubleIndirectBlockFrag * sb.FSize;
                writer.Write(dindBlock);
            }

            // Handle triple-indirect blocks if needed
            if (remaining > 0)
            {
                // Allocate the triple-indirect block itself
                var (tindFragOffset, tindCgIdx) = AllocateDataBlock(
                    fragsPerBlock, fragsPerGroup, dataStartFrag,
                    ref nextDataFragInCg, ref currentCg, sb.NumCylGroups, perCgHighWater,
                    totalFrags);
                long tripleIndirectBlockFrag = (long)tindCgIdx * fragsPerGroup + tindFragOffset;
                result.TripleIndirectBlockFrag = tripleIndirectBlockFrag;
                result.MetadataBlockCount++;

                long pointersPerDoubleIndirect = (long)pointersPerBlock * pointersPerBlock;
                int thirdLevelCount = (int)Math.Min(
                    ((long)remaining + pointersPerDoubleIndirect - 1) / pointersPerDoubleIndirect,
                    pointersPerBlock);

                byte[] tindBlock = new byte[sb.BSize];
                using var tindMs = new MemoryStream(tindBlock);
                using var tindWriter = new BinaryWriter(tindMs);

                for (int t = 0; t < thirdLevelCount; t++)
                {
                    // Allocate a double-indirect block
                    var (dindFragOffset2, dindCgIdx2) = AllocateDataBlock(
                        fragsPerBlock, fragsPerGroup, dataStartFrag,
                        ref nextDataFragInCg, ref currentCg, sb.NumCylGroups, perCgHighWater,
                        totalFrags);
                    long dindBlockFrag = (long)dindCgIdx2 * fragsPerGroup + dindFragOffset2;
                    result.MetadataBlockCount++;

                    if (FilesystemFormat == 1)
                        tindWriter.Write((int)dindBlockFrag);
                    else
                        tindWriter.Write(dindBlockFrag);

                    int secondLevelCount = (int)Math.Min(
                        (remaining + pointersPerBlock - 1) / pointersPerBlock,
                        pointersPerBlock);

                    byte[] dindBlock = new byte[sb.BSize];
                    using var dindMs = new MemoryStream(dindBlock);
                    using var dindWriter = new BinaryWriter(dindMs);

                    for (int d = 0; d < secondLevelCount; d++)
                    {
                        int thisCount = (int)Math.Min(remaining, pointersPerBlock);

                        var (sindFragOffset, sindCgIdx) = AllocateDataBlock(
                            fragsPerBlock, fragsPerGroup, dataStartFrag,
                            ref nextDataFragInCg, ref currentCg, sb.NumCylGroups, perCgHighWater,
                            totalFrags);
                        long sindBlockFrag = (long)sindCgIdx * fragsPerGroup + sindFragOffset;
                        result.MetadataBlockCount++;

                        if (FilesystemFormat == 1)
                            dindWriter.Write((int)sindBlockFrag);
                        else
                            dindWriter.Write(sindBlockFrag);

                        AllocateAndWriteSingleIndirectBlock(fs, writer, fileStream, buffer,
                            thisCount, sb, sindBlockFrag,
                            fragsPerBlock, fragsPerGroup, dataStartFrag,
                            ref nextDataFragInCg, ref currentCg,
                            ref lastDataBlockFrag, perCgHighWater, totalFrags,
                            onDataBytesWritten);

                        remaining -= thisCount;
                    }

                    // Write this double-indirect block
                    fs.Position = dindBlockFrag * sb.FSize;
                    writer.Write(dindBlock);
                }

                // Write the triple-indirect pointer block
                fs.Position = tripleIndirectBlockFrag * sb.FSize;
                writer.Write(tindBlock);
            }

            result.LastDataBlockFrag = lastDataBlockFrag;
            return result;
        }

        /// <summary>
        /// Create a padding directory entry with d_ino=0 and the specified record length.
        /// Used to fill gaps in DIRBLKSIZ chunks to prevent fsck_ufs DIRECTORY CORRUPTED errors.
        /// </summary>
        private static Ufs2DirectoryEntry CreatePaddingDirEntry(int recLen)
        {
            if (recLen > ushort.MaxValue)
                throw new ArgumentException(
                    $"Directory entry record length ({recLen}) exceeds maximum ushort value ({ushort.MaxValue}). " +
                    $"This indicates a configuration error with DIRBLKSIZ.");

            return new Ufs2DirectoryEntry
            {
                Inode = 0,         // d_ino = 0 means "empty/deleted slot"
                FileType = 0,      // Explicitly set for padding entry
                NameLength = 0,    // No name for padding entry
                Name = "",
                RecordLength = (ushort)recLen
            };
        }

        /// <summary>
        /// Write directory data blocks with ".", "..", and the given entries.
        /// Supports multi-block directories: if entries don't fit in a single block,
        /// they are split across the provided blocks. "." and ".." are only written
        /// in the first block.
        /// </summary>
        private void WriteDirBlocks(FileStream fs, BinaryWriter writer,
            List<long> blockFrags, int blockSize, int fragSize,
            uint selfInode, uint parentInode,
            List<(string name, uint inode, byte fileType)> entries)
        {
            // Build ordered list: "." and ".." first, then user entries
            var allEntries = new List<(uint inode, byte fileType, string name)>
            {
                (selfInode, Ufs2Constants.DtDir, "."),
                (parentInode, Ufs2Constants.DtDir, "..")
            };
            foreach (var (name, inode, fileType) in entries)
                allEntries.Add((inode, fileType, name));

            int entryIndex = 0;
            int dirBlkSiz = Ufs2Constants.DirBlockSize;

            for (int blockIdx = 0; blockIdx < blockFrags.Count; blockIdx++)
            {
                byte[] dirBlock = new byte[blockSize];
                using var ms = new MemoryStream(dirBlock);
                using var dw = new BinaryWriter(ms);

                while (entryIndex < allEntries.Count)
                {
                    var (inode, fileType, name) = allEntries[entryIndex];
                    bool isUfs2 = FilesystemFormat == 2;
                    ushort recLen = Ufs2DirectoryEntry.CalculateRecordLength((byte)name.Length, isUfs2);
                    int minEntrySize = Ufs2Constants.DirectoryEntryHeaderSize + name.Length + 1;

                    // Validate entry size - directory entries must fit within DIRBLKSIZ chunks
                    if (minEntrySize > dirBlkSiz)
                        throw new InvalidOperationException(
                            $"Directory entry '{name}' is too large ({minEntrySize} bytes) " +
                            $"to fit in a DIRBLKSIZ chunk ({dirBlkSiz} bytes). " +
                            $"Maximum name length is {dirBlkSiz - Ufs2Constants.DirectoryEntryHeaderSize - 1}.");

                    // Per FreeBSD ufs_dirbadentry(): d_reclen must not exceed
                    // DIRBLKSIZ - (entryoffsetinblock & (DIRBLKSIZ - 1)).
                    // Calculate remaining space in the current DIRBLKSIZ chunk.
                    int posInChunk = (int)(ms.Position % dirBlkSiz);
                    int remainingInChunk = dirBlkSiz - posInChunk;

                    // Check if this entry fits in the current DIRBLKSIZ chunk
                    if (minEntrySize > remainingInChunk)
                    {
                        // Skip to the next DIRBLKSIZ-aligned boundary within the block
                        if (ms.Position + remainingInChunk >= blockSize)
                            break; // No more space in this block

                        // Write a padding/sentinel entry to fill the gap
                        // Per FreeBSD ufs_dirbadentry(): each DIRBLKSIZ chunk must contain
                        // valid directory entries. A padding entry has d_ino=0 but must
                        // have a valid d_reclen that covers the remaining space.
                        var paddingEntry = CreatePaddingDirEntry(remainingInChunk);
                        paddingEntry.WriteTo(dw);

                        posInChunk = 0;
                        remainingInChunk = dirBlkSiz;
                    }

                    // Check if we still have space in this filesystem block
                    if (ms.Position + minEntrySize > blockSize)
                        break;

                    // Determine if this should be the last entry in this DIRBLKSIZ chunk.
                    // Per FreeBSD UFS: the last entry in each DIRBLKSIZ chunk must have its
                    // d_reclen extended to fill the remainder of the chunk (for alignment).
                    // An entry is "last in chunk" if:
                    // - It's the last entry overall, OR
                    // - The next entry won't fit in the remaining chunk space AND we're not
                    //   at a chunk boundary (nextPosInChunk != 0), OR
                    // - The next entry won't fit in the block at all
                    bool isLastInChunk = (entryIndex == allEntries.Count - 1);
                    if (!isLastInChunk && entryIndex + 1 < allEntries.Count)
                    {
                        string nextName = allEntries[entryIndex + 1].name;
                        int nextMinSize = Ufs2Constants.DirectoryEntryHeaderSize + nextName.Length + 1;
                        // Recalculate remaining after this entry
                        int afterThisEntry = (int)ms.Position + recLen;
                        int nextPosInChunk = afterThisEntry % dirBlkSiz;
                        int nextRemainingInChunk = dirBlkSiz - nextPosInChunk;
                        // Next entry won't fit in current chunk (and we're mid-chunk)
                        // or won't fit in the block at all
                        isLastInChunk = (nextMinSize > nextRemainingInChunk && nextPosInChunk != 0)
                            || (afterThisEntry + nextMinSize > blockSize);
                    }

                    // Last entry in DIRBLKSIZ chunk gets remaining space to the chunk boundary
                    if (isLastInChunk)
                    {
                        posInChunk = (int)(ms.Position % dirBlkSiz);
                        remainingInChunk = dirBlkSiz - posInChunk;
                        recLen = (ushort)remainingInChunk;
                    }

                    var entry = new Ufs2DirectoryEntry
                    {
                        Inode = inode,
                        FileType = fileType,
                        NameLength = (byte)name.Length,
                        Name = name,
                        RecordLength = recLen
                    };
                    entry.WriteTo(dw);
                    entryIndex++;

                    if (isLastInChunk && entryIndex < allEntries.Count)
                    {
                        // If we just finished a DIRBLKSIZ chunk but there are more entries,
                        // check if there's room for more chunks in this block
                        if (ms.Position >= blockSize)
                            break;
                        // Otherwise continue packing into the next DIRBLKSIZ chunk
                    }
                    else if (isLastInChunk)
                    {
                        break; // All entries written
                    }
                }

                if (ms.Position == 0)
                    throw new InvalidOperationException(
                        "Directory block allocation error: no entries fit in block. " +
                        "This indicates a bug in directory size calculation.");

                // Fill remaining DIRBLKSIZ chunks with valid padding entries to prevent
                // DIRECTORY CORRUPTED errors in fsck_ufs. FreeBSD's fsck validates that
                // every DIRBLKSIZ chunk contains valid directory entries (d_reclen > 0).
                // Zero-filled chunks are considered corrupted because d_reclen == 0 would
                // cause infinite loops when traversing.
                long currentPos = ms.Position;
                int offsetInChunk = (int)(currentPos % dirBlkSiz);

                // If we're not at a DIRBLKSIZ boundary, fill to the next boundary
                if (offsetInChunk != 0)
                {
                    int remainingInCurrentChunk = dirBlkSiz - offsetInChunk;
                    var paddingEntry = CreatePaddingDirEntry(remainingInCurrentChunk);
                    paddingEntry.WriteTo(dw);
                    currentPos = ms.Position;
                }

                // Fill all remaining complete DIRBLKSIZ chunks in the block
                while (currentPos + dirBlkSiz <= blockSize)
                {
                    var paddingEntry = CreatePaddingDirEntry(dirBlkSiz);
                    paddingEntry.WriteTo(dw);
                    currentPos = ms.Position;
                }

                fs.Position = blockFrags[blockIdx] * fragSize;
                writer.Write(dirBlock);
            }

            if (entryIndex < allEntries.Count)
                throw new InvalidOperationException(
                    $"Directory entry write error: only {entryIndex} of {allEntries.Count} entries " +
                    $"were written across {blockFrags.Count} block(s). " +
                    "This indicates a bug in directory block count calculation.");
        }



        /// <summary>
        /// Calculate the number of blocks needed for directory entries including "." and "..".
        /// Simulates the actual WriteDirBlocks packing algorithm which respects DIRBLKSIZ
        /// (512-byte) chunk boundaries per FreeBSD ufs_dirbadentry() validation.
        /// </summary>
        private static int CalculateDirBlocksNeeded(List<(string name, uint inode, byte fileType)> entries, int blockSize, bool isUfs2)
        {
            int dirBlkSiz = Ufs2Constants.DirBlockSize;

            // Build same ordered list as WriteDirBlocks
            var allEntries = new List<(string name, int recLen, int minSize)>
            {
                (".", Ufs2DirectoryEntry.CalculateRecordLength(1, isUfs2), Ufs2Constants.DirectoryEntryHeaderSize + 1 + 1), // "."
                ("..", Ufs2DirectoryEntry.CalculateRecordLength(2, isUfs2), Ufs2Constants.DirectoryEntryHeaderSize + 2 + 1) // ".."
            };
            foreach (var (name, _, _) in entries)
            {
                int recLen = Ufs2DirectoryEntry.CalculateRecordLength((byte)name.Length, isUfs2);
                int minSize = Ufs2Constants.DirectoryEntryHeaderSize + name.Length + 1;
                allEntries.Add((name, recLen, minSize));
            }

            int blocks = 0;
            int entryIdx = 0;
            while (entryIdx < allEntries.Count)
            {
                blocks++;
                int pos = 0;
                while (entryIdx < allEntries.Count)
                {
                    var (name, recLen, minSize) = allEntries[entryIdx];

                    // Validate entry size - directory entries must fit within DIRBLKSIZ chunks
                    if (minSize > dirBlkSiz)
                        throw new InvalidOperationException(
                            $"Directory entry '{name}' is too large ({minSize} bytes) " +
                            $"to fit in a DIRBLKSIZ chunk ({dirBlkSiz} bytes). " +
                            $"Maximum name length is {dirBlkSiz - Ufs2Constants.DirectoryEntryHeaderSize - 1}.");

                    // Check DIRBLKSIZ chunk boundary
                    int posInChunk = pos % dirBlkSiz;
                    int remainingInChunk = dirBlkSiz - posInChunk;

                    if (minSize > remainingInChunk)
                    {
                        // Skip to next DIRBLKSIZ boundary (accounting for padding entry)
                        // In WriteDirBlocks, we write a padding entry here, so we consume
                        // remainingInChunk bytes
                        pos += remainingInChunk;
                        if (pos >= blockSize) break;
                        remainingInChunk = dirBlkSiz;
                    }

                    if (pos + minSize > blockSize) break;

                    // Determine if this is the last entry in the current DIRBLKSIZ chunk
                    bool isLastInChunk = (entryIdx == allEntries.Count - 1);
                    if (!isLastInChunk && entryIdx + 1 < allEntries.Count)
                    {
                        int nextMinSize = allEntries[entryIdx + 1].minSize;
                        int afterThisEntry = pos + recLen;
                        int nextPosInChunk = afterThisEntry % dirBlkSiz;
                        int nextRemainingInChunk = dirBlkSiz - nextPosInChunk;
                        isLastInChunk = (nextMinSize > nextRemainingInChunk && nextPosInChunk != 0)
                            || (afterThisEntry + nextMinSize > blockSize);
                    }

                    if (isLastInChunk)
                    {
                        posInChunk = pos % dirBlkSiz;
                        remainingInChunk = dirBlkSiz - posInChunk;
                        pos += remainingInChunk;
                    }
                    else
                    {
                        pos += recLen;
                    }
                    entryIdx++;
                    if (isLastInChunk && pos >= blockSize) break;
                }
            }
            return Math.Max(1, blocks);
        }

        /// <summary>
        /// Write an inode at the correct position in the inode table.
        /// </summary>
        private void WriteInodeAtPosition(FileStream fs, BinaryWriter writer,
            uint inodeNum, long inodeTableOffset, int inodeSize,
            int inodesPerGroup, int fragsPerGroup, int fragSize,
            int blockSize, int dataStartFrag, bool isDirectory, long size,
            FileBlockAllocation allocation, long timestamp,
            short? nlink = null, uint dirDepth = 0)
        {
            int cgIndex = (int)(inodeNum / inodesPerGroup);
            int inodeIndexInCg = (int)(inodeNum % inodesPerGroup);

            long cgStartByte = (long)cgIndex * fragsPerGroup * fragSize;
            long thisInodeTableOffset;
            if (cgIndex == 0)
            {
                thisInodeTableOffset = inodeTableOffset;
            }
            else
            {
                // Derive iblkno from CG 0's inodeTableOffset
                long iblkno = inodeTableOffset / fragSize;
                thisInodeTableOffset = cgStartByte + iblkno * fragSize;
            }

            long inodeOffset = thisInodeTableOffset + (long)inodeIndexInCg * inodeSize;
            fs.Position = inodeOffset;

            // Compute di_blocks: count allocated space in 512-byte sectors.
            // Per FreeBSD's fsck_ufs expectations (ckinode/iblock in inode.c):
            //   - Files using only direct block pointers (≤12 data blocks): the last
            //     data block may be a partial (fragment-level) allocation, so count it
            //     at fragment granularity.
            //   - Files using indirect blocks (>12 data blocks): ALL data blocks
            //     (including the last one) are full blocks, because UFS always uses
            //     full-block allocation for blocks accessed through indirect pointers.
            //     fsck_ufs counts all indirect-pointed data blocks with id_numfrags =
            //     sblock.fs_frag (set in ckinode before entering the indirect loop and
            //     never adjusted inside iblock for data blocks).
            int fragsPerBlock = blockSize / fragSize;
            int sectorsPerFrag = fragSize / Ufs2Constants.DefaultSectorSize;

            long blocksNeeded = (size > 0) ? (size + blockSize - 1) / blockSize : 0;
            long dataFrags;

            if (blocksNeeded > Ufs2Constants.NDirect)
            {
                // File uses indirect blocks: ALL data blocks are full blocks
                dataFrags = blocksNeeded * fragsPerBlock;
            }
            else
            {
                // File fits in direct blocks: last block may be a partial (fragment) allocation
                long fullBlocks = (size > 0) ? size / blockSize : 0;
                dataFrags = fullBlocks * fragsPerBlock;
                long tailBytes = (size > 0) ? size % blockSize : 0;
                if (tailBytes > 0)
                {
                    long tailFrags = (tailBytes + fragSize - 1) / fragSize;
                    dataFrags += tailFrags;
                }
            }

            // Metadata blocks (indirect, double-indirect, triple-indirect) are always full blocks
            long metadataFrags = (long)allocation.MetadataBlockCount * fragsPerBlock;
            long blocks512 = (dataFrags + metadataFrags) * sectorsPerFrag;

            // Use provided nlink, or default: 2 for directories, 1 for files
            short effectiveNLink = nlink ?? (isDirectory ? (short)2 : (short)1);

            if (FilesystemFormat == 1)
            {
                int time32 = (int)(timestamp & 0xFFFFFFFF);
                var inode = new Ufs1Inode
                {
                    Mode = (ushort)((isDirectory ? Ufs2Constants.IfDir : Ufs2Constants.IfReg) |
                           (isDirectory ? Ufs2Constants.PermDir : Ufs2Constants.PermFile)),
                    NLink = effectiveNLink,
                    Size = size,
                    Blocks = (int)blocks512,
                    AccessTime = time32,
                    ModTime = time32,
                    ChangeTime = time32,
                    Generation = 1
                };
                for (int i = 0; i < Math.Min(allocation.DirectBlocks.Count, Ufs2Constants.NDirect); i++)
                    inode.DirectBlocks[i] = (int)allocation.DirectBlocks[i];
                if (allocation.IndirectBlockFrag != 0)
                    inode.IndirectBlocks[0] = (int)allocation.IndirectBlockFrag;
                if (allocation.DoubleIndirectBlockFrag != 0)
                    inode.IndirectBlocks[1] = (int)allocation.DoubleIndirectBlockFrag;
                if (allocation.TripleIndirectBlockFrag != 0)
                    inode.IndirectBlocks[2] = (int)allocation.TripleIndirectBlockFrag;
                inode.WriteTo(writer);
            }
            else
            {
                var inode = new Ufs2Inode
                {
                    Mode = (ushort)((isDirectory ? Ufs2Constants.IfDir : Ufs2Constants.IfReg) |
                           (isDirectory ? Ufs2Constants.PermDir : Ufs2Constants.PermFile)),
                    NLink = effectiveNLink,
                    BlkSize = (uint)blockSize,
                    Size = size,
                    Blocks = blocks512,
                    AccessTime = timestamp,
                    ModTime = timestamp,
                    ChangeTime = timestamp,
                    CreateTime = timestamp,
                    Generation = 1,
                    DirDepth = isDirectory ? dirDepth : 0
                };
                for (int i = 0; i < Math.Min(allocation.DirectBlocks.Count, Ufs2Constants.NDirect); i++)
                    inode.DirectBlocks[i] = allocation.DirectBlocks[i];
                if (allocation.IndirectBlockFrag != 0)
                    inode.IndirectBlocks[0] = allocation.IndirectBlockFrag;
                if (allocation.DoubleIndirectBlockFrag != 0)
                    inode.IndirectBlocks[1] = allocation.DoubleIndirectBlockFrag;
                if (allocation.TripleIndirectBlockFrag != 0)
                    inode.IndirectBlocks[2] = allocation.TripleIndirectBlockFrag;
                inode.WriteTo(writer);
            }
        }

        /// <summary>
        /// Read the root inode's first direct block pointer.
        /// </summary>
        private long ReadRootDataBlockFrag(FileStream fs, BinaryReader reader,
            long rootInodeOffset, int inodeSize)
        {
            fs.Position = rootInodeOffset;
            if (FilesystemFormat == 1)
            {
                var inode = Ufs1Inode.ReadFrom(reader);
                return inode.DirectBlocks[0];
            }
            else
            {
                var inode = Ufs2Inode.ReadFrom(reader);
                return inode.DirectBlocks[0];
            }
        }

        /// <summary>
        /// Update an inode's size and link count in place.
        /// </summary>
        private void UpdateInodeSize(FileStream fs, BinaryWriter writer,
            long inodeOffset, int inodeSize, long newSize, short nlink)
        {
            // For UFS2: size is at offset 0x10 (8 bytes), nlink at 0x02 (2 bytes)
            // For UFS1: nlink at 0x02, size at 0x08 (8 bytes)
            if (FilesystemFormat == 1)
            {
                fs.Position = inodeOffset + 2; // nlink offset
                writer.Write(nlink);
                fs.Position = inodeOffset + 8; // size offset (after mode, nlink, old_uid, old_gid)
                writer.Write(newSize);
            }
            else
            {
                fs.Position = inodeOffset + 2; // nlink offset
                writer.Write(nlink);
                fs.Position = inodeOffset + 0x10; // size offset
                writer.Write(newSize);
            }
        }

        /// <summary>
        /// Patch CG header bitmaps (inode used + fragment free) and summary counts,
        /// then update the superblock free block/inode/directory counts.
        /// </summary>
        private void PatchCgAndSuperblock(FileStream fs, BinaryReader reader, BinaryWriter writer,
            Ufs2Superblock sb, uint nextInode, int newDirCount,
            int inodeSize, int fragsPerBlock, int fragsPerGroup, int dataStartFrag,
            int nextDataFragInCg, int lastCgWithData, int[] dirsPerCg,
            int[] perCgHighWater,
            HashSet<long>? tailFreeFrags = null)
        {
            long totalFrags = sb.TotalBlocks;
            int numCylGroups = sb.NumCylGroups;

            int totalFreeInodes = 0;
            int totalDirs = 1 + newDirCount; // root + new directories
            long totalFreeBlocks = 0;
            long totalFreeFragRem = 0;

            // Prepare CG summary area to write at CsAddr
            // CG summary data: fragment-aligned (per FreeBSD howmany(cssize, fsize))
            int csFragsFrag = (sb.CsSize + sb.FSize - 1) / sb.FSize;
            // Block-aligned size (for array sizing and root dir positioning)
            int csFragsBlk = ((sb.CsSize + sb.BSize - 1) / sb.BSize) * fragsPerBlock;
            // Tail-free fragments in CG summary's last partial block
            int csSummaryTailFree = csFragsBlk - csFragsFrag;
            byte[] csSummaryData = new byte[csFragsBlk * sb.FSize];

            for (int cg = 0; cg < numCylGroups; cg++)
            {
                long cgStartFrag = (long)cg * fragsPerGroup;
                long cgStartByte = cgStartFrag * sb.FSize;

                int usableFragsInCg = fragsPerGroup;
                if (cg == numCylGroups - 1)
                    usableFragsInCg = (int)(totalFrags - cgStartFrag);

                // Determine which inodes are used in this CG
                int inodesUsedInCg;
                if (cg == 0)
                {
                    inodesUsedInCg = (int)Math.Min(nextInode, (uint)sb.InodesPerGroup);
                }
                else
                {
                    long firstInodeInCg = (long)cg * sb.InodesPerGroup;
                    if (nextInode > firstInodeInCg)
                        inodesUsedInCg = (int)Math.Min(nextInode - firstInodeInCg, (uint)sb.InodesPerGroup);
                    else
                        inodesUsedInCg = 0;
                }
                int freeInodesInCg = sb.InodesPerGroup - inodesUsedInCg;
                totalFreeInodes += freeInodesInCg;

                // Determine which data fragments are used in this CG
                // Use per-CG high-water mark for accurate accounting of trailing
                // unused fragments when a CG overflows to the next one.
                int usedDataFragsInCg;
                if (perCgHighWater[cg] > 0)
                {
                    usedDataFragsInCg = perCgHighWater[cg] - dataStartFrag;
                }
                else
                {
                    usedDataFragsInCg = 0;
                }

                // Patch CG header bitmaps and summary for all CGs
                {
                    long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;

                    // Read the CG header to find bitmap and cluster offsets
                    fs.Position = cgHeaderOffset + 0x5C; // cg_iusedoff
                    int inodeBitmapOff = reader.ReadInt32();
                    int fragBitmapOff = reader.ReadInt32();
                    int nextFreeOff = reader.ReadInt32();
                    int clustersumoffVal = reader.ReadInt32();
                    int clusteroffVal = reader.ReadInt32();
                    int nclusterblksVal = reader.ReadInt32();

                    // Rewrite inode used bitmap
                    int inodeBitmapBytes = (sb.InodesPerGroup + 7) / 8;
                    byte[] inodeBitmap = new byte[inodeBitmapBytes];
                    for (int bit = 0; bit < inodesUsedInCg; bit++)
                    {
                        inodeBitmap[bit / 8] |= (byte)(1 << (bit % 8));
                    }
                    fs.Position = cgHeaderOffset + inodeBitmapOff;
                    writer.Write(inodeBitmap);

                    // Update cg_initediblk (0x78) to reflect actual initialized inodes
                    // Per FreeBSD mkfs.c initcg(): For UFS2, this should be set to the number
                    // of initialized inodes (rounded up to INOPB boundary) so fsck_ffs can
                    // properly scan all allocated inodes in Phase 1.
                    // For UFS1, this field is not used (should remain 0).
                    if (MagicForFormat == Ufs2Constants.Ufs2Magic && inodesUsedInCg > 0)
                    {
                        int inodesPerBlock = sb.BSize / inodeSize;
                        // Round up to nearest INOPB multiple using formula: ((n + d - 1) / d) * d
                        // where n = inodesUsedInCg and d = inodesPerBlock (INOPB)
                        int initediblk = ((inodesUsedInCg + inodesPerBlock - 1) / inodesPerBlock) * inodesPerBlock;
                        // Cap at ipg (total inodes in this CG)
                        initediblk = Math.Min(initediblk, sb.InodesPerGroup);
                        fs.Position = cgHeaderOffset + 0x78;
                        writer.Write(initediblk);
                    }

                    // Rewrite free fragment bitmap (use fragsPerGroup for bitmap size per FreeBSD convention)
                    int fragBitmapBytes = (fragsPerGroup + 7) / 8;
                    byte[] fragBitmap = new byte[fragBitmapBytes];
                    // Per FreeBSD mkfs.c initcg(): for CG > 0, blocks [0, sblkno) are free
                    int sblkno = sb.SuperblockLocation;
                    if (cg > 0)
                    {
                        for (int f = 0; f < sblkno && f < usableFragsInCg; f++)
                        {
                            fragBitmap[f / 8] |= (byte)(1 << (f % 8));
                        }
                    }
                    // Mark free data fragments (starting after used data frags)
                    int firstFreeDataFrag = dataStartFrag + usedDataFragsInCg;
                    for (int f = firstFreeDataFrag; f < usableFragsInCg; f++)
                    {
                        fragBitmap[f / 8] |= (byte)(1 << (f % 8));
                    }
                    // For CG 0: mark CG summary tail fragments as free.
                    // The CG summary only occupies csFragsFrag fragments (fragment-aligned);
                    // the remaining fragments to the next block boundary are free per FreeBSD.
                    if (cg == 0 && csSummaryTailFree > 0)
                    {
                        int tailStart = dataStartFrag + csFragsFrag;
                        int tailEnd = dataStartFrag + csFragsBlk;
                        for (int f = tailStart; f < tailEnd && f < usableFragsInCg; f++)
                        {
                            fragBitmap[f / 8] |= (byte)(1 << (f % 8));
                        }
                    }
                    // Also mark tail-free fragments (unused tail of last block per file) as free
                    if (tailFreeFrags != null)
                    {
                        foreach (long absFrag in tailFreeFrags)
                        {
                            // Check if this fragment belongs to this CG
                            if (absFrag >= cgStartFrag && absFrag < cgStartFrag + usableFragsInCg)
                            {
                                int localFrag = (int)(absFrag - cgStartFrag);
                                if (localFrag < fragBitmapBytes * 8)
                                    fragBitmap[localFrag / 8] |= (byte)(1 << (localFrag % 8));
                            }
                        }
                    }
                    fs.Position = cgHeaderOffset + fragBitmapOff;
                    writer.Write(fragBitmap);

                    // Rewrite cluster bitmap and summary if clustering is enabled
                    if (sb.ContigSumSize > 0 && nclusterblksVal > 0)
                    {
                        byte[] clusterBitmap = new byte[(nclusterblksVal + 7) / 8];
                        for (int blk = 0; blk < nclusterblksVal; blk++)
                        {
                            int fragBase = blk * fragsPerBlock;
                            bool allFree = true;
                            for (int ff = 0; ff < fragsPerBlock; ff++)
                            {
                                int f = fragBase + ff;
                                if (f >= usableFragsInCg) { allFree = false; break; }
                                int byteIdx = f / 8;
                                int bitIdx = f % 8;
                                if (byteIdx >= fragBitmapBytes || (fragBitmap[byteIdx] & (1 << bitIdx)) == 0)
                                { allFree = false; break; }
                            }
                            if (allFree)
                                clusterBitmap[blk / 8] |= (byte)(1 << (blk % 8));
                        }
                        fs.Position = cgHeaderOffset + clusteroffVal;
                        writer.Write(clusterBitmap);

                        // Recompute cluster summary
                        int[] clusterSum = new int[sb.ContigSumSize + 1];
                        int run = 0;
                        for (int blk = 0; blk < nclusterblksVal; blk++)
                        {
                            bool isFree = (clusterBitmap[blk / 8] & (1 << (blk % 8))) != 0;
                            if (isFree) { run++; }
                            else if (run != 0)
                            {
                                clusterSum[Math.Min(run, sb.ContigSumSize)]++;
                                run = 0;
                            }
                        }
                        if (run != 0)
                            clusterSum[Math.Min(run, sb.ContigSumSize)]++;

                        fs.Position = cgHeaderOffset + clustersumoffVal + 4;
                        for (int i = 1; i < sb.ContigSumSize + 1; i++)
                            writer.Write(clusterSum[i]);
                    }

                    // Update CG summary (cs_ndir, cs_nbfree, cs_nifree, cs_nffree)
                    // Compute from the bitmap directly to handle tail-free fragment holes.
                    int dirsInCg = (cg < dirsPerCg.Length) ? dirsPerCg[cg] : 0;
                    int freeBlocksInCg = 0;
                    int freeFragsRemInCg = 0;

                    // Count free blocks: a block is free if all fragsPerBlock fragments are free
                    int totalBlocksInCg = usableFragsInCg / fragsPerBlock;
                    for (int blk = 0; blk < totalBlocksInCg; blk++)
                    {
                        int fragBase = blk * fragsPerBlock;
                        bool allFree = true;
                        for (int ff = 0; ff < fragsPerBlock; ff++)
                        {
                            int f = fragBase + ff;
                            int byteIdx = f / 8;
                            int bitIdx = f % 8;
                            if (byteIdx >= fragBitmapBytes || (fragBitmap[byteIdx] & (1 << bitIdx)) == 0)
                            { allFree = false; break; }
                        }
                        if (allFree) freeBlocksInCg++;
                    }

                    // Count free fragments in partial blocks (not part of a fully-free block)
                    for (int blk = 0; blk < totalBlocksInCg; blk++)
                    {
                        int fragBase = blk * fragsPerBlock;
                        bool allFree = true;
                        int freeInBlock = 0;
                        for (int ff = 0; ff < fragsPerBlock; ff++)
                        {
                            int f = fragBase + ff;
                            int byteIdx = f / 8;
                            int bitIdx = f % 8;
                            if (byteIdx < fragBitmapBytes && (fragBitmap[byteIdx] & (1 << bitIdx)) != 0)
                                freeInBlock++;
                            else
                                allFree = false;
                        }
                        if (!allFree) freeFragsRemInCg += freeInBlock;
                    }
                    // Also count free fragments in the trailing partial block (if CG doesn't divide evenly)
                    int trailingStart = totalBlocksInCg * fragsPerBlock;
                    for (int f = trailingStart; f < usableFragsInCg; f++)
                    {
                        int byteIdx = f / 8;
                        int bitIdx = f % 8;
                        if (byteIdx < fragBitmapBytes && (fragBitmap[byteIdx] & (1 << bitIdx)) != 0)
                            freeFragsRemInCg++;
                    }

                    totalFreeBlocks += freeBlocksInCg;
                    totalFreeFragRem += freeFragsRemInCg;

                    fs.Position = cgHeaderOffset + 0x18; // cs_ndir
                    writer.Write(dirsInCg);
                    writer.Write((int)freeBlocksInCg);
                    writer.Write(freeInodesInCg);
                    writer.Write(freeFragsRemInCg);

                    // Update cg_frsum (0x34) — fragment summary for partial blocks.
                    // Scan the bitmap for runs of free fragments within non-fully-free blocks.
                    int[] frsum = new int[8];
                    for (int blk = 0; blk < totalBlocksInCg; blk++)
                    {
                        int fragBase = blk * fragsPerBlock;
                        // Check if this block is fully free (skip it - counted in cs_nbfree)
                        bool allFreeInBlk = true;
                        for (int ff = 0; ff < fragsPerBlock; ff++)
                        {
                            int f = fragBase + ff;
                            int byteIdx = f / 8;
                            int bitIdx = f % 8;
                            if (byteIdx >= fragBitmapBytes || (fragBitmap[byteIdx] & (1 << bitIdx)) == 0)
                            { allFreeInBlk = false; break; }
                        }
                        if (allFreeInBlk) continue;

                        // Count runs of free fragments in this partial block
                        int runLen = 0;
                        for (int ff = 0; ff < fragsPerBlock; ff++)
                        {
                            int f = fragBase + ff;
                            int byteIdx = f / 8;
                            int bitIdx = f % 8;
                            bool isFree = byteIdx < fragBitmapBytes && (fragBitmap[byteIdx] & (1 << bitIdx)) != 0;
                            if (isFree)
                            {
                                runLen++;
                            }
                            else
                            {
                                if (runLen > 0 && runLen < 8) frsum[runLen]++;
                                runLen = 0;
                            }
                        }
                        if (runLen > 0 && runLen < 8) frsum[runLen]++;
                    }
                    // Handle trailing partial block
                    {
                        int runLen = 0;
                        for (int f = trailingStart; f < usableFragsInCg; f++)
                        {
                            int byteIdx = f / 8;
                            int bitIdx = f % 8;
                            bool isFree = byteIdx < fragBitmapBytes && (fragBitmap[byteIdx] & (1 << bitIdx)) != 0;
                            if (isFree) { runLen++; }
                            else
                            {
                                if (runLen > 0 && runLen < 8) frsum[runLen]++;
                                runLen = 0;
                            }
                        }
                        if (runLen > 0 && runLen < 8) frsum[runLen]++;
                    }
                    fs.Position = cgHeaderOffset + 0x34;
                    for (int i = 0; i < 8; i++)
                        writer.Write(frsum[i]);

                    // Write per-CG summary into the CG summary area at CsAddr
                    int csOffset = cg * Ufs2Constants.CsumStructSize;
                    if (csOffset + Ufs2Constants.CsumStructSize <= csSummaryData.Length)
                    {
                        using var csMs = new MemoryStream(csSummaryData, csOffset, Ufs2Constants.CsumStructSize, writable: true);
                        using var csW = new BinaryWriter(csMs);
                        csW.Write(dirsInCg);               // cs_ndir
                        csW.Write((int)freeBlocksInCg);    // cs_nbfree
                        csW.Write(freeInodesInCg);         // cs_nifree
                        csW.Write(freeFragsRemInCg);       // cs_nffree
                    }
                }
            }

            // Write the CG summary area at CsAddr
            fs.Position = sb.CsAddr * sb.FSize;
            writer.Write(csSummaryData);

            // Update superblock
            sb.FreeBlocks = totalFreeBlocks;
            sb.FreeFragments = totalFreeFragRem;
            sb.FreeInodes = totalFreeInodes;
            sb.Directories = totalDirs;
            // Note: NumClusters is intentionally left at 0 in fs_cstotal.
            // FreeBSD's fsck_ffs pass5.c uses memcmp on the entire struct csum_total
            // but only accumulates cs_ndir/cs_nbfree/cs_nifree/cs_nffree — cs_numclusters
            // stays 0. FreeBSD's newfs also does not set cs_numclusters in fs_cstotal.

            byte[] sbData = SerializeSuperblock(sb);
            fs.Position = Ufs2Constants.SuperblockOffset64K;
            fs.Write(sbData, 0, sbData.Length);

            // Update backup superblocks in each CG (CG > 0)
            for (int cg = 1; cg < numCylGroups; cg++)
            {
                long cgStartByte = (long)cg * fragsPerGroup * sb.FSize;
                long backupSbOffset = cgStartByte + (long)sb.SuperblockLocation * sb.FSize;
                int usableFragsInCg = fragsPerGroup;
                if (cg == numCylGroups - 1)
                    usableFragsInCg = (int)(totalFrags - (long)cg * fragsPerGroup);
                if (backupSbOffset + Ufs2Constants.SuperblockSize <=
                    cgStartByte + (long)usableFragsInCg * sb.FSize)
                {
                    fs.Position = backupSbOffset;
                    fs.Write(sbData, 0, sbData.Length);
                }
            }
        }

        // --- Utility ---

        private static long AlignUp(long value, int alignment)
        {
            return ((value + alignment - 1) / alignment) * alignment;
        }

        private static long AlignUp(long value, long alignment)
        {
            return ((value + alignment - 1) / alignment) * alignment;
        }

        private static int AlignUpInt(int value, int alignment)
        {
            return ((value + alignment - 1) / alignment) * alignment;
        }

        private static bool IsPowerOfTwo(int value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        private static int Log2(int value)
        {
            int result = 0;
            while (value > 1) { value >>= 1; result++; }
            return result;
        }
    }
}
