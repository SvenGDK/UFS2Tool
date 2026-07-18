// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests for the fsck_ufs command — validates filesystem consistency checking
    /// on UFS1/UFS2 filesystem images, matching FreeBSD fsck_ffs(8)/fsck_ufs(8) behavior.
    /// </summary>
    public class FsckUfsTests : IDisposable
    {
        private readonly string _imagePath;

        public FsckUfsTests()
        {
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2fsck_{Guid.NewGuid():N}.img");

            // Create a fresh UFS2 image for each test
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath))
                File.Delete(_imagePath);
        }

        /// <summary>
        /// A freshly created UFS2 image should pass all fsck checks cleanly.
        /// </summary>
        [Fact]
        public void FsckUfs_CleanImage_ReportsClean()
        {
            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var result = image.FsckUfs();

            Assert.True(result.Clean);
            Assert.Empty(result.Errors);
        }

        /// <summary>
        /// A freshly created UFS1 image should also pass all fsck checks.
        /// </summary>
        [Fact]
        public void FsckUfs_CleanUfs1Image_ReportsClean()
        {
            string ufs1Path = Path.Combine(Path.GetTempPath(), $"ufs1fsck_{Guid.NewGuid():N}.img");
            try
            {
                var creator = new Ufs2ImageCreator { FilesystemFormat = 1 };
                creator.CreateImage(ufs1Path, 64 * 1024 * 1024);

                using var image = new Ufs2Image(ufs1Path, readOnly: true);
                var result = image.FsckUfs();

                Assert.True(result.Clean);
                Assert.Empty(result.Errors);
            }
            finally
            {
                if (File.Exists(ufs1Path))
                    File.Delete(ufs1Path);
            }
        }

        /// <summary>
        /// Verify that fsck reports the correct number of files and directories.
        /// </summary>
        [Fact]
        public void FsckUfs_CountsFilesAndDirectories()
        {
            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var result = image.FsckUfs();

            // A fresh image has the root directory (counted as a directory) 
            Assert.True(result.Directories >= 1, "Should have at least the root directory");
        }

        /// <summary>
        /// Verify fsck detects a corrupted superblock magic number.
        /// </summary>
        [Fact]
        public void FsckUfs_CorruptedMagic_ReportsError()
        {
            // Corrupt the superblock magic number
            using (var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.ReadWrite))
            {
                // Magic is at offset 0x55C within the superblock, which is at SBLOCK_UFS2 (65536)
                fs.Position = Ufs2Constants.SuperblockOffset64K + 0x55C;
                var writer = new BinaryWriter(fs);
                writer.Write(0xDEADBEEF);
                writer.Flush();
            }

            // Ufs2Image constructor should throw on invalid magic
            Assert.Throws<InvalidDataException>(() => new Ufs2Image(_imagePath, readOnly: true));
        }

        /// <summary>
        /// Verify that fsck correctly reports CG magic number issues.
        /// </summary>
        [Fact]
        public void FsckUfs_CorruptedCgMagic_ReportsError()
        {
            // Read superblock to find CG location
            long cgHeaderOffset;
            using (var image = new Ufs2Image(_imagePath, readOnly: true))
            {
                var sb = image.Superblock;
                cgHeaderOffset = (long)sb.CblkNo * sb.FSize;
            }

            // Corrupt the CG magic number (at offset 0x04 within CG header)
            using (var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = cgHeaderOffset + 0x04;
                var writer = new BinaryWriter(fs);
                writer.Write(0xBADC0DE);
                writer.Flush();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            var result = verify.FsckUfs();

            Assert.False(result.Clean);
            Assert.Contains(result.Errors, e => e.Contains("BAD MAGIC"));
        }

        /// <summary>
        /// Verify that fsck detects incorrect free inode count in the superblock.
        /// </summary>
        [Fact]
        public void FsckUfs_WrongFreeInodeCount_ReportsWarning()
        {
            // Modify the superblock to have an incorrect free inode count
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.FreeInodes = 9999;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            var result = verify.FsckUfs();

            Assert.False(result.Clean);
            Assert.Contains(result.Warnings, w => w.Contains("FREE INODE COUNT WRONG"));
        }

        /// <summary>
        /// Verify that fsck detects incorrect free block count in the superblock.
        /// </summary>
        [Fact]
        public void FsckUfs_WrongFreeBlockCount_ReportsWarning()
        {
            // Modify the superblock to have an incorrect free block count
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.FreeBlocks = 9999;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            var result = verify.FsckUfs();

            Assert.False(result.Clean);
            Assert.Contains(result.Warnings, w => w.Contains("FREE BLOCK COUNT WRONG"));
        }

        /// <summary>
        /// Verify that fsck detects incorrect directory count in the superblock.
        /// </summary>
        [Fact]
        public void FsckUfs_WrongDirectoryCount_ReportsWarning()
        {
            // Modify the superblock to have an incorrect directory count
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Directories = 999;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            var result = verify.FsckUfs();

            Assert.False(result.Clean);
            Assert.Contains(result.Warnings, w => w.Contains("DIRECTORY COUNT WRONG"));
        }

        /// <summary>
        /// Verify that debug mode produces additional diagnostic output.
        /// </summary>
        [Fact]
        public void FsckUfs_DebugMode_ProducesExtraOutput()
        {
            using var image = new Ufs2Image(_imagePath, readOnly: true);

            var normalResult = image.FsckUfs(debug: false);
            var debugResult = image.FsckUfs(debug: true);

            Assert.True(debugResult.Messages.Count > normalResult.Messages.Count,
                "Debug mode should produce more diagnostic messages");
        }

        /// <summary>
        /// Verify that fsck works correctly on a larger image with multiple cylinder groups.
        /// </summary>
        [Fact]
        public void FsckUfs_MultiCgImage_ReportsClean()
        {
            string largePath = Path.Combine(Path.GetTempPath(), $"ufs2fsck_large_{Guid.NewGuid():N}.img");
            try
            {
                var creator = new Ufs2ImageCreator();
                creator.CreateImage(largePath, 256 * 1024 * 1024);

                using var image = new Ufs2Image(largePath, readOnly: true);
                Assert.True(image.Superblock.NumCylGroups > 1,
                    "Need multiple CGs for this test");

                var result = image.FsckUfs();

                Assert.True(result.Clean);
                Assert.Empty(result.Errors);
            }
            finally
            {
                if (File.Exists(largePath))
                    File.Delete(largePath);
            }
        }

        /// <summary>
        /// Verify that fsck works on a filesystem populated with files and directories.
        /// </summary>
        [Fact]
        public void FsckUfs_PopulatedFilesystem_ReportsClean()
        {
            string dirPath = Path.Combine(Path.GetTempPath(), $"ufs2fsck_src_{Guid.NewGuid():N}");
            string imgPath = Path.Combine(Path.GetTempPath(), $"ufs2fsck_pop_{Guid.NewGuid():N}.img");
            try
            {
                // Create source directory with files
                Directory.CreateDirectory(dirPath);
                Directory.CreateDirectory(Path.Combine(dirPath, "subdir"));
                File.WriteAllText(Path.Combine(dirPath, "file1.txt"), "Hello");
                File.WriteAllText(Path.Combine(dirPath, "subdir", "file2.txt"), "World");

                var creator = new Ufs2ImageCreator();
                creator.CreateImageFromDirectory(imgPath, dirPath);

                using var image = new Ufs2Image(imgPath, readOnly: true);
                var result = image.FsckUfs();

                Assert.True(result.Clean);
                Assert.Empty(result.Errors);
                Assert.True(result.Directories >= 2, "Should have at least root + subdir");
                Assert.True(result.Files >= 2, "Should have at least 2 files");
            }
            finally
            {
                if (Directory.Exists(dirPath))
                    Directory.Delete(dirPath, recursive: true);
                if (File.Exists(imgPath))
                    File.Delete(imgPath);
            }
        }

        /// <summary>
        /// Verify that preen mode works (same behavior for clean image).
        /// </summary>
        [Fact]
        public void FsckUfs_PreenMode_WorksOnCleanImage()
        {
            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var result = image.FsckUfs(preen: true);

            Assert.True(result.Clean);
            Assert.Empty(result.Errors);
        }

        /// <summary>
        /// Verify that fsck messages include the five standard phases.
        /// </summary>
        [Fact]
        public void FsckUfs_ReportsAllFivePhases()
        {
            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var result = image.FsckUfs();

            Assert.Contains(result.Messages, m => m.Contains("Phase 1"));
            Assert.Contains(result.Messages, m => m.Contains("Phase 2"));
            Assert.Contains(result.Messages, m => m.Contains("Phase 3"));
            Assert.Contains(result.Messages, m => m.Contains("Phase 4"));
            Assert.Contains(result.Messages, m => m.Contains("Phase 5"));
        }

        /// <summary>
        /// Verify that the result summary includes fragmentation percentage.
        /// </summary>
        [Fact]
        public void FsckUfs_ReportsSummaryWithFragmentation()
        {
            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var result = image.FsckUfs();

            Assert.Contains(result.Messages, m => m.Contains("fragmentation"));
        }

        /// <summary>
        /// Verify that fsck works after growfs has been applied.
        /// </summary>
        [Fact]
        public void FsckUfs_AfterGrowFs_ReportsClean()
        {
            string growPath = Path.Combine(Path.GetTempPath(), $"ufs2fsck_grow_{Guid.NewGuid():N}.img");
            try
            {
                var creator = new Ufs2ImageCreator();
                creator.CreateImage(growPath, 64 * 1024 * 1024);

                // Grow the file to 128 MB and then grow the filesystem
                using (var fs = new FileStream(growPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    fs.SetLength(128 * 1024 * 1024);
                }

                using (var image = new Ufs2Image(growPath, readOnly: false))
                {
                    image.GrowFs(128 * 1024 * 1024);
                }

                // Run fsck on the grown filesystem
                using var verify = new Ufs2Image(growPath, readOnly: true);
                var result = verify.FsckUfs();

                Assert.True(result.Clean);
                Assert.Empty(result.Errors);
            }
            finally
            {
                if (File.Exists(growPath))
                    File.Delete(growPath);
            }
        }

        /// <summary>
        /// Verify that fsck works after tunefs modifications.
        /// </summary>
        [Fact]
        public void FsckUfs_AfterTuneFs_ReportsClean()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.VolumeName = "FSCKTEST";
                image.Superblock.Flags |= Ufs2Constants.FsDosoftdep | Ufs2Constants.FsTrim;
                image.Superblock.MinFreePercent = 5;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            var result = verify.FsckUfs();

            Assert.True(result.Clean);
            Assert.Empty(result.Errors);
        }

        /// <summary>
        /// Verify that opening a filesystem image via an alternate (backup)
        /// superblock yields the same superblock as the primary location.
        /// Mirrors FreeBSD <c>fsck_ffs -b block</c>.
        /// </summary>
        [Fact]
        public void Open_AltSuperblock_MatchesPrimary()
        {
            // Use a multi-CG image so backup superblocks exist beyond CG 0.
            string altPath = Path.Combine(Path.GetTempPath(), $"ufs2alt_{Guid.NewGuid():N}.img");
            try
            {
                var creator = new Ufs2ImageCreator();
                // Default fpg is large enough that 64 MB fits in a single CG, so
                // size the image at 256 MB to guarantee multiple cylinder groups.
                creator.CreateImage(altPath, 256 * 1024 * 1024);

                Ufs2Superblock primary;
                long backupSbSector;
                using (var img = new Ufs2Image(altPath, readOnly: true))
                {
                    primary = img.Superblock;
                    Assert.True(img.Superblock.NumCylGroups >= 2,
                        "Test requires an image with at least 2 cylinder groups.");

                    // Backup superblock for CG 1 lives at (cg * fs_fpg + fs_sblkno)
                    // fragments. Convert that to 512-byte sectors for -b style use.
                    long backupSbFrag = (long)primary.CylGroupSize + primary.SuperblockLocation;
                    backupSbSector = backupSbFrag * primary.FSize / 512;
                }

                using var alt = new Ufs2Image(altPath, readOnly: true,
                    altSuperblockSector: backupSbSector);

                Assert.True(alt.Superblock.IsValid);
                Assert.Equal(primary.Magic, alt.Superblock.Magic);
                Assert.Equal(primary.NumCylGroups, alt.Superblock.NumCylGroups);
                Assert.Equal(primary.TotalBlocks, alt.Superblock.TotalBlocks);
                Assert.Equal(primary.BSize, alt.Superblock.BSize);
                Assert.Equal(primary.FSize, alt.Superblock.FSize);
                Assert.Equal(primary.InodesPerGroup, alt.Superblock.InodesPerGroup);

                // fsck against the backup-loaded image should still pass.
                var result = alt.FsckUfs();
                Assert.True(result.Clean);
                Assert.Empty(result.Errors);
            }
            finally
            {
                if (File.Exists(altPath))
                    File.Delete(altPath);
            }
        }

        /// <summary>
        /// Opening with an alternate superblock sector that points outside the image
        /// (or at uninitialized data) must throw rather than silently succeed.
        /// </summary>
        [Fact]
        public void Open_AltSuperblock_OutOfRange_Throws()
        {
            // A sector well beyond the end of the 64 MB image.
            long bogusSector = (200L * 1024 * 1024) / 512;
            Assert.ThrowsAny<Exception>(() =>
                new Ufs2Image(_imagePath, readOnly: true, altSuperblockSector: bogusSector));
        }
    }
}
