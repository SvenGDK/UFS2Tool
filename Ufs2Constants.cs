// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool
{
    /// <summary>
    /// UFS filesystem constants derived from FreeBSD sys/ufs/ffs/fs.h
    /// Supports both UFS1 and UFS2 formats.
    /// </summary>
    public static class Ufs2Constants
    {
        // Magic numbers
        public const int Ufs1Magic = 0x00011954;        // UFS1 magic number (FS_UFS1_MAGIC)
        public const int Ufs2Magic = 0x19540119;        // UFS2 magic number (FS_UFS2_MAGIC)
        public const int CgMagic = 0x090255;            // Cylinder group magic (CG_MAGIC)

        public const int SuperblockOffsetEmbed = 0;     // superblock at start (Tiny/Embedded)
        public const int SuperblockOffset8K = 8192;     // SBLOCK_UFS1 superblock offset (8 KB)
        public const int SuperblockOffset64K = 65536;   // SBLOCK_UFS2 superblock offset (64 KB)
        public const int SuperblockOffset256K = 262144; // extreme bootstrap superblock offset (256 KB)
        public const int SuperblockSize = 8192;         // Superblock size (SBLOCKSIZE)
        public const int MaxMntLen = 468;               // Max mount point length (MAXMNTLEN)
        public const int MaxVolLen = 32;                // Max volume name length (MAXVOLLEN)
        public const int NocsptrsSize = 28;             // NOCSPTRS
        public const int FsIdSize = 2;                  // Filesystem ID size
        //public const int MagicSuperblockOffset = 0x55C;

        // Block sizes
        public const int DefaultBlockSize = 32768;      // 32 KB default block size
        public const int DefaultFragSize = 4096;        // 4 KB default fragment size
        public const int DefaultSectorSize = 512;       // 512 bytes sector size
        public const int MaxBSize = 65536;              // MAXBSIZE - maximum block size (64 KB)
        public const int MaxContig = 16;                // FS_MAXCONTIG - max summary size

        // Default newfs parameters (matching FreeBSD defaults)
        public const int DefaultAvgFileSize = 16384;    // Average file size (-g)
        public const int DefaultAvgFilesPerDir = 64;    // Average files per dir (-h)
        public const int DefaultMinFreePercent = 8;     // Minimum free space % (-m)

        // Inode constants
        public const int DefaultInodesPerGroup = 2048;
        public const int Ufs1InodeSize = 128;           // UFS1 dinode size
        public const int Ufs2InodeSize = 256;           // UFS2 dinode size
        public const int RootInode = 2;                 // Root directory inode number (UFS_ROOTINO)

        // Directory constants
        public const int DirBlockSize = 512;            // DIRBLKSIZ — directory entries must not span across this boundary
        public const int DirectoryEntryHeaderSize = 8;  // d_ino(4) + d_reclen(2) + d_type(1) + d_namlen(1)
        public const int DirectoryEntryAlignment = 4;   // 4-byte alignment for both UFS1 and UFS2
        public const int MaxNameLen = 255;

        // File types in directory entries (matching FreeBSD DT_* constants)
        public const byte DtUnknown = 0;
        public const byte DtFifo = 1;
        public const byte DtChr = 2;
        public const byte DtDir = 4;
        public const byte DtBlk = 6;
        public const byte DtReg = 8;
        public const byte DtLnk = 10;
        public const byte DtSock = 12;
        public const byte DtWht = 14;

        // Inode mode flags (matching FreeBSD)
        public const ushort IfDir = 0x4000;   // Directory
        public const ushort IfReg = 0x8000;   // Regular file
        public const ushort IfLnk = 0xA000;   // Symbolic link

        // Default permission modes (matching FreeBSD newfs convention)
        public const ushort PermDir = 0x16D;   // 0555 - r-xr-xr-x (directories)
        public const ushort PermFile = 0x16D;  // 0555 - r-xr-xr-x (regular files)

        // Number of direct and indirect block pointers
        public const int NDirect = 12;         // Direct block pointers (UFS_NDADDR)
        public const int NIndirect = 3;        // Indirect block pointers (UFS_NIADDR)

        // Inode format (fs_old_inodefmt)
        public const int Fs44InodeFmt = 2;     // FS_44INODEFMT - 4.4BSD inode format

        // Legacy superblock constants
        public const int FsDynamicPostblFmt = -1; // FS_DYNAMICPOSTBLFMT - dynamic post block format

        // Filesystem flags (fs_flags from sys/ufs/ffs/fs.h)
        public const int FsUnclean = 0x01;     // FS_UNCLEAN
        public const int FsDosoftdep = 0x02;   // FS_DOSOFTDEP - soft updates enabled (-U)
        public const int FsNeedsfsck = 0x04;   // FS_NEEDSFSCK
        public const int FsSuj = 0x08;         // FS_SUJ - soft updates journaling (-j)
        public const int FsAcls = 0x10;        // FS_ACLS
        public const int FsMultilabel = 0x20;  // FS_MULTILABEL - multilabel MAC (-l)
        public const int FsGjournal = 0x40;    // FS_GJOURNAL - gjournal enabled (-J)
        public const int FsFlagsUpdated = 0x80; // FS_FLAGS_UPDATED
        public const int FsNfs4Acls = 0x100;   // FS_NFS4ACLS
        public const int FsMetaCkHash = 0x200; // FS_METACKHASH - kernel supports metadata check hashes
        public const int FsTrim = 0x400;       // FS_TRIM - TRIM/DISCARD enabled (-t)

        // Optimization preference (fs_optim)
        public const int FsOptTime = 0;        // FS_OPTTIME - minimize allocation time
        public const int FsOptSpace = 1;       // FS_OPTSPACE - minimize disk fragmentation

        // Structure sizes
        public const int CgHeaderBaseSize = 168;  // sizeof(struct cg) fixed portion (before bitmaps)
        public const int CsumStructSize = 16;     // sizeof(struct csum) = 4 x int32
    }
}