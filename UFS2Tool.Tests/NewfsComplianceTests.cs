// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests that verify the created UFS2 image fully complies with FreeBSD's
    /// newfs(8) output, including superblock fields, legacy compatibility fields,
    /// and CG summary consistency.
    /// </summary>
    public class NewfsComplianceTests : IDisposable
    {
        private readonly string _imagePath;

        public NewfsComplianceTests()
        {
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2comply_{Guid.NewGuid():N}.img");
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath))
                File.Delete(_imagePath);
        }

        /// <summary>
        /// Verify that fs_maxbsize is set to MAXBSIZE (65536) per FreeBSD newfs defaults.
        /// </summary>
        [Fact]
        public void Superblock_MaxBSizeIsMaxBSize()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            Assert.Equal(Ufs2Constants.MaxBSize, sb.MaxBSize);
        }

        /// <summary>
        /// Verify that fs_maxcontig is set to maxbsize/bsize per FreeBSD mkfs.c defaults.
        /// </summary>
        [Fact]
        public void Superblock_MaxContigIsMaxBSizeDivBSize()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            int expectedMaxContig = Math.Max(1, Ufs2Constants.MaxBSize / sb.BSize);
            Assert.Equal(expectedMaxContig, sb.MaxContig);
        }

        /// <summary>
        /// Verify that fs_contigsumsize = min(maxcontig, FS_MAXCONTIG).
        /// </summary>
        [Fact]
        public void Superblock_ContigSumSizeMatchesMaxContig()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            int expectedContigSumSize = Math.Min(sb.MaxContig, Ufs2Constants.MaxContig);
            Assert.Equal(expectedContigSumSize, sb.ContigSumSize);
        }

        /// <summary>
        /// Verify that fs_sblockactualloc is set to the superblock offset.
        /// </summary>
        [Fact]
        public void Superblock_SblockActualLocIsSet()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // Read fs_sblockactualloc at offset 0x3E0
            fs.Position = Ufs2Constants.SuperblockOffset64K + 0x3E0;
            long sblockActualLoc = reader.ReadInt64();

            Assert.Equal(Ufs2Constants.SuperblockOffset64K, sblockActualLoc);
            Assert.Equal(Ufs2Constants.SuperblockOffset64K, sb.SbBlockLoc);
        }

        /// <summary>
        /// Verify that FreeInodes = total_inodes - 3 (inodes 0, 1, 2 are used).
        /// </summary>
        [Fact]
        public void Superblock_FreeInodesIsCorrect()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            long expectedFreeInodes = (long)sb.NumCylGroups * sb.InodesPerGroup - 3;
            Assert.Equal(expectedFreeInodes, sb.FreeInodes);
        }

        /// <summary>
        /// Verify that the superblock's FreeInodes matches the sum of per-CG free inode counts.
        /// This catches the -2 vs -3 discrepancy that fsck_ufs would flag.
        /// </summary>
        [Fact]
        public void Superblock_FreeInodesMatchesCgSum()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            long totalFreeInodes = 0;
            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;
                fs.Position = cgHeaderOffset + 0x20; // cs_nifree
                int cgFreeInodes = reader.ReadInt32();
                totalFreeInodes += cgFreeInodes;
            }

            Assert.Equal(sb.FreeInodes, totalFreeInodes);
        }

        /// <summary>
        /// Verify that cs_numclusters in fs_cstotal is zero per FreeBSD convention.
        /// FreeBSD's fsck_ffs pass5.c uses memcmp on the entire struct csum_total
        /// but only accumulates cs_ndir/cs_nbfree/cs_nifree/cs_nffree — cs_numclusters
        /// stays 0. FreeBSD's newfs also does not set cs_numclusters in fs_cstotal.
        /// A non-zero value triggers "SUMMARY BLK COUNT(S) WRONG IN SUPERBLK".
        /// </summary>
        [Fact]
        public void Superblock_NumClustersIsZero()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            Assert.Equal(0, sb.NumClusters);
        }

        /// <summary>
        /// Verify FreeBSD legacy compatibility fields are correctly set.
        /// </summary>
        [Fact]
        public void Superblock_LegacyFieldsAreSet()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // fs_old_csaddr (0x098) should be set to CsAddr
            fs.Position = Ufs2Constants.SuperblockOffset64K + 0x098;
            int oldCsAddr = reader.ReadInt32();
            Assert.Equal((int)(sb.CsAddr & 0xFFFFFFFF), oldCsAddr);

            // fs_old_ncyl (0x0B0) should equal NumCylGroups
            fs.Position = Ufs2Constants.SuperblockOffset64K + 0x0B0;
            int oldNcyl = reader.ReadInt32();
            Assert.Equal(sb.NumCylGroups, oldNcyl);

            // fs_old_cpg (0x0B4) should be 1
            int oldCpg = reader.ReadInt32();
            Assert.Equal(1, oldCpg);

            // fs_old_postblformat (0x54C) should be -1 (FS_DYNAMICPOSTBLFMT)
            fs.Position = Ufs2Constants.SuperblockOffset64K + 0x54C;
            int oldPostblFmt = reader.ReadInt32();
            Assert.Equal(Ufs2Constants.FsDynamicPostblFmt, oldPostblFmt);

            // fs_old_nrpos (0x550) should be 1
            int oldNrpos = reader.ReadInt32();
            Assert.Equal(1, oldNrpos);
        }

        /// <summary>
        /// Verify that fs_dsize correctly accounts for CG summary area overhead.
        /// fs_dsize = fs_size - fs_sblkno - ncg * (fs_dblkno - fs_sblkno) - howmany(cssize, fsize)
        /// </summary>
        [Fact]
        public void Superblock_TotalDataBlocksSubtractsCsSummary()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // Per FreeBSD mkfs.c: csfrags = howmany(cssize, fsize) (fragment-aligned)
            int csFrags = (sb.CsSize + sb.FSize - 1) / sb.FSize;
            long expectedDsize = sb.TotalBlocks - sb.SuperblockLocation -
                (long)sb.NumCylGroups * (sb.DblkNo - sb.SuperblockLocation) -
                csFrags;

            Assert.Equal(expectedDsize, sb.TotalDataBlocks);
        }

        /// <summary>
        /// Verify that directory entry alignment uses 4-byte boundaries for both
        /// UFS1 and UFS2, matching FreeBSD's DIRECTSIZ macro in sys/ufs/ufs/dir.h:
        ///   (offsetof(struct direct, d_name) + (namlen) + 1 + 3) &amp; ~3
        /// </summary>
        [Theory]
        [InlineData(1, true)]   // "." in UFS2
        [InlineData(2, true)]   // ".." in UFS2
        [InlineData(8, true)]   // 8-char name in UFS2
        [InlineData(15, true)]  // 15-char name in UFS2
        [InlineData(1, false)]  // "." in UFS1
        [InlineData(2, false)]  // ".." in UFS1
        [InlineData(8, false)]  // 8-char name in UFS1
        [InlineData(15, false)] // 15-char name in UFS1
        public void DirectoryEntry_AlignmentIs4Bytes_BothFormats(int nameLen, bool isUfs2)
        {
            ushort recLen = Ufs2DirectoryEntry.CalculateRecordLength(nameLen, isUfs2);

            // FreeBSD DIRECTSIZ: (8 + namlen + 1 + 3) & ~3
            int expected = (Ufs2Constants.DirectoryEntryHeaderSize + nameLen + 1 + 3) & ~3;
            Assert.Equal((ushort)expected, recLen);

            // Must be 4-byte aligned
            Assert.Equal(0, recLen % 4);
        }

        /// <summary>
        /// Verify UFS1 and UFS2 produce identical directory entry sizes, since both
        /// use the same struct direct layout per FreeBSD.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(10)]
        [InlineData(255)]
        public void DirectoryEntry_SameSize_UFS1_And_UFS2(int nameLen)
        {
            ushort ufs1 = Ufs2DirectoryEntry.CalculateRecordLength(nameLen, false);
            ushort ufs2 = Ufs2DirectoryEntry.CalculateRecordLength(nameLen, true);
            Assert.Equal(ufs1, ufs2);
        }

        /// <summary>
        /// Verify that the root directory inode has 0755 permissions per FreeBSD convention.
        /// </summary>
        [Fact]
        public void RootInode_HasCorrectPermissions()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootInode = image.ReadInode(Ufs2Constants.RootInode);

            // Mode should be IfDir | 0755
            ushort expectedMode = (ushort)(Ufs2Constants.IfDir | Ufs2Constants.PermDir);
            Assert.Equal(expectedMode, rootInode.Mode);
            Assert.True(rootInode.IsDirectory);

            // Permission bits only (lower 12 bits)
            int permBits = rootInode.Mode & 0xFFF;
            Assert.Equal(Ufs2Constants.PermDir, (ushort)permBits);
        }

        /// <summary>
        /// Verify that populated file inodes have 0644 permissions and
        /// directory inodes have 0755 permissions per FreeBSD convention.
        /// </summary>
        [Fact]
        public void PopulatedInodes_HaveCorrectPermissions()
        {
            var testDir = Path.Combine(Path.GetTempPath(), $"ufs2perm_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(testDir);
                Directory.CreateDirectory(Path.Combine(testDir, "subdir"));
                File.WriteAllText(Path.Combine(testDir, "test.txt"), "hello");

                var creator = new Ufs2ImageCreator();
                long imageSize = 256 * 1024 * 1024;
                creator.CreateImage(_imagePath, imageSize);
                creator.PopulateFromDirectory(_imagePath, testDir);

                using var image = new Ufs2Image(_imagePath, readOnly: true);
                var rootEntries = image.ListRoot();

                // Check file permissions
                var fileEntry = rootEntries.First(e => e.Name == "test.txt");
                var fileInode = image.ReadInode(fileEntry.Inode);
                int filePermBits = fileInode.Mode & 0xFFF;
                Assert.Equal(Ufs2Constants.PermFile, (ushort)filePermBits);

                // Check directory permissions
                var dirEntry = rootEntries.First(e => e.Name == "subdir");
                var dirInode = image.ReadInode(dirEntry.Inode);
                int dirPermBits = dirInode.Mode & 0xFFF;
                Assert.Equal(Ufs2Constants.PermDir, (ushort)dirPermBits);
            }
            finally
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir, recursive: true);
            }
        }

        /// <summary>
        /// Verify that UFS1 root inode also has 0755 permissions.
        /// </summary>
        [Fact]
        public void RootInode_UFS1_HasCorrectPermissions()
        {
            var creator = new Ufs2ImageCreator { FilesystemFormat = 1 };
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootInode = image.ReadInode(Ufs2Constants.RootInode);

            Assert.True(rootInode.IsDirectory);
            int permBits = rootInode.Mode & 0xFFF;
            Assert.Equal(Ufs2Constants.PermDir, (ushort)permBits);
        }

        /// <summary>
        /// Verify large files with subdirectories are properly added and readable.
        /// Creates a mix of large files (requiring indirect blocks) alongside
        /// deeply nested subdirectories with their own files.
        /// </summary>
        [Fact]
        public void LargeFilesWithSubdirectories_ProperlyAdded()
        {
            var testDir = Path.Combine(Path.GetTempPath(), $"ufs2large_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(testDir);

                // Create a large file requiring indirect blocks (>12 blocks × 32KB = >384KB)
                byte[] largeData = new byte[600_000];
                new Random(42).NextBytes(largeData);
                File.WriteAllBytes(Path.Combine(testDir, "large.bin"), largeData);

                // Create subdirectories with files
                for (int i = 0; i < 5; i++)
                {
                    string subDir = Path.Combine(testDir, $"dir_{i}");
                    Directory.CreateDirectory(subDir);

                    string nestedDir = Path.Combine(subDir, "nested");
                    Directory.CreateDirectory(nestedDir);

                    File.WriteAllText(Path.Combine(subDir, "file.txt"), $"content_{i}");
                    File.WriteAllBytes(Path.Combine(nestedDir, "data.bin"), new byte[50_000]);
                }

                var creator = new Ufs2ImageCreator();
                creator.CreateImageFromDirectory(_imagePath, testDir);

                using var image = new Ufs2Image(_imagePath, readOnly: true);
                var sb = image.Superblock;
                Assert.True(sb.IsValid);

                // Verify large file
                var rootEntries = image.ListRoot();
                var largeEntry = rootEntries.First(e => e.Name == "large.bin");
                byte[] readData = image.ReadFile(largeEntry.Inode);
                Assert.Equal(largeData.Length, readData.Length);
                Assert.Equal(largeData, readData);

                // Verify all subdirectories and nested content
                for (int i = 0; i < 5; i++)
                {
                    var dirEntry = rootEntries.First(e => e.Name == $"dir_{i}");
                    var dirEntries = image.ListDirectory(dirEntry.Inode);

                    Assert.Contains(dirEntries, e => e.Name == "file.txt");
                    Assert.Contains(dirEntries, e => e.Name == "nested" && e.FileType == Ufs2Constants.DtDir);

                    var nestedEntry = dirEntries.First(e => e.Name == "nested");
                    var nestedEntries = image.ListDirectory(nestedEntry.Inode);
                    Assert.Contains(nestedEntries, e => e.Name == "data.bin");
                }
            }
            finally
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir, recursive: true);
            }
        }

        /// <summary>
        /// Verify that all directory entries respect the DIRBLKSIZ (512-byte) boundary
        /// constraint per FreeBSD ufs_dirbadentry(): d_reclen must not exceed
        /// DIRBLKSIZ - (entryoffsetinblock AND (DIRBLKSIZ - 1)).
        /// </summary>
        [Fact]
        public void DirectoryEntries_RespectDirBlkSizBoundary()
        {
            var testDir = Path.Combine(Path.GetTempPath(), $"ufs2dirblk_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(testDir);
                // Create several files and subdirectories to populate the root directory
                for (int i = 0; i < 10; i++)
                    File.WriteAllText(Path.Combine(testDir, $"file_{i}.txt"), $"content {i}");
                for (int i = 0; i < 3; i++)
                {
                    string sub = Path.Combine(testDir, $"subdir_{i}");
                    Directory.CreateDirectory(sub);
                    File.WriteAllText(Path.Combine(sub, "inner.txt"), "inner");
                }

                var creator = new Ufs2ImageCreator();
                long imageSize = 256 * 1024 * 1024;
                creator.CreateImage(_imagePath, imageSize);
                creator.PopulateFromDirectory(_imagePath, testDir);

                using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);

                fs.Position = Ufs2Constants.SuperblockOffset64K;
                var sb = Ufs2Superblock.ReadFrom(reader);

                // Read the root inode to find the root directory data block
                long inodeTableOffset = (long)sb.IblkNo * sb.FSize;
                fs.Position = inodeTableOffset + (long)Ufs2Constants.RootInode * Ufs2Constants.Ufs2InodeSize;
                var rootInode = Ufs2Inode.ReadFrom(reader);

                // For each data block in the root directory, validate all entries
                int blocksToCheck = (int)((rootInode.Size + sb.BSize - 1) / sb.BSize);
                for (int blkIdx = 0; blkIdx < blocksToCheck && blkIdx < Ufs2Constants.NDirect; blkIdx++)
                {
                    long blockFrag = rootInode.DirectBlocks[blkIdx];
                    if (blockFrag == 0) continue;

                    long blockOffset = blockFrag * sb.FSize;
                    fs.Position = blockOffset;
                    byte[] blockData = reader.ReadBytes(sb.BSize);

                    // Walk through entries in this block and validate DIRBLKSIZ constraint
                    int pos = 0;
                    while (pos < sb.BSize)
                    {
                        // Read d_ino (4 bytes) and d_reclen (2 bytes)
                        uint entryIno = BitConverter.ToUInt32(blockData, pos);
                        ushort reclen = BitConverter.ToUInt16(blockData, pos + 4);
                        if (reclen == 0) break;

                        // Per FreeBSD ufs_dirbadentry():
                        // d_reclen must not exceed DIRBLKSIZ - (entryoffsetinblock & (DIRBLKSIZ - 1))
                        int posInChunk = pos & (Ufs2Constants.DirBlockSize - 1);
                        int maxReclen = Ufs2Constants.DirBlockSize - posInChunk;
                        Assert.True(reclen <= maxReclen,
                            $"Block {blkIdx} offset {pos}: reclen {reclen} exceeds DIRBLKSIZ limit {maxReclen} " +
                            $"(entry at chunk offset {posInChunk})");

                        // reclen must be 4-byte aligned
                        Assert.Equal(0, reclen % 4);

                        pos += reclen;
                    }
                }
            }
            finally
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir, recursive: true);
            }
        }

        /// <summary>
        /// Verify that the empty root directory also respects DIRBLKSIZ boundaries.
        /// </summary>
        [Fact]
        public void EmptyRootDirectory_RespectsDirBlkSizBoundary()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            long inodeTableOffset = (long)sb.IblkNo * sb.FSize;
            fs.Position = inodeTableOffset + (long)Ufs2Constants.RootInode * Ufs2Constants.Ufs2InodeSize;
            var rootInode = Ufs2Inode.ReadFrom(reader);

            long blockFrag = rootInode.DirectBlocks[0];
            long blockOffset = blockFrag * sb.FSize;
            fs.Position = blockOffset;
            byte[] blockData = reader.ReadBytes(sb.BSize);

            // Walk through entries in the root directory block
            int pos = 0;
            int entryCount = 0;
            while (pos < sb.BSize)
            {
                uint entryIno = BitConverter.ToUInt32(blockData, pos);
                ushort reclen = BitConverter.ToUInt16(blockData, pos + 4);
                if (reclen == 0) break;

                int posInChunk = pos & (Ufs2Constants.DirBlockSize - 1);
                int maxReclen = Ufs2Constants.DirBlockSize - posInChunk;
                Assert.True(reclen <= maxReclen,
                    $"Root dir offset {pos}: reclen {reclen} exceeds DIRBLKSIZ limit {maxReclen}");
                Assert.Equal(0, reclen % 4);

                if (entryIno != 0)
                    entryCount++;
                pos += reclen;
            }

            // Should have at least "." and ".." entries
            Assert.True(entryCount >= 2, $"Expected at least 2 entries (. and ..) but found {entryCount}");
        }

        /// <summary>
        /// Verify that fs_old_flags (at offset 0x0D3) contains FS_FLAGS_UPDATED (0x80).
        /// Without this flag, fsck_ffs asks "UPDATE FILESYSTEM TO TRACK DIRECTORY DEPTH?".
        /// FreeBSD newfs always sets this flag (see sbin/newfs/mkfs.c).
        /// </summary>
        [Fact]
        public void Superblock_OldFlagsHasFlagsUpdated()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // fs_old_flags is at superblock offset 0x0D3 (1 byte)
            fs.Position = Ufs2Constants.SuperblockOffset64K + 0x0D3;
            byte oldFlags = reader.ReadByte();

            Assert.True((oldFlags & Ufs2Constants.FsFlagsUpdated) != 0,
                $"fs_old_flags (0x{oldFlags:X2}) should have FS_FLAGS_UPDATED (0x{Ufs2Constants.FsFlagsUpdated:X2}) set");
        }

        /// <summary>
        /// Verify that for CG > 0, fragments [0, sblkno) are marked as free in the bitmap.
        /// FreeBSD mkfs.c initcg() marks these blocks as free because the boot/superblock
        /// area is only needed in CG 0.
        /// </summary>
        [Fact]
        public void CylinderGroup_BootAreaFreeForNonZeroCGs()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            Assert.True(sb.NumCylGroups > 1, "Need at least 2 CGs for this test");

            int sblkno = sb.SuperblockLocation;
            Assert.True(sblkno > 0, "sblkno should be > 0");

            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;

                // Read bitmap offset
                fs.Position = cgHeaderOffset + 0x60; // cg_freeoff
                int freeoff = reader.ReadInt32();

                // Read fragment bitmap
                int bitmapBytes = (sb.CylGroupSize + 7) / 8;
                fs.Position = cgHeaderOffset + freeoff;
                byte[] bitmap = reader.ReadBytes(bitmapBytes);

                // For CG 0: fragments [0, sblkno) should NOT be free (boot area)
                // For CG > 0: fragments [0, sblkno) SHOULD be free
                for (int f = 0; f < sblkno; f++)
                {
                    bool isFree = (bitmap[f / 8] & (1 << (f % 8))) != 0;
                    if (cg == 0)
                    {
                        Assert.False(isFree,
                            $"CG 0: fragment {f} (boot area) should NOT be free");
                    }
                    else
                    {
                        Assert.True(isFree,
                            $"CG {cg}: fragment {f} (before sblkno={sblkno}) should be free");
                    }
                }
            }
        }

        /// <summary>
        /// Verify that superblock free block count equals the sum of per-CG cs_nbfree values.
        /// This catches the "FREE BLK COUNT(S) WRONG IN SUPERBLK" fsck error.
        /// </summary>
        [Fact]
        public void Superblock_FreeBlocksMatchesCgSum()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            long totalFreeBlocks = 0;
            long totalFreeFrags = 0;

            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;

                fs.Position = cgHeaderOffset + 0x1C; // cs_nbfree
                int cgFreeBlocks = reader.ReadInt32();
                fs.Position = cgHeaderOffset + 0x24; // cs_nffree
                int cgFreeFrags = reader.ReadInt32();

                totalFreeBlocks += cgFreeBlocks;
                totalFreeFrags += cgFreeFrags;
            }

            Assert.Equal(sb.FreeBlocks, totalFreeBlocks);
            Assert.Equal(sb.FreeFragments, totalFreeFrags);
        }
        /// <summary>
        /// Verify that the CG summary area tail fragments are correctly marked free in
        /// the CG 0 bitmap. This is what FreeBSD's fsck_ffs pass1.c marks as used:
        ///   fs_csaddr to fs_csaddr + howmany(fs_cssize, fs_fsize)
        /// The remaining fragments to the next block boundary should be free.
        /// This catches the "BLK(S) MISSING IN BIT MAPS" fsck error.
        /// </summary>
        [Fact]
        public void CylinderGroup0_CsSummaryTailFragsAreFree()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // Compute CG summary fragment counts (matching FreeBSD formulas)
            int csFragsFrag = (sb.CsSize + sb.FSize - 1) / sb.FSize; // howmany(cssize, fsize)
            int csFragsBlk = ((sb.CsSize + sb.BSize - 1) / sb.BSize) * sb.FragsPerBlock;
            int csSummaryTailFree = csFragsBlk - csFragsFrag;

            Assert.True(csSummaryTailFree >= 0,
                $"CG summary tail free should be >= 0, got {csSummaryTailFree}");

            if (csSummaryTailFree == 0)
                return; // No tail fragments to check (CG summary is block-aligned)

            // Read CG 0's fragment bitmap
            long cgHeaderOffset = (long)sb.CblkNo * sb.FSize;
            fs.Position = cgHeaderOffset + 0x60; // cg_freeoff
            int freeoff = reader.ReadInt32();

            int bitmapBytes = (sb.CylGroupSize + 7) / 8;
            fs.Position = cgHeaderOffset + freeoff;
            byte[] bitmap = reader.ReadBytes(bitmapBytes);

            // CG summary occupies [dblkno, dblkno + csFragsFrag) — should be USED (bit=0)
            int dblkno = sb.DblkNo;
            for (int f = dblkno; f < dblkno + csFragsFrag; f++)
            {
                bool isFree = (bitmap[f / 8] & (1 << (f % 8))) != 0;
                Assert.False(isFree,
                    $"CG 0: fragment {f} (CG summary data) should be USED but is marked FREE");
            }

            // Tail fragments [dblkno + csFragsFrag, dblkno + csFragsBlk) — should be FREE (bit=1)
            for (int f = dblkno + csFragsFrag; f < dblkno + csFragsBlk; f++)
            {
                bool isFree = (bitmap[f / 8] & (1 << (f % 8))) != 0;
                Assert.True(isFree,
                    $"CG 0: fragment {f} (CG summary tail) should be FREE but is marked USED. " +
                    $"csFragsFrag={csFragsFrag}, csFragsBlk={csFragsBlk}");
            }

            // Root dir block [dblkno + csFragsBlk, dblkno + csFragsBlk + fpb) — should be USED
            for (int f = dblkno + csFragsBlk; f < dblkno + csFragsBlk + sb.FragsPerBlock; f++)
            {
                bool isFree = (bitmap[f / 8] & (1 << (f % 8))) != 0;
                Assert.False(isFree,
                    $"CG 0: fragment {f} (root dir block) should be USED but is marked FREE");
            }
        }

        /// <summary>
        /// Simulate fsck_ffs pass5 bitmap check: for each CG, recompute free
        /// block/fragment counts from the bitmap and verify they match the CG
        /// header's summary. This specifically tests that CG summary tail
        /// fragments are properly reflected in both the bitmap AND the counts.
        /// </summary>
        [Fact]
        public void Fsck_Pass5_BitmapMatchesCgSummary_WithCsSummaryTail()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // Simulate fsck pass1: mark metadata and CG summary as used
            int totalFrags = (int)sb.TotalBlocks;
            bool[] usedMap = new bool[totalFrags];

            // Mark metadata for each CG (per fsck pass1.c)
            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgBase = (long)cg * sb.CylGroupSize;
                long cgd = cgBase + sb.DblkNo;
                long start = (cg == 0) ? cgBase : cgBase + sb.SuperblockLocation;
                for (long f = start; f < cgd && f < totalFrags; f++)
                    usedMap[f] = true;
            }
            // Mark CG summary area (per fsck pass1.c)
            long csAddr = sb.CsAddr;
            long csEnd = csAddr + (sb.CsSize + sb.FSize - 1) / sb.FSize;
            for (long f = csAddr; f < csEnd && f < totalFrags; f++)
                usedMap[f] = true;

            // Mark root directory block (from root inode)
            long inodeTableOffset = (long)sb.IblkNo * sb.FSize;
            fs.Position = inodeTableOffset + (long)Ufs2Constants.RootInode * Ufs2Constants.Ufs2InodeSize;
            var rootInode = Ufs2Inode.ReadFrom(reader);
            if (rootInode.DirectBlocks[0] != 0)
            {
                long rootFrag = rootInode.DirectBlocks[0];
                int rootFrags = (int)((rootInode.Size + sb.FSize - 1) / sb.FSize);
                for (int f = 0; f < rootFrags; f++)
                    if (rootFrag + f < totalFrags)
                        usedMap[rootFrag + f] = true;
            }

            // Now simulate pass5: for each CG, compute bitmap from usedMap and compare
            long sumFreeBlocks = 0, sumFreeFrags = 0;
            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgBase = (long)cg * sb.CylGroupSize;
                long cgEnd = Math.Min(cgBase + sb.CylGroupSize, totalFrags);

                // Compute expected free blocks and free fragments from usedMap
                int bitmapFreeBlocks = 0;
                int bitmapFreeFragRem = 0;

                for (long d = cgBase; d < cgEnd; d += sb.FragsPerBlock)
                {
                    int frags = 0;
                    for (int j = 0; j < sb.FragsPerBlock; j++)
                    {
                        long f = d + j;
                        if (f >= totalFrags || usedMap[f])
                            continue;
                        frags++;
                    }
                    if (frags == sb.FragsPerBlock)
                        bitmapFreeBlocks++;
                    else if (frags > 0)
                        bitmapFreeFragRem += frags;
                }

                // Read CG header summary
                long cgStartByte = cgBase * sb.FSize;
                long cgHdr = cgStartByte + (long)sb.CblkNo * sb.FSize;
                fs.Position = cgHdr + 0x1C;
                int csNbfree = reader.ReadInt32();
                fs.Position = cgHdr + 0x24;
                int csNffree = reader.ReadInt32();

                Assert.True(csNbfree == bitmapFreeBlocks,
                    $"CG {cg}: cs_nbfree={csNbfree} but fsck expects {bitmapFreeBlocks}");
                Assert.True(csNffree == bitmapFreeFragRem,
                    $"CG {cg}: cs_nffree={csNffree} but fsck expects {bitmapFreeFragRem}");

                sumFreeBlocks += bitmapFreeBlocks;
                sumFreeFrags += bitmapFreeFragRem;
            }

            // Superblock totals should match
            Assert.Equal(sb.FreeBlocks, sumFreeBlocks);
            Assert.Equal(sb.FreeFragments, sumFreeFrags);
        }

        /// <summary>
        /// Verify UFS2 recovery information block is written before the superblock.
        /// Per FreeBSD sbin/newfs/mkfs.c: struct fsrecovery at end of sector before SBLOCK_UFS2.
        /// </summary>
        [Fact]
        public void RecoveryBlock_ContainsCorrectFields_UFS2()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // Read superblock for reference
            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // Recovery block is at end of sector before SBLOCK_UFS2
            int sectorSize = Ufs2Constants.DefaultSectorSize;
            int recoverySize = 20; // 5 × int32
            long recoveryOffset = Ufs2Constants.SuperblockOffset64K - sectorSize + (sectorSize - recoverySize);

            fs.Position = recoveryOffset;
            int fsrMagic = reader.ReadInt32();
            int fsrFpg = reader.ReadInt32();
            int fsrFsbtodb = reader.ReadInt32();
            int fsrSblkno = reader.ReadInt32();
            int fsrNcg = reader.ReadInt32();

            Assert.Equal(Ufs2Constants.Ufs2Magic, fsrMagic);
            Assert.Equal(sb.CylGroupSize, fsrFpg);
            Assert.Equal(sb.SuperblockLocation, fsrSblkno);
            Assert.Equal(sb.NumCylGroups, fsrNcg);
            // fsbtodb = log2(fsize/sectorsize); max reasonable value for supported configs
            Assert.True(fsrFsbtodb >= 0 && fsrFsbtodb < 20,
                $"fsbtodb ({fsrFsbtodb}) should be a small non-negative integer (log2(fsize/sectorsize))");
        }

        /// <summary>
        /// Verify that UFS1 images do NOT have a recovery block (UFS2 only feature).
        /// </summary>
        [Fact]
        public void RecoveryBlock_NotWritten_UFS1()
        {
            var creator = new Ufs2ImageCreator { FilesystemFormat = 1 };
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // Recovery block position for UFS2 - should be zeros for UFS1
            int sectorSize = Ufs2Constants.DefaultSectorSize;
            int recoverySize = 20;
            long recoveryOffset = Ufs2Constants.SuperblockOffset64K - sectorSize + (sectorSize - recoverySize);

            fs.Position = recoveryOffset;
            int fsrMagic = reader.ReadInt32();

            // UFS1 should NOT have UFS2 magic in recovery block
            Assert.NotEqual(Ufs2Constants.Ufs2Magic, fsrMagic);
        }

        /// <summary>
        /// Verify that inode generation numbers (di_gen) are set for initialized inodes.
        /// Per FreeBSD mkfs.c: first cg_initediblk inodes get random di_gen values.
        /// </summary>
        [Fact]
        public void InodeGeneration_SetForInitializedInodes()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            int inodeSize = Ufs2Constants.Ufs2InodeSize;
            int inodesPerBlock = sb.BSize / inodeSize;
            int initediblk = Math.Min(sb.InodesPerGroup, 2 * inodesPerBlock);

            // Read inode table of CG 1 (CG 0 has root inode which has its own gen)
            long cg1Start = (long)sb.CylGroupSize * sb.FSize;
            long cg1InodeTable = cg1Start + (long)sb.IblkNo * sb.FSize;

            int nonZeroGens = 0;
            for (int i = 0; i < Math.Min(initediblk, 10); i++)
            {
                fs.Position = cg1InodeTable + (long)i * inodeSize;
                var inode = Ufs2Inode.ReadFrom(reader);
                if (inode.Generation != 0)
                    nonZeroGens++;
            }

            // Most initialized inodes should have non-zero generation numbers
            Assert.True(nonZeroGens > 0, "Initialized inodes should have non-zero di_gen values");
        }

        /// <summary>
        /// Verify that cg_initediblk is set to MIN(ipg, 2*INOPB) for UFS2.
        /// Per FreeBSD mkfs.c initcg().
        /// </summary>
        [Fact]
        public void CylinderGroup_InitedIblk_MatchesFreeBSD_UFS2()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            int inodeSize = Ufs2Constants.Ufs2InodeSize;
            int inodesPerBlock = sb.BSize / inodeSize;
            int expectedInitediblk = Math.Min(sb.InodesPerGroup, 2 * inodesPerBlock);

            // Read CG 0 header
            long cgHeaderOffset = (long)sb.CblkNo * sb.FSize;
            fs.Position = cgHeaderOffset;

            // Skip to cg_initediblk at offset 0x78
            byte[] cgRaw = reader.ReadBytes(sb.CgSize);
            int initediblk = BitConverter.ToInt32(cgRaw, 0x78);

            Assert.Equal(expectedInitediblk, initediblk);
        }

        /// <summary>
        /// Verify that fs_providersize is set to the total filesystem size in fragments.
        /// Per FreeBSD newfs: fs_providersize = dbtofsb(mediasize / sectorsize).
        /// </summary>
        [Fact]
        public void Superblock_ProviderSizeIsSet()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            Assert.Equal(sb.TotalBlocks, sb.ProviderSize);
        }

        /// <summary>
        /// Verify that fs_metaspace is set (non-negative) for UFS2.
        /// Per FreeBSD newfs: blknum(fpg * minfree / 200).
        /// </summary>
        [Fact]
        public void Superblock_MetaSpaceIsSet()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            Assert.True(sb.MetaSpace >= 0, "fs_metaspace should be non-negative");
            // With default minfree=8%, metaspace should be positive
            Assert.True(sb.MetaSpace > 0, "fs_metaspace should be positive with default minfree=8%");
        }

        /// <summary>
        /// Verify that all DIRBLKSIZ chunks in the root directory block have valid entries.
        /// Per FreeBSD fsck_ufs: every DIRBLKSIZ chunk must contain valid directory entries
        /// with d_reclen > 0 to prevent DIRECTORY CORRUPTED errors.
        /// </summary>
        [Fact]
        public void RootDirectory_AllDirBlkSizChunksHaveValidEntries()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // Read root inode
            long inodeTableOffset = (long)sb.IblkNo * sb.FSize;
            fs.Position = inodeTableOffset + (long)Ufs2Constants.RootInode * Ufs2Constants.Ufs2InodeSize;
            var rootInode = Ufs2Inode.ReadFrom(reader);

            Assert.True(rootInode.Size > 0, "Root directory should have positive size");
            Assert.True(rootInode.DirectBlocks[0] != 0, "Root directory should have a data block");

            // Read the root directory data block
            long rootBlockOffset = rootInode.DirectBlocks[0] * sb.FSize;
            fs.Position = rootBlockOffset;
            byte[] rootData = reader.ReadBytes((int)rootInode.Size);

            // Check every DIRBLKSIZ (512-byte) chunk has valid d_reclen > 0
            int dirBlkSiz = Ufs2Constants.DirBlockSize;
            // d_reclen is at byte offset 4 within each directory entry (after 4-byte d_ino)
            int reclenFieldOffset = 4; // sizeof(d_ino)
            for (int chunk = 0; chunk < rootData.Length / dirBlkSiz; chunk++)
            {
                int chunkOffset = chunk * dirBlkSiz;
                ushort reclen = BitConverter.ToUInt16(rootData, chunkOffset + reclenFieldOffset);
                Assert.True(reclen > 0,
                    $"DIRBLKSIZ chunk {chunk} at offset {chunkOffset} has d_reclen=0 " +
                    "(would cause DIRECTORY CORRUPTED in fsck)");
                Assert.True(reclen <= dirBlkSiz,
                    $"DIRBLKSIZ chunk {chunk} has d_reclen={reclen} exceeding DIRBLKSIZ={dirBlkSiz}");
            }
        }
    }
}
