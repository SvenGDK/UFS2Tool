// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

using static System.Net.Mime.MediaTypeNames;

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests for the growfs command — validates filesystem expansion
    /// on existing UFS2 filesystem images, matching FreeBSD growfs(8) behavior.
    /// </summary>
    public class GrowFsTests : IDisposable
    {
        private readonly string _imagePath;

        public GrowFsTests()
        {
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2growfs_{Guid.NewGuid():N}.img");
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath))
                File.Delete(_imagePath);
        }

        /// <summary>
        /// Verify that growing a filesystem increases the number of cylinder groups.
        /// </summary>
        [Fact]
        public void GrowFs_IncreasesNumberOfCylinderGroups()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            int originalCgs;
            using (var image = new Ufs2Image(_imagePath, readOnly: true))
                originalCgs = image.Superblock.NumCylGroups;

            // Extend file and grow
            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(256 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(256 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.NumCylGroups > originalCgs);
        }

        /// <summary>
        /// Verify that growing a filesystem increases total fragments (fs_size).
        /// </summary>
        [Fact]
        public void GrowFs_IncreasesTotalFragments()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            long originalFrags;
            using (var image = new Ufs2Image(_imagePath, readOnly: true))
                originalFrags = image.Superblock.TotalBlocks;

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(128 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(128 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.TotalBlocks > originalFrags);
            Assert.Equal(128 * 1024 * 1024 / verify.Superblock.FSize, verify.Superblock.TotalBlocks);
        }

        /// <summary>
        /// Verify that growing a filesystem increases free inodes.
        /// </summary>
        [Fact]
        public void GrowFs_IncreaseFreeInodes()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            long originalFreeInodes;
            using (var image = new Ufs2Image(_imagePath, readOnly: true))
                originalFreeInodes = image.Superblock.FreeInodes;

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(256 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(256 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.FreeInodes > originalFreeInodes);
        }

        /// <summary>
        /// Verify that growing a filesystem increases free blocks.
        /// </summary>
        [Fact]
        public void GrowFs_IncreaseFreeBlocks()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            long originalFreeBlocks;
            using (var image = new Ufs2Image(_imagePath, readOnly: true))
                originalFreeBlocks = image.Superblock.FreeBlocks;

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(256 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(256 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.FreeBlocks > originalFreeBlocks);
        }

        /// <summary>
        /// Verify that the superblock magic number is preserved after growth.
        /// </summary>
        [Fact]
        public void GrowFs_PreservesMagicNumber()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(128 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(128 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.IsValid);
            Assert.Equal(Ufs2Constants.Ufs2Magic, verify.Superblock.Magic);
        }

        /// <summary>
        /// Verify that growing a filesystem preserves existing file content.
        /// </summary>
        [Fact]
        public void GrowFs_PreservesExistingFiles()
        {
            byte[] readDataFromStream = new byte[5000];

            // Create an image with files
            string testDir = Path.Combine(Path.GetTempPath(), $"ufs2growfs_dir_{Guid.NewGuid():N}");
            Directory.CreateDirectory(testDir);
            try
            {
                File.WriteAllText(Path.Combine(testDir, "hello.txt"), "Hello from growfs test!");
                File.WriteAllText(Path.Combine(testDir, "data.bin"), new string('X', 1024));

                var creator = new Ufs2ImageCreator();
                creator.CreateImageFromDirectory(_imagePath, testDir);

                // Grow the filesystem
                long currentSize;
                using (var image = new Ufs2Image(_imagePath, readOnly: true))
                    currentSize = image.Superblock.TotalBlocks * image.Superblock.FSize;

                long newSize = currentSize * 4;
                using (var fs = new FileStream(_imagePath, FileMode.Open))
                    fs.SetLength(newSize);

                using (var image = new Ufs2Image(_imagePath, readOnly: false))
                    image.GrowFs(newSize);

                // Verify files are still readable
                using var verify = new Ufs2Image(_imagePath, readOnly: true);
                var entries = verify.ListRoot();
                Assert.Contains(entries, e => e.Name == "hello.txt");
                Assert.Contains(entries, e => e.Name == "data.bin");

                // Verify file content
                var helloInode = entries.Find(e => e.Name == "hello.txt")!.Inode;
                byte[] content = verify.ReadFileBytes(helloInode);
                string text = System.Text.Encoding.UTF8.GetString(content).TrimEnd('\0');
                Assert.Contains("Hello from growfs test!", text);

                // stream test
                using var msEntry = new ValidationWriteStream(content);
                verify.ReadFile(helloInode, msEntry);
                msEntry.AssertComplete();
            }
            finally
            {
                if (Directory.Exists(testDir))
                    Directory.Delete(testDir, true);
            }
        }

        /// <summary>
        /// Verify that trying to shrink the filesystem throws an exception.
        /// </summary>
        [Fact]
        public void GrowFs_RejectsSmallerSize()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using var image = new Ufs2Image(_imagePath, readOnly: false);
            Assert.Throws<InvalidOperationException>(() =>
                image.GrowFs(32 * 1024 * 1024));
        }

        /// <summary>
        /// Verify that trying to grow a read-only image throws an exception.
        /// </summary>
        [Fact]
        public void GrowFs_RejectsReadOnlyImage()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Throws<InvalidOperationException>(() =>
                image.GrowFs(128 * 1024 * 1024));
        }

        /// <summary>
        /// Verify that dry-run mode does not modify the filesystem.
        /// </summary>
        [Fact]
        public void GrowFs_DryRunDoesNotModify()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            long originalFrags;
            int originalCgs;
            using (var image = new Ufs2Image(_imagePath, readOnly: true))
            {
                originalFrags = image.Superblock.TotalBlocks;
                originalCgs = image.Superblock.NumCylGroups;
            }

            using (var image = new Ufs2Image(_imagePath, readOnly: true))
                image.GrowFs(256 * 1024 * 1024, dryRun: true);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(originalFrags, verify.Superblock.TotalBlocks);
            Assert.Equal(originalCgs, verify.Superblock.NumCylGroups);
        }

        /// <summary>
        /// Verify growing a UFS1 filesystem works correctly.
        /// </summary>
        [Fact]
        public void GrowFs_Ufs1Filesystem()
        {
            var creator = new Ufs2ImageCreator();
            creator.FilesystemFormat = 1;
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            int originalCgs;
            using (var image = new Ufs2Image(_imagePath, readOnly: true))
            {
                originalCgs = image.Superblock.NumCylGroups;
                Assert.True(image.Superblock.IsUfs1);
            }

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(256 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(256 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.IsUfs1);
            Assert.True(verify.Superblock.NumCylGroups > originalCgs);
            Assert.Equal(256 * 1024 * 1024 / verify.Superblock.FSize, verify.Superblock.TotalBlocks);
        }

        /// <summary>
        /// Verify that growing within the same number of CGs (extending the last CG) works.
        /// </summary>
        [Fact]
        public void GrowFs_ExtendLastCylinderGroup()
        {
            // Create a small filesystem with 1 CG, then grow within the same CG
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 32 * 1024 * 1024);

            long originalFrags;
            int originalCgs;
            using (var image = new Ufs2Image(_imagePath, readOnly: true))
            {
                originalFrags = image.Superblock.TotalBlocks;
                originalCgs = image.Superblock.NumCylGroups;
            }

            // Grow by a small amount (within the same CG boundaries)
            long newSize = 48 * 1024 * 1024;
            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(newSize);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(newSize);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.TotalBlocks > originalFrags);
            // May or may not add a CG depending on sizes, but total frags should match
            Assert.Equal(newSize / verify.Superblock.FSize, verify.Superblock.TotalBlocks);
        }

        /// <summary>
        /// Verify that the block size and fragment size are preserved after growth.
        /// </summary>
        [Fact]
        public void GrowFs_PreservesBlockAndFragSizes()
        {
            var creator = new Ufs2ImageCreator();
            creator.BlockSize = 16384;
            creator.FragmentSize = 2048;
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(128 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(128 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(16384, verify.Superblock.BSize);
            Assert.Equal(2048, verify.Superblock.FSize);
        }

        /// <summary>
        /// Verify that growing by a large multiple works (e.g., 64MB to 1GB).
        /// </summary>
        [Fact]
        public void GrowFs_LargeGrowth()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(1024L * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(1024L * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.IsValid);
            Assert.Equal(1024L * 1024 * 1024 / verify.Superblock.FSize, verify.Superblock.TotalBlocks);
            Assert.True(verify.Superblock.NumCylGroups >= 16);
        }

        /// <summary>
        /// Verify that fs_csaddr (CG summary address) is set correctly after growth.
        /// </summary>
        [Fact]
        public void GrowFs_CsSummaryAddressIsValid()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(256 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(256 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            // CsAddr should be a valid fragment address within the filesystem
            Assert.True(verify.Superblock.CsAddr >= 0);
            Assert.True(verify.Superblock.CsAddr < verify.Superblock.TotalBlocks);
        }

        /// <summary>
        /// Verify that the volume name is preserved after growth.
        /// </summary>
        [Fact]
        public void GrowFs_PreservesVolumeLabel()
        {
            var creator = new Ufs2ImageCreator();
            creator.VolumeName = "TESTGROW";
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(128 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(128 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal("TESTGROW", verify.Superblock.VolumeName);
        }

        /// <summary>
        /// Verify that flags (soft updates, etc.) are preserved after growth.
        /// </summary>
        [Fact]
        public void GrowFs_PreservesFlags()
        {
            var creator = new Ufs2ImageCreator();
            creator.SoftUpdates = true;
            creator.TrimEnabled = true;
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using (var fs = new FileStream(_imagePath, FileMode.Open))
                fs.SetLength(128 * 1024 * 1024);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.GrowFs(128 * 1024 * 1024);

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsDosoftdep);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsTrim);
        }

        /// <summary>
        /// Verify that trying to grow to the same size produces an error.
        /// </summary>
        [Fact]
        public void GrowFs_RejectsEqualSize()
        {
            var creator = new Ufs2ImageCreator();
            creator.CreateImage(_imagePath, 64 * 1024 * 1024);

            using var image = new Ufs2Image(_imagePath, readOnly: false);
            Assert.Throws<InvalidOperationException>(() =>
                image.GrowFs(64 * 1024 * 1024));
        }
    }
}
