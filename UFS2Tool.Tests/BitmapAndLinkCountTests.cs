// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    public class BitmapAndLinkCountTests : IDisposable
    {
        private readonly string _testDir;
        private readonly string _imagePath;

        public BitmapAndLinkCountTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"ufs2bm_{Guid.NewGuid():N}");
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2bm_{Guid.NewGuid():N}.img");
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath)) File.Delete(_imagePath);
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true);
        }

        /// <summary>
        /// Verify that ALL directory inodes have nlink matching the actual 
        /// reference count from scanning directory entries on disk.
        /// This is what fsck_ufs Phase 4 checks.
        /// </summary>
        [Fact]
        public void LinkCounts_MatchActualReferences_LargeTree()
        {
            // Create a large directory tree
            string d1 = Path.Combine(_testDir, "dir_large");
            Directory.CreateDirectory(d1);
            for (int i = 0; i < 15; i++)
            {
                string sub = Path.Combine(d1, $"sub_{i:D3}");
                Directory.CreateDirectory(sub);
                for (int j = 0; j < 10; j++)
                    File.WriteAllBytes(Path.Combine(sub, $"file_{j:D3}.bin"), new byte[500 + j]);
            }
            for (int j = 0; j < 50; j++)
                File.WriteAllText(Path.Combine(d1, $"direct_{j:D3}.txt"), $"content {j}");
            for (int i = 0; i < 5; i++)
            {
                string sub = Path.Combine(_testDir, $"extra_{i}");
                Directory.CreateDirectory(sub);
                for (int j = 0; j < 10; j++)
                    File.WriteAllText(Path.Combine(sub, $"f_{j}.txt"), $"data {j}");
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);

            var refCount = new Dictionary<uint, int>();
            void CountRefs(uint dirInode)
            {
                var entries = image.ListDirectory(dirInode);
                foreach (var e in entries)
                {
                    if (!refCount.ContainsKey(e.Inode)) refCount[e.Inode] = 0;
                    refCount[e.Inode]++;
                    if (e.FileType == Ufs2Constants.DtDir && e.Name != "." && e.Name != "..")
                        CountRefs(e.Inode);
                }
            }
            CountRefs(Ufs2Constants.RootInode);

            foreach (var (ino, actualCount) in refCount)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode != 0)
                {
                    Assert.True(inode.NLink == actualCount,
                        $"Inode {ino}: stored NLink={inode.NLink} but actual refs={actualCount} " +
                        $"(isDir={inode.IsDirectory})");
                }
            }
        }

        /// <summary>
        /// Verify that the fragment bitmap accurately reflects which fragments are used/free,
        /// and that CG summary counts match the bitmap.
        /// This is what fsck_ufs Phase 5 checks.
        /// </summary>
        [Fact]
        public void FragmentBitmap_MatchesCgSummary_AfterPopulate()
        {
            // Create enough files to potentially span CG boundaries
            for (int i = 0; i < 20; i++)
            {
                string sub = Path.Combine(_testDir, $"dir_{i:D3}");
                Directory.CreateDirectory(sub);
                for (int j = 0; j < 5; j++)
                    File.WriteAllBytes(Path.Combine(sub, $"file_{j}.bin"), new byte[10000]);
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset;
            var sb = Ufs2Superblock.ReadFrom(reader);

            long sbTotalFreeBlocks = 0;
            long sbTotalFreeFrags = 0;

            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStart = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHdr = cgStart + (long)sb.CblkNo * sb.FSize;

                // Read CG summary
                fs.Position = cgHdr + 0x18;
                int csNdir = reader.ReadInt32();
                int csNbfree = reader.ReadInt32();
                int csNifree = reader.ReadInt32();
                int csNffree = reader.ReadInt32();

                // Read bitmap offset
                fs.Position = cgHdr + 0x60;
                int freeoff = reader.ReadInt32();

                int usableFrags = sb.CylGroupSize;
                if (cg == sb.NumCylGroups - 1)
                    usableFrags = (int)(sb.TotalBlocks - (long)cg * sb.CylGroupSize);

                // Read fragment bitmap
                fs.Position = cgHdr + freeoff;
                int bitmapBytes = (sb.CylGroupSize + 7) / 8;
                byte[] bitmap = reader.ReadBytes(bitmapBytes);

                // Count free blocks and free fragment remainder from bitmap
                int fpb = sb.FragsPerBlock;
                int bitmapFreeBlocks = 0;

                // Count free fragments from bitmap (all fragments marked as free)
                int totalBitmapFreeFrags = 0;
                for (int f = 0; f < usableFrags; f++)
                {
                    if ((bitmap[f / 8] & (1 << (f % 8))) != 0)
                        totalBitmapFreeFrags++;
                }

                // Count complete free blocks (all frags in block are free)
                for (int blk = 0; blk < usableFrags / fpb; blk++)
                {
                    bool allFree = true;
                    for (int ff = 0; ff < fpb; ff++)
                    {
                        int f = blk * fpb + ff;
                        if ((bitmap[f / 8] & (1 << (f % 8))) == 0)
                        { allFree = false; break; }
                    }
                    if (allFree) bitmapFreeBlocks++;
                }

                // Free fragment count = total free frags not in complete blocks
                int bitmapFreeFragRem = totalBitmapFreeFrags - bitmapFreeBlocks * fpb;

                Assert.True(csNbfree == bitmapFreeBlocks,
                    $"CG {cg}: cs_nbfree={csNbfree} but bitmap shows {bitmapFreeBlocks} free blocks");
                Assert.True(csNffree == bitmapFreeFragRem,
                    $"CG {cg}: cs_nffree={csNffree} but bitmap shows {bitmapFreeFragRem} free frags");

                sbTotalFreeBlocks += csNbfree;
                sbTotalFreeFrags += csNffree;
            }

            // Superblock totals should match sum of CG summaries
            Assert.Equal(sb.FreeBlocks, sbTotalFreeBlocks);
            Assert.Equal(sb.FreeFragments, sbTotalFreeFrags);
        }

        /// <summary>
        /// Verify that di_blocks matches actual allocated blocks for all inodes after
        /// populating with many files. This catches the INCORRECT BLOCK COUNT error from fsck.
        /// </summary>
        [Fact]
        public void BlockCount_MatchesAllocatedBlocks_LargeTree()
        {
            // Create files with non-block-aligned sizes (triggers rounding bug)
            // Include files both smaller and larger than blockSize (32768)
            // to exercise both fragment-level and block-level di_blocks paths
            for (int i = 0; i < 10; i++)
            {
                string sub = Path.Combine(_testDir, $"dir_{i:D3}");
                Directory.CreateDirectory(sub);
                for (int j = 0; j < 5; j++)
                {
                    // Non-block-aligned sizes; j>=3 produces files > blockSize
                    int fileSize = 1000 + j * 10000;
                    File.WriteAllBytes(Path.Combine(sub, $"file_{j}.bin"), new byte[fileSize]);
                }
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            int fragsPerBlock = sb.BSize / sb.FSize;

            // Check every inode with data
            long totalInodes = (long)sb.InodesPerGroup * sb.NumCylGroups;
            for (uint ino = Ufs2Constants.RootInode; ino < totalInodes; ino++)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode == 0) continue; // unused inode

                // Count actual allocated blocks from direct and indirect pointers
                int allocatedBlocks = 0;
                for (int i = 0; i < Ufs2Constants.NDirect; i++)
                    if (inode.DirectBlocks[i] != 0) allocatedBlocks++;
                // Metadata blocks (indirect pointers themselves)
                for (int i = 0; i < Ufs2Constants.NIndirect; i++)
                    if (inode.IndirectBlocks[i] != 0) allocatedBlocks++;

                // For files using only direct blocks, verify di_blocks matches full-block counting.
                // Since AllocateDataBlock always allocates full blocks, di_blocks counts all data blocks
                // For files without indirect blocks, count data using fragment-level tail handling:
                // - Full blocks: size / blockSize
                // - Tail fragments: ceil((size % blockSize) / fragSize)
                if (inode.IndirectBlocks[0] == 0 && inode.IndirectBlocks[1] == 0 && inode.IndirectBlocks[2] == 0)
                {
                    long expectedFrags;
                    if (inode.Size > 0)
                    {
                        // Count full data blocks at full-block granularity
                        long fullBlocks = inode.Size / sb.BSize;
                        expectedFrags = fullBlocks * fragsPerBlock;

                        // Count the last (partial) block at fragment granularity
                        long tailBytes = inode.Size % sb.BSize;
                        if (tailBytes > 0)
                        {
                            long tailFrags = (tailBytes + sb.FSize - 1) / sb.FSize;
                            expectedFrags += tailFrags;
                        }
                    }
                    else
                    {
                        expectedFrags = 0;
                    }
                    long expectedBlocks512 = expectedFrags * (sb.FSize / 512);
                    Assert.True(inode.Blocks == expectedBlocks512,
                        $"Inode {ino}: di_blocks={inode.Blocks} but expected {expectedBlocks512} " +
                        $"(size={inode.Size}, allocatedBlocks={allocatedBlocks}, isDir={inode.IsDirectory})");
                }
            }
        }

        /// <summary>
        /// Verify that ALL block pointers reference valid fragment numbers within the filesystem.
        /// This is what fsck Phase 1 checks for BAD blocks.
        /// </summary>
        [Fact]
        public void AllBlockPointers_AreWithinFilesystem_LargeTree()
        {
            // Create a large enough tree to span multiple CGs
            for (int i = 0; i < 15; i++)
            {
                string sub = Path.Combine(_testDir, $"dir_{i:D3}");
                Directory.CreateDirectory(sub);
                for (int j = 0; j < 10; j++)
                    File.WriteAllBytes(Path.Combine(sub, $"file_{j:D3}.bin"), new byte[500 + j]);
            }
            for (int i = 0; i < 5; i++)
            {
                string sub = Path.Combine(_testDir, $"extra_{i}");
                Directory.CreateDirectory(sub);
                for (int j = 0; j < 10; j++)
                    File.WriteAllText(Path.Combine(sub, $"f_{j}.txt"), $"data {j}");
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            long totalFrags = sb.TotalBlocks;
            int fragsPerBlock = sb.BSize / sb.FSize;

            long totalInodes = (long)sb.InodesPerGroup * sb.NumCylGroups;
            for (uint ino = Ufs2Constants.RootInode; ino < totalInodes; ino++)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode == 0) continue;

                for (int i = 0; i < Ufs2Constants.NDirect; i++)
                {
                    long blk = inode.DirectBlocks[i];
                    if (blk != 0)
                    {
                        Assert.True(blk >= 0 && blk + fragsPerBlock <= totalFrags,
                            $"Inode {ino} DirectBlock[{i}]={blk} is out of range [0, {totalFrags})");
                    }
                }
                for (int i = 0; i < Ufs2Constants.NIndirect; i++)
                {
                    long blk = inode.IndirectBlocks[i];
                    if (blk != 0)
                    {
                        Assert.True(blk >= 0 && blk + fragsPerBlock <= totalFrags,
                            $"Inode {ino} IndirectBlock[{i}]={blk} is out of range [0, {totalFrags})");
                    }
                }
            }
        }

        /// <summary>
        /// Simulate a game directory structure (like Minecraft) with many files per
        /// directory. Validates that di_blocks, bitmaps, superblock counts, and
        /// directory entries are all consistent — covering all fsck_ufs checks.
        /// </summary>
        [Fact]
        public void LargeGameDirectory_AllFsckChecksPass()
        {
            // Create a structure similar to Minecraft behavior_packs with many recipes
            string packs = Path.Combine(_testDir, "data", "behavior_packs", "chemistry");
            string recipes = Path.Combine(packs, "recipes");
            Directory.CreateDirectory(recipes);

            // Many files in one directory triggers multi-block directory entries
            for (int i = 0; i < 200; i++)
                File.WriteAllText(Path.Combine(recipes, $"recipe_{i:D4}.json"),
                    $"{{\"type\":\"crafting_shaped\",\"id\":{i}}}");

            // Additional directories with files
            for (int d = 0; d < 10; d++)
            {
                string sub = Path.Combine(packs, $"textures_{d}");
                Directory.CreateDirectory(sub);
                for (int f = 0; f < 20; f++)
                    File.WriteAllBytes(Path.Combine(sub, $"tex_{f:D3}.png"), new byte[1024 + f * 100]);
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            int fragsPerBlock = sb.BSize / sb.FSize;

            // Check 1: All inodes have correct di_blocks (INCORRECT BLOCK COUNT)
            long totalInodes = (long)sb.InodesPerGroup * sb.NumCylGroups;
            for (uint ino = Ufs2Constants.RootInode; ino < totalInodes; ino++)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode == 0) continue;

                if (inode.IndirectBlocks[0] == 0 && inode.IndirectBlocks[1] == 0 && inode.IndirectBlocks[2] == 0)
                {
                    // Count full data blocks at full-block granularity
                    long fullBlocks = (inode.Size > 0) ? inode.Size / sb.BSize : 0;
                    long expectedFrags = fullBlocks * fragsPerBlock;

                    // Count the last (partial) block at fragment granularity
                    long tailBytes = (inode.Size > 0) ? inode.Size % sb.BSize : 0;
                    if (tailBytes > 0)
                    {
                        long tailFrags = (tailBytes + sb.FSize - 1) / sb.FSize;
                        expectedFrags += tailFrags;
                    }

                    long expectedBlocks512 = expectedFrags * (sb.FSize / 512);
                    Assert.True(inode.Blocks == expectedBlocks512,
                        $"Inode {ino}: di_blocks={inode.Blocks} expected {expectedBlocks512} (size={inode.Size})");
                }
            }

            // Check 2: All directory inodes have correct nlink (UNREF FILE)
            var refCount = new Dictionary<uint, int>();
            void CountRefs(uint dirInode)
            {
                var entries = image.ListDirectory(dirInode);
                foreach (var e in entries)
                {
                    if (!refCount.ContainsKey(e.Inode)) refCount[e.Inode] = 0;
                    refCount[e.Inode]++;
                    if (e.FileType == Ufs2Constants.DtDir && e.Name != "." && e.Name != "..")
                        CountRefs(e.Inode);
                }
            }
            CountRefs(Ufs2Constants.RootInode);

            foreach (var (ino, count) in refCount)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode != 0)
                    Assert.True(inode.NLink == count,
                        $"Inode {ino}: NLink={inode.NLink} but refs={count}");
            }

            // Check 3: CG bitmap matches summary counts (FREE BLK COUNT, BLK MISSING IN BIT MAPS)
            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset;
            var sbRaw = Ufs2Superblock.ReadFrom(reader);

            long sumFreeBlocks = 0, sumFreeFrags = 0;
            for (int cg = 0; cg < sbRaw.NumCylGroups; cg++)
            {
                long cgStart = (long)cg * sbRaw.CylGroupSize * sbRaw.FSize;
                long cgHdr = cgStart + (long)sbRaw.CblkNo * sbRaw.FSize;

                fs.Position = cgHdr + 0x18;
                int csNdir = reader.ReadInt32();
                int csNbfree = reader.ReadInt32();
                int csNifree = reader.ReadInt32();
                int csNffree = reader.ReadInt32();

                fs.Position = cgHdr + 0x60;
                int freeoff = reader.ReadInt32();

                int usableFrags = sbRaw.CylGroupSize;
                if (cg == sbRaw.NumCylGroups - 1)
                    usableFrags = (int)(sbRaw.TotalBlocks - (long)cg * sbRaw.CylGroupSize);

                fs.Position = cgHdr + freeoff;
                byte[] bitmap = reader.ReadBytes((sbRaw.CylGroupSize + 7) / 8);

                int bitmapFreeBlocks = 0;
                int totalBitmapFreeFrags = 0;
                for (int f = 0; f < usableFrags; f++)
                    if ((bitmap[f / 8] & (1 << (f % 8))) != 0)
                        totalBitmapFreeFrags++;

                for (int blk = 0; blk < usableFrags / fragsPerBlock; blk++)
                {
                    bool allFree = true;
                    for (int ff = 0; ff < fragsPerBlock; ff++)
                    {
                        int f = blk * fragsPerBlock + ff;
                        if ((bitmap[f / 8] & (1 << (f % 8))) == 0) { allFree = false; break; }
                    }
                    if (allFree) bitmapFreeBlocks++;
                }

                int bitmapFreeFragRem = totalBitmapFreeFrags - bitmapFreeBlocks * fragsPerBlock;

                Assert.True(csNbfree == bitmapFreeBlocks,
                    $"CG {cg}: cs_nbfree={csNbfree} bitmap={bitmapFreeBlocks}");
                Assert.True(csNffree == bitmapFreeFragRem,
                    $"CG {cg}: cs_nffree={csNffree} bitmap={bitmapFreeFragRem}");

                sumFreeBlocks += csNbfree;
                sumFreeFrags += csNffree;
            }

            // Check 4: Superblock totals match CG sums (FREE BLK COUNT WRONG IN SUPERBLK)
            Assert.Equal(sbRaw.FreeBlocks, sumFreeBlocks);
            Assert.Equal(sbRaw.FreeFragments, sumFreeFrags);
        }

        /// <summary>
        /// Verify that directories with many entries (requiring multi-block layout)
        /// don't drop entries due to block count underestimation.
        /// The packing algorithm expands the last entry in each block to fill it,
        /// so the sum of minimum record lengths underestimates the actual space needed.
        /// </summary>
        [Fact]
        public void LargeDirectory_NoEntriesDropped()
        {
            // Create a directory with enough files to trigger multi-block directory
            // entries where the packing waste could cause entry loss
            string bigDir = Path.Combine(_testDir, "bigdir");
            Directory.CreateDirectory(bigDir);

            // Use name lengths that maximize block boundary waste
            int fileCount = 2000;
            for (int i = 0; i < fileCount; i++)
                File.WriteAllBytes(Path.Combine(bigDir, $"file_{i:D6}.dat"), new byte[100]);

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);

            // Walk the root to find bigdir's inode
            var rootEntries = image.ListDirectory(Ufs2Constants.RootInode);
            var bigDirEntry = rootEntries.Find(e => e.Name == "bigdir");
            Assert.NotNull(bigDirEntry);

            // List bigdir's entries and verify all files are present
            var bigDirEntries = image.ListDirectory(bigDirEntry.Inode);
            var fileEntries = bigDirEntries.Where(e => e.Name != "." && e.Name != "..").ToList();
            Assert.Equal(fileCount, fileEntries.Count);

            // Verify every file name is present
            var expectedNames = new HashSet<string>();
            for (int i = 0; i < fileCount; i++)
                expectedNames.Add($"file_{i:D6}.dat");

            foreach (var entry in fileEntries)
                Assert.True(expectedNames.Contains(entry.Name),
                    $"Unexpected entry: {entry.Name}");
        }

        /// <summary>
        /// Verify that di_blocks is correct for files large enough to require
        /// indirect block pointers. This catches the INCORRECT BLOCK COUNT
        /// error from fsck_ufs when metadata blocks aren't properly counted.
        /// </summary>
        [Fact]
        public void BlockCount_CorrectForFilesWithIndirectBlocks()
        {
            // Create files that require indirect blocks (> 12 * blockSize = 393216 bytes)
            int[] fileSizes = new int[]
            {
                400000,    // Just over 12 blocks - needs single indirect
                500000,    // Solidly in indirect range
                700000,    // More indirect blocks needed
                393216,    // Exactly 12 blocks - no indirect
                393217,    // 12 blocks + 1 byte - needs indirect
                32768,     // Exactly 1 block
                1,         // Minimal file
                0,         // Empty file
            };

            var rng = new Random(42);
            for (int i = 0; i < fileSizes.Length; i++)
            {
                byte[] data = new byte[fileSizes[i]];
                rng.NextBytes(data);
                File.WriteAllBytes(Path.Combine(_testDir, $"large_{i}.bin"), data);
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            int fragsPerBlock = sb.BSize / sb.FSize;

            long totalInodes = (long)sb.InodesPerGroup * sb.NumCylGroups;
            for (uint ino = Ufs2Constants.RootInode; ino < totalInodes; ino++)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode == 0) continue;

                // Calculate data fragments per FreeBSD fsck_ufs expectations:
                // - Files using indirect blocks (>12 data blocks): ALL data blocks are full blocks
                //   (fsck_ufs iblock() counts all indirect-pointed data blocks with fs_frag)
                // - Files using only direct blocks (≤12 data blocks): last block at fragment granularity
                long blocksNeeded = (inode.Size > 0) ? (inode.Size + sb.BSize - 1) / sb.BSize : 0;
                long dataFrags;
                if (blocksNeeded > Ufs2Constants.NDirect)
                {
                    // All data blocks are full blocks when using indirect pointers
                    dataFrags = blocksNeeded * fragsPerBlock;
                }
                else
                {
                    dataFrags = 0;
                    if (inode.Size > 0)
                    {
                        long fullBlocks = inode.Size / sb.BSize;
                        dataFrags = fullBlocks * fragsPerBlock;

                        long tailBytes = inode.Size % sb.BSize;
                        if (tailBytes > 0)
                        {
                            long tailFrags = (tailBytes + sb.FSize - 1) / sb.FSize;
                            dataFrags += tailFrags;
                        }
                    }
                }

                // Count metadata blocks from indirect pointers
                int metadataBlocks = 0;
                if (inode.IndirectBlocks[0] != 0) metadataBlocks++; // single indirect block
                if (inode.IndirectBlocks[1] != 0)
                {
                    metadataBlocks++; // double indirect block itself
                    // Count single-indirect sub-blocks referenced by double indirect
                    // For our test files, we won't need double indirect
                }
                if (inode.IndirectBlocks[2] != 0)
                {
                    metadataBlocks++; // triple indirect block itself
                }

                long metadataFrags = (long)metadataBlocks * fragsPerBlock;

                // For files with single indirect only, also count sub-indirect blocks
                // by computing expected metadata from the number of data blocks
                if (inode.IndirectBlocks[0] != 0 && inode.IndirectBlocks[1] == 0)
                {
                    // Just one single indirect block
                    metadataFrags = fragsPerBlock;
                }

                long expectedBlocks512 = (dataFrags + metadataFrags) * (sb.FSize / 512);
                Assert.True(inode.Blocks == expectedBlocks512,
                    $"Inode {ino}: di_blocks={inode.Blocks} expected {expectedBlocks512} " +
                    $"(size={inode.Size}, metadataBlocks={metadataBlocks}, " +
                    $"hasIndirect={inode.IndirectBlocks[0] != 0}, isDir={inode.IsDirectory})");
            }
        }

        /// <summary>
        /// Verify di_blocks matches FreeBSD's fsck_ufs ckinode/iblock expectations:
        /// - Files with ≤12 data blocks: last block counted at fragment granularity
        /// - Files with >12 data blocks: ALL data blocks counted as full blocks
        /// This is the root cause of the INCORRECT BLOCK COUNT errors reported by
        /// fsck_ufs when tail fragments were incorrectly counted for indirect files.
        /// </summary>
        [Fact]
        public void BlockCount_MatchesFsckExpectationsForIndirectFiles()
        {
            // Create files with sizes carefully chosen to test edge cases:
            // - Exactly at the direct/indirect boundary (12 blocks)
            // - Just over the boundary with various tail sizes
            int blockSize = 32768;  // default
            int fragSize = 4096;    // default
            int fragsPerBlock = blockSize / fragSize;
            int sectorsPerFrag = fragSize / 512;

            // File sizes that exercise the boundary between direct-only and indirect
            var fileSizes = new long[]
            {
                (long)12 * blockSize,               // Exactly 12 blocks (direct only, no tail)
                (long)12 * blockSize + 1,            // 13 blocks, 1-byte tail → indirect needed
                (long)12 * blockSize + fragSize,     // 13 blocks, 1-frag tail → indirect needed
                (long)12 * blockSize + fragSize * 3, // 13 blocks, 3-frag tail → indirect needed
                (long)13 * blockSize,                // Exactly 13 blocks (indirect, no tail)
                (long)13 * blockSize - 1,            // 13 blocks with tail = blockSize-1 → indirect
                (long)20 * blockSize + fragSize * 5, // 21 blocks with 5-frag tail → indirect
                (long)11 * blockSize + fragSize * 2, // 12 blocks with 2-frag tail → direct only
                (long)1 * blockSize + fragSize,      // 2 blocks with 1-frag tail → direct only
            };

            var rng = new Random(123);
            for (int i = 0; i < fileSizes.Length; i++)
            {
                byte[] data = new byte[fileSizes[i]];
                rng.NextBytes(data);
                File.WriteAllBytes(Path.Combine(_testDir, $"edge_{i}.bin"), data);
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            long totalInodes = (long)sb.InodesPerGroup * sb.NumCylGroups;
            for (uint ino = Ufs2Constants.RootInode; ino < totalInodes; ino++)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode == 0 || inode.Size == 0) continue;

                long fileBlocksNeeded = (inode.Size + sb.BSize - 1) / sb.BSize;
                int fpb = sb.BSize / sb.FSize;

                // Compute expected di_blocks per FreeBSD fsck_ufs algorithm
                long expectedDataFrags;
                if (fileBlocksNeeded > Ufs2Constants.NDirect)
                {
                    // Indirect blocks: ALL data blocks are full blocks
                    expectedDataFrags = fileBlocksNeeded * fpb;
                }
                else
                {
                    // Direct only: last block at fragment granularity
                    long fullBlocks = inode.Size / sb.BSize;
                    expectedDataFrags = fullBlocks * fpb;
                    long tail = inode.Size % sb.BSize;
                    if (tail > 0)
                        expectedDataFrags += (tail + sb.FSize - 1) / sb.FSize;
                }

                // Count metadata blocks from indirect pointers
                long metadataFrags = 0;
                if (inode.IndirectBlocks[0] != 0) metadataFrags += fpb;
                // (double/triple indirect not needed for these test sizes)

                long expectedBlocks512 = (expectedDataFrags + metadataFrags) * (sb.FSize / 512);

                Assert.True(inode.Blocks == expectedBlocks512,
                    $"Inode {ino}: di_blocks={inode.Blocks} expected {expectedBlocks512} " +
                    $"(size={inode.Size}, blocksNeeded={fileBlocksNeeded}, usesIndirect={fileBlocksNeeded > Ufs2Constants.NDirect})");
            }
        }

        /// <summary>
        /// Verify that all allocated inodes are reachable from the directory tree.
        /// This catches the UNREF FILE error from fsck when directory entries are
        /// lost due to insufficient directory block allocation.
        /// </summary>
        [Fact]
        public void AllAllocatedInodes_AreReferencedByDirectories()
        {
            // Create a substantial tree that exercises multi-block directories
            for (int d = 0; d < 5; d++)
            {
                string sub = Path.Combine(_testDir, $"subdir_{d}");
                Directory.CreateDirectory(sub);
                for (int f = 0; f < 100; f++)
                    File.WriteAllBytes(Path.Combine(sub, $"file_{f:D4}.bin"), new byte[200 + f]);
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            // Collect all inodes referenced from directory tree
            var referencedInodes = new HashSet<uint>();
            void WalkDirs(uint dirIno)
            {
                var entries = image.ListDirectory(dirIno);
                foreach (var e in entries)
                {
                    referencedInodes.Add(e.Inode);
                    if (e.FileType == Ufs2Constants.DtDir && e.Name != "." && e.Name != "..")
                        WalkDirs(e.Inode);
                }
            }
            WalkDirs(Ufs2Constants.RootInode);

            // Check that every allocated inode is referenced
            long totalInodes = (long)sb.InodesPerGroup * sb.NumCylGroups;
            for (uint ino = Ufs2Constants.RootInode; ino < totalInodes; ino++)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode == 0) continue; // unused

                Assert.True(referencedInodes.Contains(ino),
                    $"Inode {ino} is allocated (mode=0x{inode.Mode:X4}, size={inode.Size}, " +
                    $"isDir={inode.IsDirectory}) but NOT referenced by any directory entry (UNREF)");
            }
        }

        /// <summary>
        /// Verify that directories requiring more than 12 blocks (NDirect) correctly
        /// use indirect block pointers. This catches DIRECTORY CORRUPTED and UNREF FILE
        /// errors from fsck when directory entries spill beyond 12 direct blocks.
        /// </summary>
        [Fact]
        public void LargeDirectory_IndirectBlocks_AllEntriesAccessible()
        {
            // Create a directory with enough files to require more than 12 blocks.
            // With 44-byte entries (29-char names) and blockSize=32768, each block holds
            // ~744 entries, so 13 blocks need ~9672 entries. Use 10000 to be safe.
            string bigDir = Path.Combine(_testDir, "hugedir");
            Directory.CreateDirectory(bigDir);

            int fileCount = 10000;
            for (int i = 0; i < fileCount; i++)
                File.WriteAllBytes(Path.Combine(bigDir, $"worldchunk_region_{i:D06}.dat"), new byte[10]);

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            int fragsPerBlock = sb.BSize / sb.FSize;

            // Find the hugedir inode
            var rootEntries = image.ListDirectory(Ufs2Constants.RootInode);
            var hugeDirEntry = rootEntries.Find(e => e.Name == "hugedir");
            Assert.NotNull(hugeDirEntry);

            // The directory inode should have an indirect block pointer
            var dirInode = image.ReadInode(hugeDirEntry.Inode);
            Assert.True(dirInode.IsDirectory);
            int dirBlocks = (int)(dirInode.Size / sb.BSize);
            Assert.True(dirBlocks > Ufs2Constants.NDirect,
                $"Directory should need >{Ufs2Constants.NDirect} blocks but only needs {dirBlocks}");
            Assert.True(dirInode.IndirectBlocks[0] != 0,
                "Directory with >12 blocks should have indirect block pointer set");

            // Verify di_blocks includes the indirect metadata block
            long expectedDataFrags = dirInode.Size / sb.FSize;
            long expectedMetadataFrags = fragsPerBlock; // 1 indirect block
            long expectedBlocks512 = (expectedDataFrags + expectedMetadataFrags) * (sb.FSize / 512);
            Assert.True(dirInode.Blocks == expectedBlocks512,
                $"di_blocks={dirInode.Blocks} expected {expectedBlocks512} " +
                $"(dirBlocks={dirBlocks}, includes 1 indirect metadata block)");

            // List all entries in the directory — this exercises indirect block reading
            var bigDirEntries = image.ListDirectory(hugeDirEntry.Inode);
            var fileEntries = bigDirEntries.Where(e => e.Name != "." && e.Name != "..").ToList();
            Assert.Equal(fileCount, fileEntries.Count);

            // Verify all file names are present
            var expectedNames = new HashSet<string>();
            for (int i = 0; i < fileCount; i++)
                expectedNames.Add($"worldchunk_region_{i:D06}.dat");
            foreach (var entry in fileEntries)
                Assert.True(expectedNames.Contains(entry.Name),
                    $"Unexpected entry: {entry.Name}");
        }

        /// <summary>
        /// Verify that all fsck checks pass for a filesystem with directories requiring
        /// indirect blocks: di_blocks, nlink, bitmaps, and superblock totals.
        /// </summary>
        [Fact]
        public void LargeDirectory_IndirectBlocks_AllFsckChecksPass()
        {
            // Create a directory large enough to need indirect blocks
            // With 44-byte entries (29-char names), ~744 entries per block, 13 blocks need ~9672
            string bigDir = Path.Combine(_testDir, "chunkdir");
            Directory.CreateDirectory(bigDir);

            int fileCount = 10000;
            for (int i = 0; i < fileCount; i++)
                File.WriteAllBytes(Path.Combine(bigDir, $"worldchunk_region_{i:D06}.dat"), new byte[50]);

            // Also add some regular subdirectories with files
            for (int d = 0; d < 5; d++)
            {
                string sub = Path.Combine(_testDir, $"sub_{d}");
                Directory.CreateDirectory(sub);
                for (int f = 0; f < 20; f++)
                    File.WriteAllBytes(Path.Combine(sub, $"data_{f}.bin"), new byte[200]);
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            int fragsPerBlock = sb.BSize / sb.FSize;

            // Check 1: All inodes have correct di_blocks
            long totalInodes = (long)sb.InodesPerGroup * sb.NumCylGroups;
            for (uint ino = Ufs2Constants.RootInode; ino < totalInodes; ino++)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode == 0) continue;

                // Calculate data fragments - count full blocks and last partial block at fragment granularity
                long fullBlocks = (inode.Size > 0) ? inode.Size / sb.BSize : 0;
                long dataFrags = fullBlocks * fragsPerBlock;

                long tailBytes = (inode.Size > 0) ? inode.Size % sb.BSize : 0;
                if (tailBytes > 0)
                {
                    long tailFrags = (tailBytes + sb.FSize - 1) / sb.FSize;
                    dataFrags += tailFrags;
                }

                int metadataBlocks = 0;
                for (int i = 0; i < Ufs2Constants.NIndirect; i++)
                    if (inode.IndirectBlocks[i] != 0) metadataBlocks++;
                // For single indirect, count the one indirect block
                if (inode.IndirectBlocks[0] != 0 && inode.IndirectBlocks[1] == 0)
                {
                    long metadataFrags = fragsPerBlock;
                    long expectedBlocks512 = (dataFrags + metadataFrags) * (sb.FSize / 512);
                    Assert.True(inode.Blocks == expectedBlocks512,
                        $"Inode {ino}: di_blocks={inode.Blocks} expected {expectedBlocks512} " +
                        $"(size={inode.Size}, isDir={inode.IsDirectory})");
                }
                else if (inode.IndirectBlocks[0] == 0)
                {
                    long expectedBlocks512 = dataFrags * (sb.FSize / 512);
                    Assert.True(inode.Blocks == expectedBlocks512,
                        $"Inode {ino}: di_blocks={inode.Blocks} expected {expectedBlocks512}");
                }
            }

            // Check 2: All allocated inodes are referenced (no UNREF FILE)
            var referencedInodes = new HashSet<uint>();
            void WalkDirs(uint dirIno)
            {
                var entries = image.ListDirectory(dirIno);
                foreach (var e in entries)
                {
                    referencedInodes.Add(e.Inode);
                    if (e.FileType == Ufs2Constants.DtDir && e.Name != "." && e.Name != "..")
                        WalkDirs(e.Inode);
                }
            }
            WalkDirs(Ufs2Constants.RootInode);

            for (uint ino = Ufs2Constants.RootInode; ino < totalInodes; ino++)
            {
                var inode = image.ReadInode(ino);
                if (inode.Mode == 0) continue;
                Assert.True(referencedInodes.Contains(ino),
                    $"Inode {ino} allocated but not referenced (UNREF FILE)");
            }

            // Check 3: CG bitmaps match summary counts
            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            fs.Position = Ufs2Constants.SuperblockOffset;
            var sbRaw = Ufs2Superblock.ReadFrom(reader);

            long sumFreeBlocks = 0, sumFreeFrags = 0;
            for (int cg = 0; cg < sbRaw.NumCylGroups; cg++)
            {
                long cgStart = (long)cg * sbRaw.CylGroupSize * sbRaw.FSize;
                long cgHdr = cgStart + (long)sbRaw.CblkNo * sbRaw.FSize;

                fs.Position = cgHdr + 0x18;
                reader.ReadInt32();                  // cs_ndir (skip)
                int csNbfree = reader.ReadInt32();   // cs_nbfree
                int csNifree = reader.ReadInt32();
                int csNffree = reader.ReadInt32();

                fs.Position = cgHdr + 0x60;
                int freeoff = reader.ReadInt32();

                int usableFrags = sbRaw.CylGroupSize;
                if (cg == sbRaw.NumCylGroups - 1)
                    usableFrags = (int)(sbRaw.TotalBlocks - (long)cg * sbRaw.CylGroupSize);

                fs.Position = cgHdr + freeoff;
                byte[] bitmap = reader.ReadBytes((sbRaw.CylGroupSize + 7) / 8);

                int bitmapFreeBlocks = 0;
                int totalBitmapFreeFrags = 0;
                for (int f = 0; f < usableFrags; f++)
                    if ((bitmap[f / 8] & (1 << (f % 8))) != 0)
                        totalBitmapFreeFrags++;

                for (int blk = 0; blk < usableFrags / fragsPerBlock; blk++)
                {
                    bool allFree = true;
                    for (int ff = 0; ff < fragsPerBlock; ff++)
                    {
                        int f = blk * fragsPerBlock + ff;
                        if ((bitmap[f / 8] & (1 << (f % 8))) == 0) { allFree = false; break; }
                    }
                    if (allFree) bitmapFreeBlocks++;
                }

                int bitmapFreeFragRem = totalBitmapFreeFrags - bitmapFreeBlocks * fragsPerBlock;

                Assert.True(csNbfree == bitmapFreeBlocks,
                    $"CG {cg}: cs_nbfree={csNbfree} bitmap={bitmapFreeBlocks}");
                Assert.True(csNffree == bitmapFreeFragRem,
                    $"CG {cg}: cs_nffree={csNffree} bitmap={bitmapFreeFragRem}");

                sumFreeBlocks += csNbfree;
                sumFreeFrags += csNffree;
            }

            // Check 4: Superblock totals match
            Assert.Equal(sbRaw.FreeBlocks, sumFreeBlocks);
            Assert.Equal(sbRaw.FreeFragments, sumFreeFrags);
        }

        /// <summary>
        /// Verify that CreateImageFromDirectory populates ALL source files and
        /// directories into the resulting image. Walks the source directory tree
        /// and the image directory tree, then asserts they contain the same entries.
        /// This catches the "Some files and folders are not added" issue.
        /// </summary>
        [Fact]
        public void CreateImageFromDirectory_AllSourceEntriesPresent()
        {
            // Build a complex directory tree with files and subdirectories at every level
            var expectedFiles = new List<string>();
            var expectedDirs = new List<string>();

            // Root-level files
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(_testDir, $"root_{i}.txt"), $"root content {i}");
                expectedFiles.Add($"/root_{i}.txt");
            }

            // Multiple directories with files and nested subdirectories
            for (int d = 0; d < 4; d++)
            {
                var subDir = Path.Combine(_testDir, $"dir{d}");
                Directory.CreateDirectory(subDir);
                expectedDirs.Add($"/dir{d}");

                for (int f = 0; f < 3; f++)
                {
                    File.WriteAllText(Path.Combine(subDir, $"file_{f}.txt"), $"content {d}/{f}");
                    expectedFiles.Add($"/dir{d}/file_{f}.txt");
                }

                var nestedDir = Path.Combine(subDir, "nested");
                Directory.CreateDirectory(nestedDir);
                expectedDirs.Add($"/dir{d}/nested");

                File.WriteAllText(Path.Combine(nestedDir, "deep.txt"), $"deep {d}");
                expectedFiles.Add($"/dir{d}/nested/deep.txt");
            }

            // Deep nesting
            string cur = Path.Combine(_testDir, "deep_chain");
            string pathPrefix = "/deep_chain";
            Directory.CreateDirectory(cur);
            expectedDirs.Add(pathPrefix);
            for (int i = 0; i < 4; i++)
            {
                File.WriteAllText(Path.Combine(cur, $"level{i}.txt"), $"level {i}");
                expectedFiles.Add($"{pathPrefix}/level{i}.txt");
                cur = Path.Combine(cur, $"sub{i}");
                pathPrefix += $"/sub{i}";
                Directory.CreateDirectory(cur);
                expectedDirs.Add(pathPrefix);
            }

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);

            var foundFiles = new List<string>();
            var foundDirs = new List<string>();

            void Walk(uint inodeNum, string path)
            {
                var entries = image.ListDirectory(inodeNum);
                foreach (var e in entries)
                {
                    if (e.Name == "." || e.Name == "..") continue;
                    string fullPath = path + "/" + e.Name;
                    if (e.FileType == Ufs2Constants.DtDir)
                    {
                        foundDirs.Add(fullPath);
                        Walk(e.Inode, fullPath);
                    }
                    else if (e.FileType == Ufs2Constants.DtReg)
                    {
                        foundFiles.Add(fullPath);
                    }
                }
            }

            Walk(Ufs2Constants.RootInode, "");

            var missingFiles = expectedFiles.Except(foundFiles).ToList();
            var missingDirs = expectedDirs.Except(foundDirs).ToList();

            Assert.True(missingFiles.Count == 0,
                $"Missing files ({missingFiles.Count}): {string.Join(", ", missingFiles.Take(10))}");
            Assert.True(missingDirs.Count == 0,
                $"Missing directories ({missingDirs.Count}): {string.Join(", ", missingDirs.Take(10))}");
            Assert.Equal(expectedFiles.Count, foundFiles.Count);
            Assert.Equal(expectedDirs.Count, foundDirs.Count);
        }

        /// <summary>
        /// Verify that file content is preserved correctly after population.
        /// Writes files with known content and reads them back from the image.
        /// </summary>
        [Fact]
        public void CreateImageFromDirectory_FileContentPreserved()
        {
            // Create files with distinct content
            for (int i = 0; i < 10; i++)
                File.WriteAllText(Path.Combine(_testDir, $"file_{i}.txt"), $"content_{i}_data");

            var sub = Path.Combine(_testDir, "sub");
            Directory.CreateDirectory(sub);
            byte[] binaryData = new byte[5000];
            new Random(42).NextBytes(binaryData);
            File.WriteAllBytes(Path.Combine(sub, "binary.bin"), binaryData);

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);

            // Verify text file content
            var rootEntries = image.ListRoot();
            foreach (var e in rootEntries)
            {
                if (e.FileType != Ufs2Constants.DtReg) continue;
                if (!e.Name.StartsWith("file_") || !e.Name.EndsWith(".txt")) continue;
                int idx = int.Parse(e.Name.Substring("file_".Length, e.Name.Length - "file_".Length - ".txt".Length));
                byte[] data = image.ReadFileBytes(e.Inode);
                string content = System.Text.Encoding.UTF8.GetString(data);
                Assert.Equal($"content_{idx}_data", content);

                // stream test
                using var msEntry = new ValidationWriteStream(data);
                image.ReadFile(e.Inode, msEntry);
                msEntry.AssertComplete();
            }

            // Verify binary file content
            var subEntry = rootEntries.Find(e => e.Name == "sub");
            Assert.NotNull(subEntry);
            var subEntries = image.ListDirectory(subEntry.Inode);
            var binEntry = subEntries.Find(e => e.Name == "binary.bin");
            Assert.NotNull(binEntry);
            byte[] readData = image.ReadFileBytes(binEntry.Inode);
            Assert.Equal(binaryData, readData);

            // stream test
            using var ms = new ValidationWriteStream(binaryData);
            image.ReadFile(binEntry.Inode, ms);
            ms.AssertComplete();
        }
    }
}
