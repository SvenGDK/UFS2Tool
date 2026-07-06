// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests that verify the "newfs -D" option correctly:
    /// 1. Creates an empty image based on directory size (with UFS2 overhead)
    /// 2. Constructs a UFS2 filesystem like "newfs -O 2 -b 32768 -f 4096 /dev/md0"
    /// 3. Adds files and folders recursively to the created UFS2 partition
    /// </summary>
    public class NewfsDOptionTests : IDisposable
    {
        private readonly string _testDir;
        private readonly string _imagePath;

        public NewfsDOptionTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"ufs2test_{Guid.NewGuid():N}");
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2test_{Guid.NewGuid():N}.img");
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath))
                File.Delete(_imagePath);
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }

        // -----------------------------------------------------------------------
        // Requirement 1: Empty image based on directory size with UFS2 overhead
        // -----------------------------------------------------------------------

        [Fact]
        public void CalculateDirectorySizes_ReturnsCorrectRawAndBlockAligned()
        {
            int blockSize = Ufs2Constants.DefaultBlockSize; // 32768
            File.WriteAllBytes(Path.Combine(_testDir, "file1.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(_testDir, "file2.bin"), new byte[50000]);
            Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));

            var (rawSize, blockAlignedSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, blockSize);

            // Raw size = 1000 + 50000 = 51000
            Assert.Equal(51000, rawSize);

            // Block-aligned: file1 rounds to 1 block (32768), file2 rounds to 2 blocks (65536)
            // + 1 block for subdir + 1 block for root = 4 blocks = 131072
            long expectedAligned = (1 * blockSize) + (2 * blockSize) + blockSize + blockSize;
            Assert.Equal(expectedAligned, blockAlignedSize);
        }

        [Fact]
        public void CalculateImageSize_AddsOverheadForUfs2Metadata()
        {
            var creator = new Ufs2ImageCreator();
            long blockAlignedSize = 1024 * 1024; // 1 MB of data

            long imageSize = creator.CalculateImageSize(blockAlignedSize);

            // Should add 10% overhead + 10 MB safety margin
            long expected = (long)(blockAlignedSize * 1.10) + (10 * 1024 * 1024);
            expected = ((expected + creator.FragmentSize - 1) / creator.FragmentSize) * creator.FragmentSize;
            Assert.Equal(expected, imageSize);
            Assert.True(imageSize > blockAlignedSize,
                "Image size must be larger than data size to accommodate UFS2 metadata");
        }

        [Fact]
        public void CalculateImageSize_EnforcesMinimumSize()
        {
            var creator = new Ufs2ImageCreator();
            long tinySize = 100; // Very small data

            long imageSize = creator.CalculateImageSize(tinySize);

            // Minimum: 10% + 10MB, aligned to fragment
            Assert.True(imageSize >= creator.BlockSize * 16,
                "Image size must meet minimum of 16 blocks");
        }

        [Fact]
        public void DOption_CreatesImageWithCorrectSize()
        {
            // Create test data
            File.WriteAllBytes(Path.Combine(_testDir, "data.bin"), new byte[100_000]);
            Directory.CreateDirectory(Path.Combine(_testDir, "dir1"));
            File.WriteAllText(Path.Combine(_testDir, "dir1", "inner.txt"), "inner content");

            var creator = new Ufs2ImageCreator();
            int blockSize = creator.BlockSize;

            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, blockSize);
            long expectedImageSize = creator.CalculateImageSize(diskSize);

            // Create the image
            creator.CreateImage(_imagePath, expectedImageSize);

            Assert.True(File.Exists(_imagePath), "Image file should be created");
            var fileInfo = new FileInfo(_imagePath);
            Assert.Equal(expectedImageSize, fileInfo.Length);
        }

        // -----------------------------------------------------------------------
        // Requirement 2: UFS2 filesystem constructed like FreeBSD newfs
        //   "newfs -O 2 -b 32768 -f 4096 /dev/md0"
        // -----------------------------------------------------------------------

        [Fact]
        public void DOption_CreatesValidUfs2Superblock()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "hello");
            var creator = new Ufs2ImageCreator();

            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            Assert.True(sb.IsValid, "Superblock should be valid");
            Assert.True(sb.IsUfs2, "Filesystem should be UFS2 format");
            Assert.Equal(Ufs2Constants.Ufs2Magic, sb.Magic);
        }

        [Fact]
        public void DOption_UsesDefaultBlockAndFragmentSizes()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "hello");
            var creator = new Ufs2ImageCreator();

            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            // Default newfs parameters: -b 32768 -f 4096
            Assert.Equal(32768, sb.BSize);
            Assert.Equal(4096, sb.FSize);
            Assert.Equal(8, sb.FragsPerBlock); // 32768 / 4096
        }

        [Fact]
        public void DOption_HasCorrectCylinderGroupLayout()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "hello");
            var creator = new Ufs2ImageCreator();

            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            Assert.True(sb.NumCylGroups >= 1, "Should have at least 1 cylinder group");
            Assert.True(sb.InodesPerGroup > 0, "Should have inodes per group");
            Assert.True(sb.CylGroupSize > 0, "Should have fragments per group");
            Assert.Equal(Ufs2Constants.SuperblockOffset64K, sb.SbBlockLoc);
        }

        [Fact]
        public void DOption_HasRootDirectoryAsInode2()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "hello");
            var creator = new Ufs2ImageCreator();

            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);

            // Root directory should be at inode 2
            var rootInode = image.ReadInode(Ufs2Constants.RootInode);
            Assert.True(rootInode.IsDirectory, "Root inode should be a directory");
            Assert.True(rootInode.NLink >= 2, "Root directory should have at least nlink=2");
            Assert.True(rootInode.Size > 0, "Root directory should have non-zero size");
        }

        [Fact]
        public void DOption_RootDirectoryContainsDotEntries()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "hello");
            var creator = new Ufs2ImageCreator();

            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var entries = image.ListRoot();

            // Should have at least "." and ".." entries
            Assert.Contains(entries, e => e.Name == ".");
            Assert.Contains(entries, e => e.Name == "..");

            // "." and ".." should both point to root inode 2
            var dot = entries.First(e => e.Name == ".");
            var dotdot = entries.First(e => e.Name == "..");
            Assert.Equal((uint)Ufs2Constants.RootInode, dot.Inode);
            Assert.Equal((uint)Ufs2Constants.RootInode, dotdot.Inode);
        }

        [Fact]
        public void DOption_SuperblockCountsAreConsistent()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "hello");
            var creator = new Ufs2ImageCreator();

            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            Assert.True(sb.FreeInodes > 0, "Should have free inodes");
            Assert.True(sb.FreeBlocks >= 0, "Free blocks should be non-negative");
            Assert.True(sb.Directories >= 1, "Should have at least 1 directory (root)");
            Assert.True(sb.TotalBlocks > 0, "Total fragments should be positive");
        }

        // -----------------------------------------------------------------------
        // Requirement 3: Files and folders added recursively
        // -----------------------------------------------------------------------

        [Fact]
        public void DOption_PopulatesFilesRecursively()
        {
            // Create a directory structure with files at multiple levels
            File.WriteAllText(Path.Combine(_testDir, "root_file.txt"), "root content");
            Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
            File.WriteAllText(Path.Combine(_testDir, "subdir", "sub_file.txt"), "sub content");
            Directory.CreateDirectory(Path.Combine(_testDir, "subdir", "nested"));
            File.WriteAllText(Path.Combine(_testDir, "subdir", "nested", "deep.txt"), "deep");

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootEntries = image.ListRoot();

            // Root should contain root_file.txt and subdir (plus . and ..)
            Assert.Contains(rootEntries, e => e.Name == "root_file.txt" && e.FileType == Ufs2Constants.DtReg);
            Assert.Contains(rootEntries, e => e.Name == "subdir" && e.FileType == Ufs2Constants.DtDir);
        }

        [Fact]
        public void DOption_SubdirectoryContainsItsFiles()
        {
            Directory.CreateDirectory(Path.Combine(_testDir, "mydir"));
            File.WriteAllText(Path.Combine(_testDir, "mydir", "a.txt"), "file a");
            File.WriteAllText(Path.Combine(_testDir, "mydir", "b.txt"), "file b");

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootEntries = image.ListRoot();

            // Find subdirectory inode
            var subdirEntry = rootEntries.First(e => e.Name == "mydir");
            Assert.Equal(Ufs2Constants.DtDir, subdirEntry.FileType);

            // Read subdirectory contents
            var subEntries = image.ListDirectory(subdirEntry.Inode);
            Assert.Contains(subEntries, e => e.Name == ".");
            Assert.Contains(subEntries, e => e.Name == "..");
            Assert.Contains(subEntries, e => e.Name == "a.txt" && e.FileType == Ufs2Constants.DtReg);
            Assert.Contains(subEntries, e => e.Name == "b.txt" && e.FileType == Ufs2Constants.DtReg);
        }

        [Fact]
        public void DOption_FileContentIsPreserved()
        {
            string content = "Hello, UFS2 World! This is test content.";
            File.WriteAllText(Path.Combine(_testDir, "hello.txt"), content);

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootEntries = image.ListRoot();
            var fileEntry = rootEntries.First(e => e.Name == "hello.txt");

            byte[] fileData = image.ReadFile(fileEntry.Inode);
            string readContent = System.Text.Encoding.UTF8.GetString(fileData);

            Assert.Equal(content, readContent);
        }

        [Fact]
        public void DOption_BinaryFileContentIsPreserved()
        {
            // Create a binary file with known content
            byte[] originalData = new byte[100_000];
            new Random(42).NextBytes(originalData);
            File.WriteAllBytes(Path.Combine(_testDir, "binary.dat"), originalData);

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootEntries = image.ListRoot();
            var fileEntry = rootEntries.First(e => e.Name == "binary.dat");

            byte[] readData = image.ReadFile(fileEntry.Inode);
            Assert.Equal(originalData.Length, readData.Length);
            Assert.Equal(originalData, readData);
        }

        [Fact]
        public void DOption_NestedDirectoryStructure()
        {
            // Create deeply nested structure: dir1/dir2/dir3/file.txt
            var path = _testDir;
            for (int i = 1; i <= 3; i++)
            {
                path = Path.Combine(path, $"dir{i}");
                Directory.CreateDirectory(path);
            }
            File.WriteAllText(Path.Combine(path, "deepfile.txt"), "deep content");

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);

            // Navigate through the directory tree
            var entries = image.ListRoot();
            var dir1 = entries.First(e => e.Name == "dir1");
            Assert.Equal(Ufs2Constants.DtDir, dir1.FileType);

            entries = image.ListDirectory(dir1.Inode);
            var dir2 = entries.First(e => e.Name == "dir2");
            Assert.Equal(Ufs2Constants.DtDir, dir2.FileType);

            entries = image.ListDirectory(dir2.Inode);
            var dir3 = entries.First(e => e.Name == "dir3");
            Assert.Equal(Ufs2Constants.DtDir, dir3.FileType);

            entries = image.ListDirectory(dir3.Inode);
            Assert.Contains(entries, e => e.Name == "deepfile.txt" && e.FileType == Ufs2Constants.DtReg);
        }

        [Fact]
        public void DOption_DirectoryCountMatchesActual()
        {
            // Create 3 directories total (+ root = 4)
            Directory.CreateDirectory(Path.Combine(_testDir, "a"));
            Directory.CreateDirectory(Path.Combine(_testDir, "b"));
            Directory.CreateDirectory(Path.Combine(_testDir, "b", "c"));
            File.WriteAllText(Path.Combine(_testDir, "file.txt"), "test");

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            // 4 directories: root + a + b + c
            Assert.Equal(4, sb.Directories);
        }

        [Fact]
        public void DOption_MultipleFilesInRootDirectory()
        {
            // Create multiple files in root
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(Path.Combine(_testDir, $"file{i:D2}.txt"), $"Content {i}");
            }

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootEntries = image.ListRoot();

            // Should have all 10 files + "." + ".."
            var fileEntries = rootEntries.Where(e => e.FileType == Ufs2Constants.DtReg).ToList();
            Assert.Equal(10, fileEntries.Count);

            for (int i = 0; i < 10; i++)
            {
                Assert.Contains(fileEntries, e => e.Name == $"file{i:D2}.txt");
            }
        }

        [Fact]
        public void DOption_EmptyDirectoryIsCreated()
        {
            Directory.CreateDirectory(Path.Combine(_testDir, "emptydir"));

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootEntries = image.ListRoot();

            var emptyDir = rootEntries.First(e => e.Name == "emptydir");
            Assert.Equal(Ufs2Constants.DtDir, emptyDir.FileType);

            // Empty directory should still have "." and ".."
            var subEntries = image.ListDirectory(emptyDir.Inode);
            Assert.Contains(subEntries, e => e.Name == ".");
            Assert.Contains(subEntries, e => e.Name == "..");
            // No other entries
            Assert.Equal(2, subEntries.Count);
        }

        [Fact]
        public void DOption_VolumeNameIsSet()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "hello");
            var creator = new Ufs2ImageCreator { VolumeName = "MYVOLUME" };

            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal("MYVOLUME", image.Superblock.VolumeName);
        }

        [Fact]
        public void DOption_FreeInodeCountReflectsUsage()
        {
            // Create 5 files + 2 directories = 7 inodes used (+ 3 reserved = 10)
            Directory.CreateDirectory(Path.Combine(_testDir, "d1"));
            Directory.CreateDirectory(Path.Combine(_testDir, "d2"));
            for (int i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(_testDir, $"f{i}.txt"), $"data{i}");

            var creator = new Ufs2ImageCreator();
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            long imageSize = creator.CalculateImageSize(diskSize);
            creator.CreateImage(_imagePath, imageSize);
            creator.PopulateFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            // nextInode should be 3 (reserved) + 5 files + 2 dirs = 10
            // Total inodes = InodesPerGroup * NumCylGroups
            long totalInodes = (long)sb.InodesPerGroup * sb.NumCylGroups;
            long usedInodes = totalInodes - sb.FreeInodes;
            Assert.Equal(10, usedInodes); // 3 reserved + 5 files + 2 dirs
        }

        // -----------------------------------------------------------------------
        // Requirement: CreateImageFromDirectory — unified create + populate
        // -----------------------------------------------------------------------

        [Fact]
        public void CreateImageFromDirectory_AutoSizes_CreatesAndPopulates()
        {
            // Create a directory with files and a subdirectory
            File.WriteAllText(Path.Combine(_testDir, "file1.txt"), "hello world");
            Directory.CreateDirectory(Path.Combine(_testDir, "sub"));
            File.WriteAllBytes(Path.Combine(_testDir, "sub", "data.bin"), new byte[5000]);

            var creator = new Ufs2ImageCreator();
            long imageSize = creator.CreateImageFromDirectory(_imagePath, _testDir);

            // Verify image was created with auto-calculated size
            Assert.True(File.Exists(_imagePath), "Image file should be created");
            Assert.Equal(imageSize, new FileInfo(_imagePath).Length);

            // Verify filesystem is valid and populated
            using var image = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(image.Superblock.IsValid);
            Assert.True(image.Superblock.IsUfs2);

            var rootEntries = image.ListRoot();
            Assert.Contains(rootEntries, e => e.Name == "file1.txt" && e.FileType == Ufs2Constants.DtReg);
            Assert.Contains(rootEntries, e => e.Name == "sub" && e.FileType == Ufs2Constants.DtDir);

            // Verify file content is preserved
            var fileEntry = rootEntries.First(e => e.Name == "file1.txt");
            string content = System.Text.Encoding.UTF8.GetString(image.ReadFile(fileEntry.Inode));
            Assert.Equal("hello world", content);
        }

        [Fact]
        public void CreateImageFromDirectory_WithExplicitSize_UsesProvidedSize()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            long explicitSize = 64 * 1024 * 1024; // 64 MB
            long returnedSize = creator.CreateImageFromDirectory(_imagePath, _testDir, explicitSize);

            Assert.Equal(explicitSize, returnedSize);
            Assert.Equal(explicitSize, new FileInfo(_imagePath).Length);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var rootEntries = image.ListRoot();
            Assert.Contains(rootEntries, e => e.Name == "test.txt");
        }

        [Fact]
        public void CreateImageFromDirectory_RecursivelyAddsNestedContent()
        {
            // Create nested structure: a/b/c/deep.txt (like tar -C input -cf - .)
            var nested = Path.Combine(_testDir, "a", "b", "c");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "deep.txt"), "deep content");
            File.WriteAllText(Path.Combine(_testDir, "root.txt"), "root content");

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);

            // Navigate root -> a -> b -> c -> deep.txt
            var entries = image.ListRoot();
            Assert.Contains(entries, e => e.Name == "root.txt");
            var dirA = entries.First(e => e.Name == "a");

            entries = image.ListDirectory(dirA.Inode);
            var dirB = entries.First(e => e.Name == "b");

            entries = image.ListDirectory(dirB.Inode);
            var dirC = entries.First(e => e.Name == "c");

            entries = image.ListDirectory(dirC.Inode);
            Assert.Contains(entries, e => e.Name == "deep.txt" && e.FileType == Ufs2Constants.DtReg);

            // Verify file content at leaf
            var deepFile = entries.First(e => e.Name == "deep.txt");
            string content = System.Text.Encoding.UTF8.GetString(image.ReadFile(deepFile.Inode));
            Assert.Equal("deep content", content);
        }

        [Fact]
        public void CreateImageFromDirectory_ThrowsOnMissingDirectory()
        {
            var creator = new Ufs2ImageCreator();
            Assert.Throws<DirectoryNotFoundException>(() =>
                creator.CreateImageFromDirectory(_imagePath, "/nonexistent/path"));
        }

        [Fact]
        public void CreateImageFromDirectory_ManySmallFilesAllocatesEnoughInodes()
        {
            // Create enough empty files to exceed a single CG's inode capacity (2048).
            // Previously, the auto-sizing only considered data size, so many small/empty
            // files would create an image with too few CGs, causing inode overflow.
            for (int d = 0; d < 30; d++)
            {
                var dir = Path.Combine(_testDir, $"dir_{d:D3}");
                Directory.CreateDirectory(dir);
                for (int f = 0; f < 70; f++)
                    File.WriteAllText(Path.Combine(dir, $"f_{f:D3}.txt"), "");
            }
            for (int f = 0; f < 100; f++)
                File.WriteAllText(Path.Combine(_testDir, $"root_{f:D3}.txt"), "");

            // 2230 entries + 3 reserved = 2233 inodes needed (exceeds default 2048 per CG)
            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;

            // Must have enough CGs for all inodes
            long totalInodes = (long)sb.NumCylGroups * sb.InodesPerGroup;
            Assert.True(totalInodes >= 2233, $"Need >= 2233 inodes, but only {totalInodes} available ({sb.NumCylGroups} CGs × {sb.InodesPerGroup} inodes/CG)");

            // File size must not exceed expected filesystem size (no silent extension)
            var fi = new FileInfo(_imagePath);
            long expectedSize = sb.TotalBlocks * sb.FSize;
            Assert.Equal(expectedSize, fi.Length);

            // Verify all entries are present
            int totalFound = 0;
            var queue = new Queue<uint>();
            queue.Enqueue(Ufs2Constants.RootInode);
            while (queue.Count > 0)
            {
                var entries = image.ListDirectory(queue.Dequeue());
                foreach (var e in entries)
                {
                    if (e.Name == "." || e.Name == ".." || e.Name == ".snap") continue;
                    if (e.FileType == Ufs2Constants.DtDir)
                        queue.Enqueue(e.Inode);
                    totalFound++;
                }
            }
            Assert.Equal(2230, totalFound);
        }
        [Fact]
        public void CreateImageFromDirectory_DeepDirectoryNesting()
        {
            // Create a deeply nested directory structure (15 levels)
            var path = _testDir;
            for (int i = 1; i <= 15; i++)
            {
                path = Path.Combine(path, $"level{i}");
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, $"file_at_level{i}.txt"), $"Content at level {i}");
            }

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);

            // Navigate all 15 levels and verify file at each level
            var entries = image.ListRoot();
            uint currentDirInode = Ufs2Constants.RootInode;
            for (int i = 1; i <= 15; i++)
            {
                entries = image.ListDirectory(currentDirInode);
                var levelDir = entries.First(e => e.Name == $"level{i}");
                Assert.Equal(Ufs2Constants.DtDir, levelDir.FileType);

                var dirEntries = image.ListDirectory(levelDir.Inode);
                Assert.Contains(dirEntries, e => e.Name == $"file_at_level{i}.txt" && e.FileType == Ufs2Constants.DtReg);

                // Verify file content
                var fileEntry = dirEntries.First(e => e.Name == $"file_at_level{i}.txt");
                string content = System.Text.Encoding.UTF8.GetString(image.ReadFile(fileEntry.Inode));
                Assert.Equal($"Content at level {i}", content);

                currentDirInode = levelDir.Inode;
            }

            // Verify directory count: root + 15 nested directories = 16
            Assert.Equal(16, sb.Directories);
        }

        [Fact]
        public void CreateImageFromDirectory_LargeDirectoryWithManyChildren()
        {
            // Create a single directory with 500 files (enough to need multi-block directory entries)
            string bigDir = Path.Combine(_testDir, "bigdir");
            Directory.CreateDirectory(bigDir);
            for (int i = 0; i < 500; i++)
                File.WriteAllText(Path.Combine(bigDir, $"item_{i:D04}.txt"), $"data_{i}");

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);

            // Find bigdir
            var rootEntries = image.ListRoot();
            var bigDirEntry = rootEntries.First(e => e.Name == "bigdir");
            Assert.Equal(Ufs2Constants.DtDir, bigDirEntry.FileType);

            // Verify all 500 files are listed
            var bigDirEntries = image.ListDirectory(bigDirEntry.Inode);
            var fileEntries = bigDirEntries.Where(e => e.FileType == Ufs2Constants.DtReg).ToList();
            Assert.Equal(500, fileEntries.Count);

            // Verify all filenames and content
            for (int i = 0; i < 500; i++)
            {
                string expectedName = $"item_{i:D04}.txt";
                var entry = fileEntries.First(e => e.Name == expectedName);
                byte[] data = image.ReadFile(entry.Inode);
                Assert.Equal($"data_{i}", System.Text.Encoding.UTF8.GetString(data));
            }

            // Verify directory "." and ".." entries
            Assert.Contains(bigDirEntries, e => e.Name == "." && e.Inode == bigDirEntry.Inode);
            Assert.Contains(bigDirEntries, e => e.Name == "..");
        }

        [Fact]
        public void CreateImageFromDirectory_LargeFilesWithIndirectBlocks()
        {
            // Create a file large enough to require single-indirect block pointers.
            // NDirect = 12 direct blocks × 32768 bytes/block = 393216 bytes.
            // A ~500 KB file needs > 12 blocks, requiring an indirect block.
            byte[] largeData = new byte[500_000];
            new Random(123).NextBytes(largeData);
            File.WriteAllBytes(Path.Combine(_testDir, "large.bin"), largeData);

            // Also add a small file for comparison
            File.WriteAllText(Path.Combine(_testDir, "small.txt"), "hello");

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);

            // Verify large file content
            var rootEntries = image.ListRoot();
            var largeEntry = rootEntries.First(e => e.Name == "large.bin");
            byte[] readData = image.ReadFile(largeEntry.Inode);
            Assert.Equal(largeData.Length, readData.Length);
            Assert.Equal(largeData, readData);

            // Verify small file content
            var smallEntry = rootEntries.First(e => e.Name == "small.txt");
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(image.ReadFile(smallEntry.Inode)));

            // Verify inode has indirect block pointer set
            var inode = image.ReadInode(largeEntry.Inode);
            Assert.True(inode.IndirectBlocks[0] != 0, "Large file should use single-indirect block");
        }

        [Fact]
        public void CreateImageFromDirectory_MixedLargeTree()
        {
            // Create a realistic mixed tree: multiple directories, subdirectories,
            // and files of varying sizes.
            for (int d = 0; d < 10; d++)
            {
                string dir = Path.Combine(_testDir, $"project_{d}");
                Directory.CreateDirectory(dir);

                // Each project has a source dir and a docs dir
                string srcDir = Path.Combine(dir, "src");
                string docsDir = Path.Combine(dir, "docs");
                Directory.CreateDirectory(srcDir);
                Directory.CreateDirectory(docsDir);

                // Source files (small)
                for (int f = 0; f < 20; f++)
                    File.WriteAllText(Path.Combine(srcDir, $"module_{f}.cs"), $"// Module {f}\nclass M{f} {{}}");

                // Docs files (medium)
                for (int f = 0; f < 5; f++)
                    File.WriteAllBytes(Path.Combine(docsDir, $"doc_{f}.pdf"), new byte[10_000]);
            }

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);

            // Walk entire tree and count entries
            int totalFiles = 0;
            int totalDirs = 0;
            var queue = new Queue<uint>();
            queue.Enqueue(Ufs2Constants.RootInode);
            while (queue.Count > 0)
            {
                var entries = image.ListDirectory(queue.Dequeue());
                foreach (var e in entries)
                {
                    if (e.Name == "." || e.Name == ".." || e.Name == ".snap") continue;
                    if (e.FileType == Ufs2Constants.DtDir)
                    {
                        totalDirs++;
                        queue.Enqueue(e.Inode);
                    }
                    else if (e.FileType == Ufs2Constants.DtReg)
                    {
                        totalFiles++;
                    }
                }
            }

            // 10 projects × (1 project dir + 1 src dir + 1 docs dir) = 30 directories
            Assert.Equal(30, totalDirs);
            // 10 projects × (20 src files + 5 doc files) = 250 files
            Assert.Equal(250, totalFiles);
            // Total directories including root = 31
            Assert.Equal(31, sb.Directories);
        }

        [Fact]
        public void CreateImageFromDirectory_BitmapAndSummaryConsistent_AfterLargePopulate()
        {
            // Create a tree with enough files to span multiple CGs and verify
            // that CG bitmap and summary counts are consistent after population.
            for (int d = 0; d < 5; d++)
            {
                string dir = Path.Combine(_testDir, $"dir_{d}");
                Directory.CreateDirectory(dir);
                for (int f = 0; f < 100; f++)
                    File.WriteAllBytes(Path.Combine(dir, $"f_{f:D3}.bin"), new byte[500]);
            }

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            int fragsPerBlock = sb.BSize / sb.FSize;

            using var fs = new FileStream(_imagePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            long sumFreeBlocks = 0, sumFreeFrags = 0;
            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                long cgStart = (long)cg * sb.CylGroupSize * sb.FSize;
                long cgHdr = cgStart + (long)sb.CblkNo * sb.FSize;

                fs.Position = cgHdr + 0x18;
                reader.ReadInt32(); // cs_ndir
                int csNbfree = reader.ReadInt32();
                reader.ReadInt32(); // cs_nifree
                int csNffree = reader.ReadInt32();

                fs.Position = cgHdr + 0x60;
                int freeoff = reader.ReadInt32();

                int usableFrags = sb.CylGroupSize;
                if (cg == sb.NumCylGroups - 1)
                    usableFrags = (int)(sb.TotalBlocks - (long)cg * sb.CylGroupSize);

                fs.Position = cgHdr + freeoff;
                byte[] bitmap = reader.ReadBytes((sb.CylGroupSize + 7) / 8);

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

            Assert.Equal(sb.FreeBlocks, sumFreeBlocks);
            Assert.Equal(sb.FreeFragments, sumFreeFrags);
        }

        // -----------------------------------------------------------------------
        // FreeBSD makefs(8) compliance: Soft updates behavior
        // -----------------------------------------------------------------------

        [Fact]
        public void CreateImageFromDirectory_DisablesSoftUpdates_ByDefault()
        {
            // FreeBSD makefs default: softupdates=0
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);

            // Soft updates flags should NOT be set by default (FreeBSD makefs default)
            Assert.Equal(0, sb.Flags & Ufs2Constants.FsDosoftdep);
            Assert.Equal(0, sb.Flags & Ufs2Constants.FsSuj);
        }

        [Fact]
        public void CreateImageFromDirectory_EnablesSoftUpdates_WhenExplicitlySet()
        {
            // FreeBSD makefs -o softupdates=1: explicitly enable soft updates
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.SoftUpdates = true;
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);

            // Soft updates should be enabled when explicitly requested (softupdates=1)
            Assert.NotEqual(0, sb.Flags & Ufs2Constants.FsDosoftdep);
        }

        [Fact]
        public void CreateImageFromDirectory_RestoresOriginalSettings_AfterCreation()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.SoftUpdates = true;
            creator.SoftUpdatesJournal = true;
            creator.CreateImageFromDirectory(_imagePath, _testDir);

            // After creating the image, the original settings should be restored
            Assert.True(creator.SoftUpdates, "SoftUpdates should be restored after image creation");
            Assert.True(creator.SoftUpdatesJournal, "SoftUpdatesJournal should be restored after image creation");
        }

        // -----------------------------------------------------------------------
        // FreeBSD makefs(8) compliance: MakeFsImage — separate from newfs -D
        // -----------------------------------------------------------------------

        [Fact]
        public void MakeFsImage_DefaultVersion1_CreatesUFS1()
        {
            // FreeBSD makefs default version is 1 (FFS/UFS1)
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.FilesystemFormat = 1; // makefs default
            creator.MakeFsImage(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);
            Assert.True(sb.IsUfs1, "makefs default should create UFS1 filesystem");
        }

        [Fact]
        public void MakeFsImage_Version2_CreatesUFS2()
        {
            // makefs -o version=2 — creates UFS2
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.FilesystemFormat = 2; // -o version=2
            creator.MakeFsImage(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);
            Assert.True(sb.IsUfs2, "version=2 should create UFS2 filesystem");
        }

        [Fact]
        public void MakeFsImage_WithFreeBlocks_IncreasesImageSize()
        {
            // makefs -b <bytes> — add raw bytes of free space (matching FreeBSD ffs_validate)
            // Use enough data to exceed minimum size, and enough freeblocks to matter
            File.WriteAllBytes(Path.Combine(_testDir, "data.bin"), new byte[256 * 1024]);

            var creator = new Ufs2ImageCreator();
            long sizeWithout = creator.MakeFsImage(_imagePath, _testDir);

            if (File.Exists(_imagePath)) File.Delete(_imagePath);

            // freeblocks is in raw bytes (FreeBSD: fsopts->size += fsopts->freeblocks)
            long sizeWith = creator.MakeFsImage(_imagePath, _testDir,
                freeblocks: 1024 * 1024); // 1 MB of free space

            Assert.True(sizeWith > sizeWithout,
                $"Image with free blocks ({sizeWith}) should be larger than without ({sizeWithout})");
        }

        [Fact]
        public void MakeFsImage_WithFreeBlocksPercent_IncreasesImageSize()
        {
            // makefs -b 50% — ensure at least 50% additional free blocks
            // Use enough data to exceed the minimum filesystem size so the percentage matters
            File.WriteAllBytes(Path.Combine(_testDir, "data.bin"), new byte[256 * 1024]);

            var creator = new Ufs2ImageCreator();
            long sizeWithout = creator.MakeFsImage(_imagePath, _testDir);

            if (File.Exists(_imagePath)) File.Delete(_imagePath);

            long sizeWith = creator.MakeFsImage(_imagePath, _testDir,
                freeblockpc: 50);

            Assert.True(sizeWith > sizeWithout,
                $"Image with 50% free blocks ({sizeWith}) should be larger than without ({sizeWithout})");
        }

        [Fact]
        public void MakeFsImage_WithMinimumSize_EnforcesMinimum()
        {
            // makefs -M 64m — minimum size 64 MB
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            long minimumSize = 64 * 1024 * 1024; // 64 MB

            long resultSize = creator.MakeFsImage(_imagePath, _testDir,
                minimumSize: minimumSize);

            Assert.True(resultSize >= minimumSize,
                $"Image size ({resultSize}) should be at least minimum ({minimumSize})");
        }

        [Fact]
        public void MakeFsImage_WithMaximumSize_ThrowsIfTooSmall()
        {
            // makefs -m 1024 — maximum size 1024 bytes (too small for any content)
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            long maximumSize = 1024; // Way too small

            Assert.Throws<ArgumentException>(() =>
                creator.MakeFsImage(_imagePath, _testDir,
                    maximumSize: maximumSize));
        }

        [Fact]
        public void MakeFsImage_WithExplicitSize_SetsMinAndMax()
        {
            // makefs -s 10m — sets both min and max size
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            long explicitSize = 10 * 1024 * 1024; // 10 MB

            long resultSize = creator.MakeFsImage(_imagePath, _testDir,
                imageSize: explicitSize);

            // -s sets both minsize and maxsize, so result equals explicit size
            Assert.Equal(explicitSize, resultSize);
        }

        [Fact]
        public void MakeFsImage_SoftUpdatesDisabledByDefault()
        {
            // FreeBSD makefs default: softupdates=0
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.MakeFsImage(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.Equal(0, sb.Flags & Ufs2Constants.FsDosoftdep);
        }

        [Fact]
        public void MakeFsImage_SoftUpdatesEnabled_WhenRequested()
        {
            // makefs -o softupdates=1
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.SoftUpdates = true;
            creator.MakeFsImage(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.NotEqual(0, sb.Flags & Ufs2Constants.FsDosoftdep);
        }

        [Fact]
        public void MakeFsImage_FreeBSDAccurateOverhead_SmallerThanNewfsDHeuristic()
        {
            // MakeFsImage (FreeBSD ffs_validate) should produce a tighter image
            // than CreateImageFromDirectory (newfs -D 10%+10MB heuristic)
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator1 = new Ufs2ImageCreator();
            long makeFsSize = creator1.MakeFsImage(_imagePath, _testDir);

            if (File.Exists(_imagePath)) File.Delete(_imagePath);

            var creator2 = new Ufs2ImageCreator();
            long newFsDSize = creator2.CreateImageFromDirectory(_imagePath, _testDir);

            // makefs uses precise structural overhead; newfs -D uses 10%+10MB heuristic
            // For small directories, makefs should produce a smaller image
            Assert.True(makeFsSize <= newFsDSize,
                $"MakeFsImage ({makeFsSize}) should be <= CreateImageFromDirectory ({newFsDSize})");
        }

        [Fact]
        public void MakeFsImage_RestoresBytesPerInode_AfterCreation()
        {
            // MakeFsImage auto-calculates density; verify it's restored afterward
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            int origBpi = creator.BytesPerInode;
            creator.MakeFsImage(_imagePath, _testDir);

            Assert.Equal(origBpi, creator.BytesPerInode);
        }

        [Fact]
        public void MakeFsImage_RestoresBlocksPerCylGroup_AfterCreation()
        {
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            int origBpcg = creator.BlocksPerCylGroup;
            creator.MakeFsImage(_imagePath, _testDir);

            Assert.Equal(origBpcg, creator.BlocksPerCylGroup);
        }

        [Fact]
        public void MakeFsImage_AutoMaxBpcg_UsesLargeBlocksPerCylinderGroup_ForLargeDataWithFewInodes()
        {
            // Large data with very few files should use larger blocks-per-CG to avoid
            // excessive CG metadata overhead when maxbpcg is not specified.
            File.WriteAllBytes(Path.Combine(_testDir, "large.bin"), new byte[64 * 1024 * 1024]);

            var creator = new Ufs2ImageCreator();
            creator.FilesystemFormat = 2;
            creator.MakeFsImage(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            int fragsPerBlock = sb.BSize / sb.FSize;
            int blocksPerCg = sb.CylGroupSize / fragsPerBlock;

            Assert.True(blocksPerCg > sb.InodesPerGroup,
                $"Auto maxbpcg should create larger CG data area (blocks/CG={blocksPerCg}) than inode-count baseline (ipg={sb.InodesPerGroup}).");
        }

        [Fact]
        public void MakeFsImage_WithFreeFiles_IncreasesInodes()
        {
            // makefs -f 100 — ensure at least 100 extra free inodes
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.MakeFsImage(_imagePath, _testDir, freefiles: 100);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.IsValid);
            // Should have at least 100 free inodes
            Assert.True(sb.FreeInodes >= 100,
                $"Expected at least 100 free inodes, got {sb.FreeInodes}");
        }

        [Fact]
        public void MakeFsImage_MinInodes_UsesFewerInodesThanDefault()
        {
            // When no free space requested, makefs uses minimum inode count
            File.WriteAllText(Path.Combine(_testDir, "test.txt"), "data");

            var creator = new Ufs2ImageCreator();
            creator.MakeFsImage(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            // With min_inodes, total inodes should be much less than default 2048
            Assert.True(sb.InodesPerGroup < Ufs2Constants.DefaultInodesPerGroup,
                $"min_inodes should use fewer than default {Ufs2Constants.DefaultInodesPerGroup}, " +
                $"got {sb.InodesPerGroup}");
        }

        [Fact]
        public void MakeFsImage_ManySmallFiles_DoesNotOverflowCylinderGroups()
        {
            // Regression test: directories with many small files should produce
            // an image large enough to hold all data. Previously the size calculator
            // underestimated because it used fragment-level rounding while the
            // actual allocator uses full-block allocation.
            var subDir = Path.Combine(_testDir, "data");
            Directory.CreateDirectory(subDir);
            for (int i = 0; i < 500; i++)
                File.WriteAllBytes(Path.Combine(subDir, $"file_{i:D4}.dat"), new byte[100]);

            var creator = new Ufs2ImageCreator();
            creator.FilesystemFormat = 2;
            // This should NOT throw "Image is too small: no more cylinder groups available for data."
            creator.MakeFsImage(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.FreeBlocks > 0,
                "Image should have free blocks remaining after population");
        }

        [Fact]
        public void MakeFsImage_LargeDirectoryTree_ProducesUsableImage()
        {
            // Simulate a moderately large directory tree (like a game install)
            // with many files of varying sizes across multiple subdirectories.
            for (int d = 0; d < 5; d++)
            {
                var dir = Path.Combine(_testDir, $"dir_{d}");
                Directory.CreateDirectory(dir);
                for (int f = 0; f < 200; f++)
                {
                    int size = (f % 10 == 0) ? 100_000 : 500; // mix of large and small
                    File.WriteAllBytes(Path.Combine(dir, $"file_{f:D4}.dat"), new byte[size]);
                }
            }

            var creator = new Ufs2ImageCreator();
            creator.FilesystemFormat = 2;
            long imageSize = creator.MakeFsImage(_imagePath, _testDir);

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            var sb = image.Superblock;
            Assert.True(sb.FreeBlocks > 0,
                "Image should have free blocks remaining after population");
            Assert.True(imageSize > 0, "Image size should be positive");
        }
    }
}
