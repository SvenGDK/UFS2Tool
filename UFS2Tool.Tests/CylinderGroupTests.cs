// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests that verify cylinder group header fields are correctly set,
    /// particularly the cluster-related fields that FreeBSD's fsck_ufs validates.
    /// </summary>
    public class CylinderGroupTests : IDisposable
    {
        private readonly string _imagePath;

        public CylinderGroupTests()
        {
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2cg_{Guid.NewGuid():N}.img");
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath))
                File.Delete(_imagePath);
        }

        /// <summary>
        /// Verify that CG cluster fields (cg_nclusterblks, cg_clustersumoff, cg_clusteroff,
        /// cg_nextfreeoff) are set correctly per FreeBSD's fsck_ufs validation formulas.
        /// </summary>
        [Fact]
        public void CylinderGroup_ClusterFieldsAreCorrect()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024; // 256 MB
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // Read superblock
            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            Assert.True(sb.ContigSumSize > 0, "ContigSumSize should be > 0 for cluster support");

            // Check each cylinder group
            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;

                // Read CG header fields
                fs.Position = cgHeaderOffset + 0x14; // cg_ndblk
                int ndblk = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x60; // cg_freeoff
                int freeoff = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x64; // cg_nextfreeoff
                int nextfreeoff = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x68; // cg_clustersumoff
                int clustersumoff = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x6C; // cg_clusteroff
                int clusteroff = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x70; // cg_nclusterblks
                int nclusterblks = reader.ReadInt32();

                // Validate per FreeBSD fsck_ufs formulas
                int expectedNclusterblks = ndblk / sb.FragsPerBlock;
                Assert.Equal(expectedNclusterblks, nclusterblks);

                // Per FreeBSD fsck_ffs pass5.c validation:
                // clustersumoff = roundup(freeoff + howmany(fs_fpg, CHAR_BIT), sizeof(uint)) - sizeof(uint)
                int fragBitmapBytes = (sb.CylGroupSize + 7) / 8;
                int rawEnd = freeoff + fragBitmapBytes;
                int expectedClustersumoff = RoundUp(rawEnd, 4) - 4;
                Assert.Equal(expectedClustersumoff, clustersumoff);

                int expectedClusteroff = clustersumoff + (sb.ContigSumSize + 1) * 4;
                Assert.Equal(expectedClusteroff, clusteroff);

                int expectedNextfreeoff = clusteroff +
                    (FragsToBlks(sb.CylGroupSize, sb.FragsPerBlock) + 7) / 8;
                Assert.Equal(expectedNextfreeoff, nextfreeoff);

                // nclusterblks should be non-zero
                Assert.True(nclusterblks > 0, $"CG {cg}: nclusterblks should be > 0");
            }
        }

        /// <summary>
        /// Verify that the root directory data block doesn't overlap with the CG summary area.
        /// </summary>
        [Fact]
        public void CylinderGroup_RootDirDoesNotOverlapCsSummary()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024; // 256 MB
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // Read superblock
            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // CG summary occupies fragments at csAddr
            long csAddr = sb.CsAddr;
            int csFrags = (sb.CsSize + sb.FSize - 1) / sb.FSize;

            // Root inode's data block should be after CG summary
            long inodeTableOffset = (long)sb.IblkNo * sb.FSize;
            fs.Position = inodeTableOffset + (long)Ufs2Constants.RootInode * Ufs2Constants.Ufs2InodeSize;
            var rootInode = Ufs2Inode.ReadFrom(reader);

            long rootDataFrag = rootInode.DirectBlocks[0];
            Assert.True(rootDataFrag >= csAddr + csFrags,
                $"Root directory data frag ({rootDataFrag}) must be >= csAddr + csFrags ({csAddr + csFrags})");
        }

        /// <summary>
        /// Verify that fs_cgsize accounts for cluster data.
        /// </summary>
        [Fact]
        public void Superblock_CgSizeIncludesClusterData()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // CG size should include cluster data
            int cgFixedHeader = Ufs2Constants.CgHeaderBaseSize;
            int inodeBitmapSize = (sb.InodesPerGroup + 7) / 8;
            int fragBitmapSize = (sb.CylGroupSize + 7) / 8;
            int minCgSize = cgFixedHeader + inodeBitmapSize + fragBitmapSize;

            if (sb.ContigSumSize > 0)
            {
                int blocksPerGroup = sb.CylGroupSize / sb.FragsPerBlock;
                minCgSize += sb.ContigSumSize * 4 + (blocksPerGroup + 7) / 8;
            }

            Assert.True(sb.CgSize >= minCgSize,
                $"CgSize ({sb.CgSize}) should be >= minimum ({minCgSize}) to include cluster data");
        }

        /// <summary>
        /// Verify that the CG summary area at CsAddr contains valid per-CG data
        /// that matches each CG header's summary fields.
        /// </summary>
        [Fact]
        public void CylinderGroup_CsSummaryAreaMatchesCgHeaders()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024; // 256 MB
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            // Read the CG summary area at CsAddr
            long csAreaOffset = sb.CsAddr * sb.FSize;

            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                // Read per-CG summary from the CG summary area
                fs.Position = csAreaOffset + cg * Ufs2Constants.CsumStructSize;
                int csNdir = reader.ReadInt32();
                int csNbfree = reader.ReadInt32();
                int csNifree = reader.ReadInt32();
                int csNffree = reader.ReadInt32();

                // Read the same fields from the CG header
                long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;

                fs.Position = cgHeaderOffset + 0x18; // cg_cs
                int cgNdir = reader.ReadInt32();
                int cgNbfree = reader.ReadInt32();
                int cgNifree = reader.ReadInt32();
                int cgNffree = reader.ReadInt32();

                Assert.Equal(cgNdir, csNdir);
                Assert.Equal(cgNbfree, csNbfree);
                Assert.Equal(cgNifree, csNifree);
                Assert.Equal(cgNffree, csNffree);
            }
        }

        /// <summary>
        /// Verify that the CG summary area remains consistent after directory population.
        /// </summary>
        [Fact]
        public void CylinderGroup_CsSummaryAreaMatchesAfterPopulate()
        {
            var testDir = Path.Combine(Path.GetTempPath(), $"ufs2cgtest_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(testDir);
                Directory.CreateDirectory(Path.Combine(testDir, "subdir"));
                File.WriteAllText(Path.Combine(testDir, "file.txt"), "test content");
                File.WriteAllBytes(Path.Combine(testDir, "subdir", "data.bin"), new byte[50000]);

                var creator = new Ufs2ImageCreator();
                long imageSize = 256 * 1024 * 1024;
                creator.CreateImage(_imagePath, imageSize);
                creator.PopulateFromDirectory(_imagePath, testDir);

                using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);

                fs.Position = Ufs2Constants.SuperblockOffset64K;
                var sb = Ufs2Superblock.ReadFrom(reader);

                long csAreaOffset = sb.CsAddr * sb.FSize;
                long totalNdir = 0, totalNbfree = 0, totalNifree = 0, totalNffree = 0;

                for (int cg = 0; cg < sb.NumCylGroups; cg++)
                {
                    // Read from CG summary area
                    fs.Position = csAreaOffset + cg * Ufs2Constants.CsumStructSize;
                    int csNdir = reader.ReadInt32();
                    int csNbfree = reader.ReadInt32();
                    int csNifree = reader.ReadInt32();
                    int csNffree = reader.ReadInt32();

                    // Read from CG header
                    long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                    long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;
                    fs.Position = cgHeaderOffset + 0x18;
                    int cgNdir = reader.ReadInt32();
                    int cgNbfree = reader.ReadInt32();
                    int cgNifree = reader.ReadInt32();
                    int cgNffree = reader.ReadInt32();

                    Assert.Equal(cgNdir, csNdir);
                    Assert.Equal(cgNbfree, csNbfree);
                    Assert.Equal(cgNifree, csNifree);
                    Assert.Equal(cgNffree, csNffree);

                    totalNdir += csNdir;
                    totalNbfree += csNbfree;
                    totalNifree += csNifree;
                    totalNffree += csNffree;
                }

                // Superblock totals should match sum of per-CG summaries
                Assert.Equal(sb.Directories, totalNdir);
                Assert.Equal(sb.FreeBlocks, totalNbfree);
                Assert.Equal(sb.FreeInodes, totalNifree);
                Assert.Equal(sb.FreeFragments, totalNffree);
            }
            finally
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir, recursive: true);
            }
        }

        /// <summary>
        /// Verify that cg_frsum is correctly set for each cylinder group.
        /// The sum of i*frsum[i] should equal the CG's cs_nffree.
        /// </summary>
        [Fact]
        public void CylinderGroup_FrsumMatchesFreeFragCount()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024;
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;

                // Read cs_nffree
                fs.Position = cgHeaderOffset + 0x24;
                int nffree = reader.ReadInt32();

                // Read cg_frsum[8]
                fs.Position = cgHeaderOffset + 0x34;
                int[] frsum = new int[8];
                for (int i = 0; i < 8; i++)
                    frsum[i] = reader.ReadInt32();

                // Sum of i*frsum[i] should equal nffree
                int frsumTotal = 0;
                for (int i = 1; i < 8; i++)
                    frsumTotal += i * frsum[i];

                Assert.Equal(nffree, frsumTotal);
            }
        }

        /// <summary>
        /// Verify that CG cluster fields are correct even when the last CG has fewer
        /// fragments than fragsPerGroup. This specifically tests the cg_nextfreeoff
        /// calculation which fsck validates using fs_fpg, not cg_ndblk.
        /// </summary>
        [Fact]
        public void CylinderGroup_ClusterFieldsCorrect_UnevenLastCG()
        {
            // Choose a size that doesn't divide evenly by fragsPerGroup,
            // ensuring the last CG is smaller than the others.
            var creator = new Ufs2ImageCreator();
            int fragsPerBlock = creator.BlockSize / creator.FragmentSize;
            int fragsPerGroup = Ufs2Constants.DefaultInodesPerGroup * fragsPerBlock;
            // Use 2.5 CGs worth of fragments to ensure last CG is smaller
            long totalFrags = (long)fragsPerGroup * 2 + fragsPerGroup / 2;
            long imageSize = totalFrags * creator.FragmentSize;

            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            Assert.True(sb.NumCylGroups >= 2, "Need at least 2 CGs for this test");

            // Verify the last CG has fewer fragments
            long lastCgStartFrag = (long)(sb.NumCylGroups - 1) * sb.CylGroupSize;
            int lastCgFrags = (int)(sb.TotalBlocks - lastCgStartFrag);
            Assert.True(lastCgFrags < sb.CylGroupSize,
                $"Last CG should have fewer fragments ({lastCgFrags}) than fragsPerGroup ({sb.CylGroupSize})");

            // Check each CG's cluster fields
            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;

                fs.Position = cgHeaderOffset + 0x14;
                int ndblk = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x60;
                int freeoff = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x64;
                int nextfreeoff = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x68;
                int clustersumoff = reader.ReadInt32();

                fs.Position = cgHeaderOffset + 0x6C;
                int clusteroff = reader.ReadInt32();

                // nextfreeoff must use fs_fpg (fragsPerGroup), not cg_ndblk
                int expectedNextfreeoff = clusteroff +
                    (FragsToBlks(sb.CylGroupSize, sb.FragsPerBlock) + 7) / 8;
                Assert.Equal(expectedNextfreeoff, nextfreeoff);
            }
        }

        /// <summary>
        /// Verify that backup superblocks are updated after population.
        /// </summary>
        [Fact]
        public void BackupSuperblocks_UpdatedAfterPopulate()
        {
            var testDir = Path.Combine(Path.GetTempPath(), $"ufs2backup_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(testDir);
                for (int i = 0; i < 5; i++)
                    File.WriteAllBytes(Path.Combine(testDir, $"file_{i}.bin"), new byte[1000]);

                var creator = new Ufs2ImageCreator();
                long imageSize = 256 * 1024 * 1024;
                creator.CreateImage(_imagePath, imageSize);
                creator.PopulateFromDirectory(_imagePath, testDir);

                using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);

                fs.Position = Ufs2Constants.SuperblockOffset64K;
                var sb = Ufs2Superblock.ReadFrom(reader);

                // Check that backup superblocks match the primary
                for (int cg = 1; cg < sb.NumCylGroups; cg++)
                {
                    long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                    long backupSbOffset = cgStartByte + (long)sb.SuperblockLocation * sb.FSize;
                    fs.Position = backupSbOffset;
                    var backupSb = Ufs2Superblock.ReadFrom(reader);

                    Assert.Equal(sb.FreeBlocks, backupSb.FreeBlocks);
                    Assert.Equal(sb.FreeInodes, backupSb.FreeInodes);
                    Assert.Equal(sb.Directories, backupSb.Directories);
                    Assert.Equal(sb.FreeFragments, backupSb.FreeFragments);
                }
            }
            finally
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir, recursive: true);
            }
        }

        /// <summary>
        /// Verify that for UFS2 images, cg_iusedoff equals sizeof(struct cg) (168)
        /// and cg_old_btotoff / cg_old_boff are zero, per FreeBSD mkfs.c initcg()
        /// and fsck_ffs pass5.c validation.
        /// This is the exact check that FreeBSD's fsck performs:
        ///   cgp->cg_iusedoff must equal sizeof(struct cg) for UFS2.
        /// </summary>
        [Fact]
        public void CylinderGroup_Ufs2_IusedoffEqualsStructCgSize()
        {
            var creator = new Ufs2ImageCreator();
            long imageSize = 256 * 1024 * 1024; // 256 MB
            creator.CreateImage(_imagePath, imageSize);

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            int expectedIusedoff = Ufs2Constants.CgHeaderBaseSize; // 168 = sizeof(struct cg)

            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStartByte = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHeaderOffset = cgStartByte + (long)sb.CblkNo * sb.FSize;

                // cg_old_btotoff (0x54) — should be 0 for UFS2
                fs.Position = cgHeaderOffset + 0x54;
                int oldBtotoff = reader.ReadInt32();
                Assert.Equal(0, oldBtotoff);

                // cg_old_boff (0x58) — should be 0 for UFS2
                fs.Position = cgHeaderOffset + 0x58;
                int oldBoff = reader.ReadInt32();
                Assert.Equal(0, oldBoff);

                // cg_iusedoff (0x5C) — must equal sizeof(struct cg) = 168 for UFS2
                fs.Position = cgHeaderOffset + 0x5C;
                int iusedoff = reader.ReadInt32();
                Assert.Equal(expectedIusedoff, iusedoff);

                // cg_freeoff (0x60) — must equal cg_iusedoff + howmany(fs_ipg, 8)
                fs.Position = cgHeaderOffset + 0x60;
                int freeoff = reader.ReadInt32();
                int expectedFreeoff = expectedIusedoff + (sb.InodesPerGroup + 7) / 8;
                Assert.Equal(expectedFreeoff, freeoff);
            }
        }

        private static int RoundUp(int value, int alignment)
        {
            return ((value + alignment - 1) / alignment) * alignment;
        }

        private static int FragsToBlks(int frags, int fragsPerBlock)
        {
            return frags / fragsPerBlock;
        }
    }
}
