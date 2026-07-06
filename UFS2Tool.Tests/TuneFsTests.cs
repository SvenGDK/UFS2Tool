// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests for the tunefs command — validates superblock modifications
    /// on existing UFS2 filesystem images, matching FreeBSD tunefs(8) behavior.
    /// </summary>
    public class TuneFsTests : IDisposable
    {
        private readonly string _imagePath;

        public TuneFsTests()
        {
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2tunefs_{Guid.NewGuid():N}.img");

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
        /// Verify that changing the volume label persists in the superblock.
        /// </summary>
        [Fact]
        public void TuneFs_ChangeVolumeLabel()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.VolumeName = "TESTLABEL";
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal("TESTLABEL", verify.Superblock.VolumeName);
        }

        /// <summary>
        /// Verify that enabling soft updates sets the FS_DOSOFTDEP flag.
        /// </summary>
        [Fact]
        public void TuneFs_EnableSoftUpdates()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                Assert.Equal(0, image.Superblock.Flags & Ufs2Constants.FsDosoftdep);
                image.Superblock.Flags |= Ufs2Constants.FsDosoftdep;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsDosoftdep);
        }

        /// <summary>
        /// Verify that disabling soft updates clears the FS_DOSOFTDEP flag.
        /// </summary>
        [Fact]
        public void TuneFs_DisableSoftUpdates()
        {
            // First enable
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags |= Ufs2Constants.FsDosoftdep;
                image.WriteSuperblock();
            }

            // Then disable
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags &= ~Ufs2Constants.FsDosoftdep;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(0, verify.Superblock.Flags & Ufs2Constants.FsDosoftdep);
        }

        /// <summary>
        /// Verify that enabling soft updates journaling sets both FS_DOSOFTDEP and FS_SUJ.
        /// </summary>
        [Fact]
        public void TuneFs_EnableSoftUpdatesJournaling()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags |= Ufs2Constants.FsDosoftdep | Ufs2Constants.FsSuj;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsDosoftdep);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsSuj);
        }

        /// <summary>
        /// Verify that enabling TRIM sets the FS_TRIM flag.
        /// </summary>
        [Fact]
        public void TuneFs_EnableTrim()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags |= Ufs2Constants.FsTrim;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsTrim);
        }

        /// <summary>
        /// Verify that enabling gjournal sets the FS_GJOURNAL flag.
        /// </summary>
        [Fact]
        public void TuneFs_EnableGjournal()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags |= Ufs2Constants.FsGjournal;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsGjournal);
        }

        /// <summary>
        /// Verify that enabling multilabel sets the FS_MULTILABEL flag.
        /// </summary>
        [Fact]
        public void TuneFs_EnableMultilabel()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags |= Ufs2Constants.FsMultilabel;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsMultilabel);
        }

        /// <summary>
        /// Verify that POSIX.1e ACLs can be enabled and disabled.
        /// </summary>
        [Fact]
        public void TuneFs_TogglePosixAcls()
        {
            // Enable
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags |= Ufs2Constants.FsAcls;
                image.WriteSuperblock();
            }

            using (var verify = new Ufs2Image(_imagePath, readOnly: true))
            {
                Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsAcls);
            }

            // Disable
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags &= ~Ufs2Constants.FsAcls;
                image.WriteSuperblock();
            }

            using var verify2 = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(0, verify2.Superblock.Flags & Ufs2Constants.FsAcls);
        }

        /// <summary>
        /// Verify that NFSv4 ACLs can be enabled and disabled.
        /// </summary>
        [Fact]
        public void TuneFs_ToggleNfsv4Acls()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags |= Ufs2Constants.FsNfs4Acls;
                image.WriteSuperblock();
            }

            using (var verify = new Ufs2Image(_imagePath, readOnly: true))
            {
                Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsNfs4Acls);
            }

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.Flags &= ~Ufs2Constants.FsNfs4Acls;
                image.WriteSuperblock();
            }

            using var verify2 = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(0, verify2.Superblock.Flags & Ufs2Constants.FsNfs4Acls);
        }

        /// <summary>
        /// Verify that changing minfree percentage persists.
        /// </summary>
        [Fact]
        public void TuneFs_ChangeMinfree()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                Assert.Equal(8, image.Superblock.MinFreePercent);
                image.Superblock.MinFreePercent = 5;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(5, verify.Superblock.MinFreePercent);
        }

        /// <summary>
        /// Verify that changing the optimization preference persists.
        /// </summary>
        [Fact]
        public void TuneFs_ChangeOptimization()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                Assert.Equal(Ufs2Constants.FsOptTime, image.Superblock.Optimization);
                image.Superblock.Optimization = Ufs2Constants.FsOptSpace;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(Ufs2Constants.FsOptSpace, verify.Superblock.Optimization);
        }

        /// <summary>
        /// Verify that changing maxbpg persists.
        /// </summary>
        [Fact]
        public void TuneFs_ChangeMaxBpg()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                int original = image.Superblock.MaxBpg;
                image.Superblock.MaxBpg = 512;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(512, verify.Superblock.MaxBpg);
        }

        /// <summary>
        /// Verify that changing average file size persists.
        /// </summary>
        [Fact]
        public void TuneFs_ChangeAvgFileSize()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.AvgFileSize = 32768;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(32768, verify.Superblock.AvgFileSize);
        }

        /// <summary>
        /// Verify that changing average files per directory persists.
        /// </summary>
        [Fact]
        public void TuneFs_ChangeAvgFilesPerDir()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.AvgFilesPerDir = 128;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(128, verify.Superblock.AvgFilesPerDir);
        }

        /// <summary>
        /// Verify that changing metaspace persists.
        /// </summary>
        [Fact]
        public void TuneFs_ChangeMetaSpace()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.MetaSpace = 128;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal(128, verify.Superblock.MetaSpace);
        }

        /// <summary>
        /// Verify that multiple flags can be changed simultaneously.
        /// </summary>
        [Fact]
        public void TuneFs_MultipleChanges()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.VolumeName = "MULTI";
                image.Superblock.Flags |= Ufs2Constants.FsDosoftdep | Ufs2Constants.FsTrim;
                image.Superblock.MinFreePercent = 3;
                image.Superblock.Optimization = Ufs2Constants.FsOptSpace;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Equal("MULTI", verify.Superblock.VolumeName);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsDosoftdep);
            Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsTrim);
            Assert.Equal(3, verify.Superblock.MinFreePercent);
            Assert.Equal(Ufs2Constants.FsOptSpace, verify.Superblock.Optimization);
        }

        /// <summary>
        /// Verify that WriteSuperblockToAll writes backup superblocks in multi-CG images.
        /// </summary>
        [Fact]
        public void TuneFs_WriteSuperblockToAll_UpdatesBackups()
        {
            // Create a larger image with multiple cylinder groups
            string largePath = Path.Combine(Path.GetTempPath(), $"ufs2tunefs_large_{Guid.NewGuid():N}.img");
            try
            {
                var creator = new Ufs2ImageCreator();
                creator.CreateImage(largePath, 256 * 1024 * 1024);

                // Modify and write to all
                using (var image = new Ufs2Image(largePath, readOnly: false))
                {
                    Assert.True(image.Superblock.NumCylGroups > 1,
                        "Need multiple CGs to test backup superblock writing");

                    image.Superblock.VolumeName = "BACKUP";
                    image.WriteSuperblockToAll();
                }

                // Read back and verify primary
                using var fs = new FileStream(largePath, FileMode.Open, FileAccess.Read);
                using var reader = new BinaryReader(fs);

                fs.Position = Ufs2Constants.SuperblockOffset64K;
                var primary = Ufs2Superblock.ReadFrom(reader);
                Assert.Equal("BACKUP", primary.VolumeName);

                // Verify at least one backup superblock
                if (primary.NumCylGroups > 1)
                {
                    long cg1Start = (long)primary.CylGroupSize * primary.FSize;
                    long backupOffset = cg1Start + (long)primary.SuperblockLocation * primary.FSize;
                    fs.Position = backupOffset;
                    var backup = Ufs2Superblock.ReadFrom(reader);
                    Assert.Equal("BACKUP", backup.VolumeName);
                }
            }
            finally
            {
                if (File.Exists(largePath))
                    File.Delete(largePath);
            }
        }

        /// <summary>
        /// Verify that opening in read-only mode prevents WriteSuperblock.
        /// </summary>
        [Fact]
        public void TuneFs_ReadOnlyPreventsWrite()
        {
            using var image = new Ufs2Image(_imagePath, readOnly: true);
            Assert.Throws<InvalidOperationException>(() => image.WriteSuperblock());
        }

        /// <summary>
        /// Verify tunefs works with UFS1 images too.
        /// </summary>
        [Fact]
        public void TuneFs_Ufs1Image()
        {
            string ufs1Path = Path.Combine(Path.GetTempPath(), $"ufs1tunefs_{Guid.NewGuid():N}.img");
            try
            {
                var creator = new Ufs2ImageCreator { FilesystemFormat = 1 };
                creator.CreateImage(ufs1Path, 64 * 1024 * 1024);

                using (var image = new Ufs2Image(ufs1Path, readOnly: false))
                {
                    Assert.True(image.Superblock.IsUfs1);
                    image.Superblock.VolumeName = "UFS1VOL";
                    image.Superblock.Flags |= Ufs2Constants.FsDosoftdep;
                    image.WriteSuperblock();
                }

                using var verify = new Ufs2Image(ufs1Path, readOnly: true);
                Assert.Equal("UFS1VOL", verify.Superblock.VolumeName);
                Assert.NotEqual(0, verify.Superblock.Flags & Ufs2Constants.FsDosoftdep);
            }
            finally
            {
                if (File.Exists(ufs1Path))
                    File.Delete(ufs1Path);
            }
        }

        /// <summary>
        /// Verify that the filesystem remains valid (correct magic number) after tunefs operations.
        /// </summary>
        [Fact]
        public void TuneFs_PreservesMagicNumber()
        {
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Superblock.VolumeName = "MAGIC";
                image.Superblock.Flags |= Ufs2Constants.FsDosoftdep | Ufs2Constants.FsSuj | Ufs2Constants.FsTrim;
                image.Superblock.MinFreePercent = 10;
                image.Superblock.Optimization = Ufs2Constants.FsOptSpace;
                image.Superblock.AvgFileSize = 8192;
                image.Superblock.AvgFilesPerDir = 32;
                image.WriteSuperblock();
            }

            using var verify = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(verify.Superblock.IsValid);
            Assert.Equal(Ufs2Constants.Ufs2Magic, verify.Superblock.Magic);
        }
    }
}
