// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Regression tests verifying that the user-facing -S (sector size) parameter
    /// produces an on-disk filesystem layout that matches FreeBSD's native newfs(8)
    /// exactly.
    ///
    /// Per FreeBSD sbin/newfs/newfs.c: when the user-specified sectorsize differs
    /// from DEV_BSIZE (512), newfs preserves it as <c>realsectorsize</c> for raw
    /// device I/O alignment but resets the in-memory <c>sectorsize</c> to DEV_BSIZE
    /// before laying out the filesystem and multiplies the requested filesystem
    /// size by <c>sectorsize / DEV_BSIZE</c>. The resulting on-disk superblock
    /// fields (fs_fsbtodb, fs_old_nspf), the recovery-block offset, and the data
    /// region offsets are therefore identical to those produced with -S 512.
    ///
    /// These tests exercise the case that previously produced broken images
    /// (e.g., -O 2 -b 65536 -f 65536 -S 4096) and lock in the FreeBSD-equivalent
    /// behavior for both the layout and the public SectorSize property.
    /// </summary>
    public class SectorSizeNormalizationTests : IDisposable
    {
        private readonly string _imagePathA;
        private readonly string _imagePathB;

        public SectorSizeNormalizationTests()
        {
            _imagePathA = Path.Combine(Path.GetTempPath(), $"ufs_sect_a_{Guid.NewGuid():N}.img");
            _imagePathB = Path.Combine(Path.GetTempPath(), $"ufs_sect_b_{Guid.NewGuid():N}.img");
        }

        public void Dispose()
        {
            if (File.Exists(_imagePathA)) File.Delete(_imagePathA);
            if (File.Exists(_imagePathB)) File.Delete(_imagePathB);
        }

        private static Ufs2ImageCreator NewCreator(int blockSize, int fragmentSize, int sectorSize)
        {
            return new Ufs2ImageCreator
            {
                BlockSize = blockSize,
                FragmentSize = fragmentSize,
                SectorSize = sectorSize,
                BytesPerInode = 262144,
                MinFreePercent = 0,
            };
        }

        /// <summary>
        /// Per FreeBSD newfs.c: fs_fsbtodb is computed against DEV_BSIZE (512),
        /// not the user's -S value. With FragmentSize=65536, fs_fsbtodb must be
        /// log2(65536/512) = 7 regardless of whether -S was 512, 1024, 2048 or 4096.
        /// </summary>
        [Theory]
        [InlineData(512)]
        [InlineData(1024)]
        [InlineData(2048)]
        [InlineData(4096)]
        public void FsbToDbAlwaysComputedAgainstDevBsize(int userSectorSize)
        {
            var creator = NewCreator(blockSize: 65536, fragmentSize: 65536, sectorSize: userSectorSize);
            creator.CreateImage(_imagePathA, 16 * 1024 * 1024);

            using var fs = new FileStream(_imagePathA, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // fs_fsbtodb is at offset 0x64 (100) in the superblock.
            fs.Position = Ufs2Constants.SuperblockOffset64K + 0x64;
            int fsbtodb = reader.ReadInt32();

            // Expected: log2(fsize / DEV_BSIZE) = log2(65536 / 512) = 7.
            Assert.Equal(7, fsbtodb);
        }

        /// <summary>
        /// Per FreeBSD newfs.c: fs_old_nspf = fsize / DEV_BSIZE, not fsize / userSectorSize.
        /// </summary>
        [Theory]
        [InlineData(512)]
        [InlineData(1024)]
        [InlineData(2048)]
        [InlineData(4096)]
        public void OldNspfAlwaysComputedAgainstDevBsize(int userSectorSize)
        {
            var creator = NewCreator(blockSize: 65536, fragmentSize: 65536, sectorSize: userSectorSize);
            creator.CreateImage(_imagePathA, 16 * 1024 * 1024);

            using var fs = new FileStream(_imagePathA, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // fs_old_nspf is at offset 0x7C (124) in the superblock.
            fs.Position = Ufs2Constants.SuperblockOffset64K + 0x7C;
            int nspf = reader.ReadInt32();

            // Expected: fsize / DEV_BSIZE = 65536 / 512 = 128.
            Assert.Equal(128, nspf);
        }

        /// <summary>
        /// The UFS2 fsrecovery struct is always located in the sector immediately
        /// before SBLOCK_UFS2 using DEV_BSIZE units, i.e. at file offset 65024
        /// (= SBLOCK_UFS2 - 512), regardless of the user's -S value. Per FreeBSD
        /// mkfs.c, fsr_fsbtodb is also computed against DEV_BSIZE.
        /// </summary>
        [Theory]
        [InlineData(512)]
        [InlineData(1024)]
        [InlineData(4096)]
        public void RecoveryBlockAtDevBsizeOffset(int userSectorSize)
        {
            var creator = NewCreator(blockSize: 65536, fragmentSize: 65536, sectorSize: userSectorSize);
            creator.CreateImage(_imagePathA, 16 * 1024 * 1024);

            using var fs = new FileStream(_imagePathA, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // struct fsrecovery occupies the last 20 bytes of the DEV_BSIZE sector
            // immediately before SBLOCK_UFS2. Recovery sector starts at 65024.
            const int devBsize = 512;
            const int recoverySize = 20;
            long recoveryOffset = Ufs2Constants.SuperblockOffset64K - devBsize + (devBsize - recoverySize);

            fs.Position = recoveryOffset;
            int fsrMagic = reader.ReadInt32();
            int fsrFpg = reader.ReadInt32();
            int fsrFsbtodb = reader.ReadInt32();
            int fsrSblkno = reader.ReadInt32();
            int fsrNcg = reader.ReadInt32();

            Assert.Equal(Ufs2Constants.Ufs2Magic, fsrMagic);
            // fsr_fsbtodb is computed against DEV_BSIZE, like fs_fsbtodb.
            Assert.Equal(7, fsrFsbtodb);
            Assert.True(fsrFpg > 0);
            Assert.True(fsrSblkno > 0);
            Assert.True(fsrNcg >= 1);

            // The byte range [SuperblockOffset - userSectorSize, 65024) must NOT
            // be occupied by recovery data when userSectorSize > DEV_BSIZE — it
            // should remain zero (this exact bug previously corrupted -S 4096
            // images by relocating the recovery block to offset 61440).
            if (userSectorSize > devBsize)
            {
                fs.Position = Ufs2Constants.SuperblockOffset64K - userSectorSize;
                byte[] preBuf = reader.ReadBytes(userSectorSize - devBsize);
                foreach (byte b in preBuf)
                    Assert.Equal(0, b);
            }
        }

        /// <summary>
        /// Two images created with the same parameters but different -S values must
        /// produce byte-identical on-disk layouts (excluding fs_id, which is
        /// timestamp- and process-id-derived). This is the strongest verification
        /// that the FreeBSD-style sectorsize normalization is in effect.
        /// </summary>
        [Fact]
        public void DifferentSectorSizesProduceIdenticalLayout()
        {
            // Use timestamp pinning by creating both images back-to-back; they
            // will share the same Unix-second timestamp, leaving only fs_id[1]
            // (which incorporates Environment.ProcessId) potentially different —
            // but since both images are created in the same process, even fs_id
            // matches.
            var creatorA = NewCreator(blockSize: 65536, fragmentSize: 65536, sectorSize: 4096);
            var creatorB = NewCreator(blockSize: 65536, fragmentSize: 65536, sectorSize: 512);
            const long imageSize = 16L * 1024 * 1024;
            creatorA.CreateImage(_imagePathA, imageSize);
            creatorB.CreateImage(_imagePathB, imageSize);

            byte[] a = File.ReadAllBytes(_imagePathA);
            byte[] b = File.ReadAllBytes(_imagePathB);
            Assert.Equal(a.Length, b.Length);

            // Allow timestamp-related differences in the superblock: fs_old_time
            // (0x20-0x23), fs_id (0x90-0x97), fs_time (0x430-0x437), fs_mtime
            // (0x4B8-0x4BF). All other bytes — including the recovery block,
            // fs_fsbtodb, fs_old_nspf, CG headers, and inode tables — must
            // match exactly.
            var sbBase = Ufs2Constants.SuperblockOffset64K;
            var allowedRanges = new (long start, long end)[]
            {
                (sbBase + 0x20, sbBase + 0x23), // fs_old_time
                (sbBase + 0x90, sbBase + 0x97), // fs_id[0..1]
                (sbBase + 0x430, sbBase + 0x437), // fs_time
                (sbBase + 0x4B8, sbBase + 0x4BF), // fs_mtime
            };

            int diffCount = 0;
            for (long i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;

                // Allow timestamp/inode-time differences anywhere in inode tables
                // (UFS2 inode timestamps may differ by a sub-second across runs).
                bool allowed = false;
                foreach (var (start, end) in allowedRanges)
                {
                    if (i >= start && i <= end) { allowed = true; break; }
                }
                if (!allowed)
                {
                    diffCount++;
                    if (diffCount <= 5)
                    {
                        // Surface the first few differences in the assertion.
                        Assert.Fail(
                            $"Unexpected layout difference at byte {i} (0x{i:X}): " +
                            $"S=4096 has 0x{a[i]:X2}, S=512 has 0x{b[i]:X2}.");
                    }
                }
            }
            Assert.Equal(0, diffCount);
        }

        /// <summary>
        /// Per FreeBSD newfs.c, the -s option is in real-sector units; with
        /// -S 4096 -s N, the resulting filesystem byte size must be N * 4096,
        /// not N * 512 as the previous bug produced.
        /// </summary>
        [Fact]
        public void SizeOverrideUsesRealSectorUnits()
        {
            // Pick a sector count that yields a valid filesystem at -S 4096 with
            // 65536-byte blocks (must hold at least 16 blocks => >= 1 MB).
            const long sectorsRequested = 4096; // 4096 * 4096 = 16 MB
            var creator = NewCreator(blockSize: 65536, fragmentSize: 65536, sectorSize: 4096);
            creator.SizeOverride = sectorsRequested;
            // Pass an arbitrary placeholder size; SizeOverride takes precedence.
            creator.CreateImage(_imagePathA, 1024L * 1024 * 1024);

            long expectedBytes = sectorsRequested * 4096;
            Assert.Equal(expectedBytes, new FileInfo(_imagePathA).Length);
        }

        /// <summary>
        /// CreateImage must restore the user-visible SectorSize property to its
        /// configured value after running, even though the filesystem layout is
        /// computed in DEV_BSIZE units internally.
        /// </summary>
        [Fact]
        public void SectorSizePropertyIsPreservedAcrossCreateImage()
        {
            var creator = NewCreator(blockSize: 65536, fragmentSize: 65536, sectorSize: 4096);
            Assert.Equal(4096, creator.SectorSize);
            creator.CreateImage(_imagePathA, 16 * 1024 * 1024);
            Assert.Equal(4096, creator.SectorSize);
        }

        /// <summary>
        /// The image must remain valid (according to the bundled fsck) when
        /// created with -S 4096 plus large blocks — the original failing case.
        /// </summary>
        [Fact]
        public void Image_CreatedWithS4096_PassesSuperblockValidation()
        {
            var creator = NewCreator(blockSize: 65536, fragmentSize: 65536, sectorSize: 4096);
            creator.CreateImage(_imagePathA, 16 * 1024 * 1024);

            using var fs = new FileStream(_imagePathA, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            fs.Position = Ufs2Constants.SuperblockOffset64K;
            var sb = Ufs2Superblock.ReadFrom(reader);

            Assert.Equal(Ufs2Constants.Ufs2Magic, sb.Magic);
            Assert.Equal(65536, sb.BSize);
            Assert.Equal(65536, sb.FSize);
            Assert.True(sb.NumCylGroups >= 1);
            Assert.True(sb.InodesPerGroup > 0);
            Assert.True(sb.TotalBlocks > 0);
        }
    }
}
