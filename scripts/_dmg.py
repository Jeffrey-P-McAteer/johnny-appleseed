"""
Pure-Python Apple UDIF DMG creator with embedded HFS+ filesystem.

Zero system dependencies beyond the Python standard library.

────────────────────────────────────────────────────────────────────────────────
APPLE DISK IMAGE (UDIF) FORMAT — GUIDANCE
────────────────────────────────────────────────────────────────────────────────

A .dmg file created by hdiutil is an Apple Universal Disk Image Format (UDIF)
file.  Its structure (verified against real hdiutil output):

  [data fork: compressed/raw sector data]
  [XML plist — the blkx resource fork]
  [512-byte koly block — the UDIF trailer]

KOLY BLOCK (512 bytes, at end of file):
  magic            "koly"  (4 bytes)
  version          4       (uint32 big-endian)
  headerSize       512
  flags            1
  runningDataOff   0       (unused)
  dataForkOffset   0       (data fork starts at byte 0)
  dataForkLength           (total bytes of compressed sector data)
  rsrcForkOffset   0       (not used — we use xmlOffset instead)
  rsrcForkLength   0
  segmentNumber    1
  segmentCount     1
  segmentGUID              (random 16-byte UUID)
  dataForkChecksum         (UDIFChecksum, 136 bytes: type=2/CRC32, crc32(data_fork))
  xmlOffset                (byte offset of plist after data fork)
  xmlLength
  reserved         zeros (120 bytes)
  masterChecksum           (UDIFChecksum, 136 bytes: same type/value as dfChecksum)
  imageVariant     1       (kUDIFDeviceImageType — device disk with partition table)
  sectorCount              (total 512-byte sectors described by all blkx entries)
  reserved         zeros (12 bytes)

UDIF plist (XML, appended to data fork, before koly):
  Must be structured as:
    { "resource-fork": { "blkx": [...], "plst": [...] } }
  NOT a bare array.  macOS's DiskImages.framework reads the "blkx" key
  under "resource-fork" to find the block map.  A bare array causes
  EINVAL ("Invalid argument").

BLKX ENTRIES (inside the "blkx" list):
  Each entry describes one partition of the GPT-structured disk image:
    Attributes  "0x0050"
    CFName      human-readable partition name
    Data        binary mish block (see below)
    ID          partition index as string ("-1" for MBR, "0" for GPT header, …)
    Name        same as CFName

MISH BLOCK (binary, embedded in each blkx Data field):
  sig              "mish" (0x6D697368)
  version          1
  firstSectorNumber    absolute disk-LBA of the first sector in this partition
  sectorCount          number of 512-byte sectors in this partition
  dataStart        0
  decompressBufferRequested  buffer in sectors for decompression (0x808 = 2056 for
                             ZLIB-compressed entries; 0 for IGNORE/raw)
  blockDescriptors     partition index (ordinal of this blkx entry, 0-based)
  reserved[6]          zeros
  UDIFChecksum         CRC32 of the DECOMPRESSED raw sector data for this partition
                       (type=2, size=32 bits, crc32(raw_bytes))
  numberOfBlockChunks  number of blkx_run entries that follow
  blkx_run entries:    each 40 bytes
    type             0x00000001 = RAW (uncompressed)
                     0x00000002 = IGNORE (all-zero sectors; clen=0 in data fork)
                     0x80000005 = ZLIB compressed
                     0xFFFFFFFF = END-OF-DESCRIPTOR sentinel
    reserved         0
    sectorNumber     sector offset RELATIVE TO firstSectorNumber
    sectorCount      number of sectors this chunk covers
    compressedOffset byte offset of chunk data in the data fork
    compressedLength byte count of chunk in data fork (0 for IGNORE; sector*512 for RAW)

GPT DISK LAYOUT (required — raw HFS+ without GPT causes "Invalid argument"):
  LBA  0     : Protective MBR (type 0xEE, covers whole disk)
  LBA  1     : Primary GPT Header
  LBA  2-33  : Primary GPT Partition Table (128 entries × 128 bytes = 32 sectors)
  LBA 34-39  : Apple_Free gap (6 sectors) — aligns HFS+ to LBA 40
  LBA 40-N   : HFS+ partition (our content)
  LBA N+1-N+6: Apple_Free gap (6 sectors)
  LBA N+7-N+38: Backup GPT Partition Table (32 sectors)
  LBA N+39   : Backup GPT Header

HFS+ VOLUME HEADER within the partition:
  The HFS+ VolumeHeader is at byte offset 1024 WITHIN the partition
  (= 2 sectors from the partition start).  In the global disk image,
  the VH is at byte (LBA_40 × 512 + 1024) = 20480 + 1024 = 21504.
  The Alternate VH is at image_size - 1024 (second-to-last 512-byte sector).

────────────────────────────────────────────────────────────────────────────────
DMG BACKGROUND IMAGE — APPLE GUIDANCE
────────────────────────────────────────────────────────────────────────────────

To show a custom background when a user opens the DMG in Finder:

1. Place the background image at .background/background.png (or .tiff) inside
   the DMG staging directory.  The .background directory is hidden (dot prefix)
   so it won't appear to the user in Finder.

2. Place a .DS_Store file at the root of the staging directory.  This file
   controls the Finder window appearance.  The relevant icvp record keys are:

     backgroundType       2         (0=color, 1=solid, 2=picture/image)
     backgroundImageAlias <bytes>   Mac OS Alias record pointing to the image
     arrangeBy            "none"    keep manual icon positions
     iconSize             128.0     icon size in points
     textSize             12.0      label text size
     gridOffsetX/Y        0.0       grid origin offsets
     gridSpacing          100.0     icon grid spacing
     labelOnBottom        True      icon labels below icons
     showIconPreview      True
     showItemInfo         False
     viewOptionsVersion   1

   The "backgroundImageAlias" value MUST be a Mac OS Alias resource blob
   (binary bytes), not a plain string.  Use mac_alias.Alias to produce it.
   Key alias fields:
     - VolumeInfo: volume name, HFS+ filesystem type ('H+'), fixed disk
     - TargetInfo: filename='background.png', folder_cnid=<CNID of .background>,
                   cnid=<CNID of background.png>, posix_path, etc.
   The CNIDs must match what is in the HFS+ catalog.

3. Set Finder window bounds and icon positions in .DS_Store:
     d["."]["bwsp"]    = window bounds and settings
     d["JohnnyAppleseed.app"]["Iloc"] = (x, y)   # pixel position
     d["Applications"]["Iloc"]        = (x, y)

4. The Applications symlink in the staging root:
     (staging / "Applications").symlink_to("/Applications")
   This creates an HFS+ symlink (stored as a file record with fileType='slnk',
   creator='rhsf', data=target path).  Finder renders it as a folder alias
   pointing to /Applications.

────────────────────────────────────────────────────────────────────────────────
"""

from __future__ import annotations

import datetime
import math
import os
import stat
import struct
import time
import uuid
import zlib
import plistlib
from pathlib import Path

# ── geometry ───────────────────────────────────────────────────────────────────

BLOCK = 4096    # HFS+ allocation block size and B-tree node size

# GPT disk layout constants (sector = 512 bytes)
_MBR_SECTORS        = 1
_GPT_HDR_SECTORS    = 1
_GPT_TABLE_SECTORS  = 32   # 128 GPT entries × 128 bytes = 16 384 bytes
_GAP_SECTORS        = 6    # Apple_Free padding before and after HFS+
# HFS+ partition starts at LBA 40 (= 1+1+32+6)
HFS_START_LBA = _MBR_SECTORS + _GPT_HDR_SECTORS + _GPT_TABLE_SECTORS + _GAP_SECTORS
# Total GPT overhead sectors surrounding the HFS+ content
_GPT_TAIL = _GAP_SECTORS + _GPT_TABLE_SECTORS + _GPT_HDR_SECTORS   # = 39

# Apple HFS+ partition type GUID in GPT mixed-endian byte order
# UUID: 48465300-0000-11AA-AA11-00306543ECAC
_HFS_PART_TYPE_GUID = uuid.UUID('48465300-0000-11AA-AA11-00306543ECAC').bytes_le

# HFS+ epoch: seconds from 1904-01-01 to 1970-01-01
_HFS_EPOCH = 2082844800

def _hfs_now() -> int:
    return int(time.time()) + _HFS_EPOCH


# ── verified struct sizes ───────────────────────────────────────────────────────

def _vsz(fmt: str, expected: int) -> str:
    got = struct.calcsize(fmt)
    assert got == expected, f"struct '{fmt}' is {got}, expected {expected}"
    return fmt

_VH_FMT    = _vsz(">HH 17I Q 8I", 112)   # HFS+ VolumeHeader scalar portion
_NODE_FMT  = _vsz(">IIbbHH",       14)    # BTNodeDescriptor
_BTH_FMT   = _vsz(">HIIIIHHIIHIBBI16I", 106)  # BTHeaderRec
_BSD_FMT   = _vsz(">II BBH I",     16)    # HFSPlusBSDInfo
_TAIL_FMT  = _vsz(">II",            8)    # textEncoding + reserved


# ── HFS+ struct helpers ────────────────────────────────────────────────────────

def _unistr(name: str) -> bytes:
    return struct.pack(">H", len(name)) + name.encode("utf-16-be")

def _catalog_key(parent: int, name: str) -> bytes:
    body = struct.pack(">I", parent) + _unistr(name)
    return struct.pack(">H", len(body)) + body

def _thread_key(cnid: int) -> bytes:
    body = struct.pack(">I", cnid) + struct.pack(">H", 0)
    return struct.pack(">H", len(body)) + body

def _bsd(mode: int) -> bytes:
    return struct.pack(_BSD_FMT, 0, 0, 0, 0, mode & 0xFFFF, 0)

def _fork(logical: int, start: int, count: int) -> bytes:
    hdr  = struct.pack(">QII", logical, BLOCK, count)
    ext0 = struct.pack(">II", start, count)
    return hdr + ext0 + b"\x00" * (7 * 8)

def _empty_fork() -> bytes:
    return b"\x00" * 80

def _folder_rec(cnid: int, valence: int, now: int, mode: int = 0o40755) -> bytes:
    r  = struct.pack(">hH IIIIIII", 0x0001, 0, valence, cnid, now, now, now, now, 0)
    r += _bsd(mode) + b"\x00" * 16 + b"\x00" * 16 + struct.pack(_TAIL_FMT, 0, 0)
    assert len(r) == 88, len(r)
    return r

def _file_rec(cnid: int, data_size: int, start: int, count: int, now: int,
              mode: int = 0o100644, ftype: int = 0, creator: int = 0) -> bytes:
    ui = struct.pack(">II", ftype, creator) + b"\x00" * 8
    r  = struct.pack(">hH IIIIIII", 0x0002, 0, 0, cnid, now, now, now, now, 0)
    r += _bsd(mode) + ui + b"\x00" * 16 + struct.pack(_TAIL_FMT, 0, 0)
    r += _fork(data_size, start, count) + _empty_fork()
    assert len(r) == 248, len(r)
    return r

def _thread_rec(rtype: int, parent: int, name: str) -> bytes:
    return struct.pack(">hHHI", rtype, 0, 0, parent) + _unistr(name)

_SLNK_TYPE    = 0x736C6E6B   # 'slnk' — HFS+ symlink file type
_SLNK_CREATOR = 0x72687366   # 'rhsf' — HFS+ symlink creator


# ── B-tree ─────────────────────────────────────────────────────────────────────

def _leaf_node(flink: int, blink: int, records: list[bytes]) -> bytes:
    desc = struct.pack(_NODE_FMT, flink, blink, -1, 1, len(records), 0)
    offsets, pos = [], 14
    for r in records:
        offsets.append(pos); pos += len(r)
    offsets.append(pos)   # free-space sentinel
    table = b"".join(struct.pack(">H", o) for o in reversed(offsets))
    body  = desc + b"".join(records)
    if len(body) + len(table) > BLOCK:
        raise OverflowError(f"Leaf node too large: {len(body)+len(table)} > {BLOCK}")
    return body + b"\x00" * (BLOCK - len(body) - len(table)) + table

def _header_node(total: int, free: int, root: int,
                 first: int, last: int, n_recs: int,
                 depth: int, max_key: int) -> bytes:
    desc = struct.pack(_NODE_FMT, 0, 0, 1, 0, 3, 0)
    hdr  = struct.pack(_BTH_FMT,
        depth, root, n_recs, first, last,
        BLOCK, max_key, total, free, 0, BLOCK, 0, 0xBC, 0x00000006,
        *([0] * 16)
    )
    assert len(hdr) == 106
    user = b"\x00" * 128
    map_size = BLOCK - 14 - 106 - 128 - 8
    bmap = bytearray(map_size)
    for idx in {0, root, *range(first, last + 1)}:
        byte, bit = divmod(idx, 8)
        if byte < map_size:
            bmap[byte] |= 0x80 >> bit
    records = [hdr, user, bytes(bmap)]
    offsets, p = [14], 14
    for r in records:
        p += len(r); offsets.append(p)
    table = b"".join(struct.pack(">H", o) for o in reversed(offsets))
    node  = desc + hdr + user + bytes(bmap)
    assert len(node) + len(table) == BLOCK
    return node + table

def _key_order(k: bytes) -> tuple:
    parent = struct.unpack_from(">I", k, 2)[0]
    nlen   = struct.unpack_from(">H", k, 6)[0]
    name   = k[8:8+nlen*2].decode("utf-16-be", errors="replace").lower()
    return (parent, name)

def _build_catalog(pairs: list[tuple[bytes, bytes]]) -> bytes:
    pairs  = sorted(pairs, key=lambda kv: _key_order(kv[0]))
    # kHFSPlusCatalogMaxKeyLength = 516 (Apple constant: max catalog key body size)
    max_key = 516

    nodes: list[list[bytes]] = [[]]
    used = 14
    for key, val in pairs:
        rec  = key + val
        need = len(rec) + 2
        if nodes[-1] and used + need + (len(nodes[-1]) + 2) * 2 > BLOCK:
            nodes.append([]); used = 14
        nodes[-1].append(rec); used += len(rec)

    n = len(nodes)
    leaf_bytes = []
    for i, recs in enumerate(nodes):
        leaf_bytes.append(_leaf_node(
            (i + 2) if i < n - 1 else 0,
            i if i > 0 else 0,
            recs,
        ))

    n_recs = sum(len(nd) for nd in nodes)
    head   = _header_node(1 + n, 0, 1, 1, n, n_recs, 1, max_key)
    return head + b"".join(leaf_bytes)


# ── HFS+ volume header ─────────────────────────────────────────────────────────

def _volume_header(total_blocks: int, free_blocks: int,
                   file_count: int, folder_count: int,
                   now: int, next_cnid: int,
                   alloc_start: int, alloc_blocks: int,
                   ext_start: int, ext_size: int,
                   cat_start: int, cat_size: int) -> bytes:
    scalar = struct.pack(
        _VH_FMT,
        0x482B, 0x0004,      # signature 'H+', version 4
        (1 << 8),            # kHFSVolumeUnmountedMask — skip fsck on mount
        0x31302E30,          # lastMountedVersion '10.0'
        0,                   # journalInfoBlock (not journaled)
        now, now, 0, now,    # create, modify, backup, checked
        file_count, folder_count,
        BLOCK, total_blocks, free_blocks,
        total_blocks // 8,   # nextAllocation hint
        BLOCK * 4, BLOCK * 4, next_cnid, 1,  # clumps, nextCatalogID, writeCount
        0,                   # encodingsBitmap
        *([0] * 8),          # finderInfo[8]
    )
    forks = (
        _fork(alloc_blocks * BLOCK, alloc_start, alloc_blocks)
        + _fork(ext_size, ext_start, 1)      # extents overflow B-tree
        + _fork(cat_size, cat_start,
                math.ceil(cat_size / BLOCK)) # catalog B-tree
        + _empty_fork()                       # attributes file
        + _empty_fork()                       # startup file
    )
    vh = scalar + forks
    assert len(vh) == 512, len(vh)
    return vh


# ── staging walker ─────────────────────────────────────────────────────────────

class _Entry:
    __slots__ = ("kind", "parent", "name", "cnid", "mode", "data")
    def __init__(self, kind, parent, name, cnid, mode, data):
        self.kind, self.parent, self.name = kind, parent, name
        self.cnid, self.mode, self.data   = cnid, mode, data

def _walk(path: Path, parent: int, ctr: list[int]) -> list[_Entry]:
    out = []
    for item in sorted(path.iterdir(), key=lambda p: p.name.lower()):
        cnid = ctr[0]; ctr[0] += 1
        if item.is_symlink():
            target = os.readlink(item)
            out.append(_Entry("symlink", parent, item.name, cnid,
                              0o120777, target.encode("utf-8")))
        elif item.is_dir():
            out.append(_Entry("dir", parent, item.name, cnid, 0o40755, None))
            out.extend(_walk(item, cnid, ctr))
        elif item.is_file():
            out.append(_Entry("file", parent, item.name, cnid,
                              item.stat().st_mode & 0xFFFF,
                              item.read_bytes()))
    return out

_CNID_ROOT_PARENT = 1
_CNID_ROOT        = 2
_CNID_EXTENTS     = 3
_CNID_CATALOG     = 4
_CNID_BITMAP      = 5
_CNID_FIRST_USER  = 16


# ── HFS+ image builder ─────────────────────────────────────────────────────────

def _build_hfs_image(staging: Path, label: str) -> bytes:
    """
    Return raw HFS+ partition bytes.
    The VolumeHeader is at byte 1024 and the Alternate VH at image_size - 1024.
    Both are required by macOS; the partition is placed at LBA 40 in the disk image
    so the absolute positions in the UDIF data stream differ by 40 × 512 = 20 480.
    """
    now = _hfs_now()
    ctr = [_CNID_FIRST_USER]
    entries = _walk(staging, _CNID_ROOT, ctr)
    next_cnid = ctr[0]

    files  = [e for e in entries if e.kind in ("file", "symlink")]
    dirs   = [e for e in entries if e.kind == "dir"]
    file_count   = len(files)
    folder_count = len(dirs) + 1   # +1 for root

    # ── first pass: measure catalog size ──────────────────────────────────────
    pairs = _make_catalog_pairs(entries, {}, label, now)
    cat0  = _build_catalog(pairs)
    cat_blocks = math.ceil(len(cat0) / BLOCK)

    # Fixed block layout (alloc_blocks=1 covers ≤128 MB; ext_blocks=1):
    #   [0: reserved+VH] [1: bitmap] [2: extents] [3..3+cat-1: catalog] [data…] [last: altVH]
    data_start = 3 + cat_blocks
    pos = data_start
    positions: dict[int, tuple[int, int]] = {}
    for e in files:
        sz = len(e.data) if e.data else 0
        nb = math.ceil(sz / BLOCK) if sz else 0
        positions[e.cnid] = (pos, nb)
        pos += nb
    total_blocks = pos + 1   # +1 for alternate VH block
    free_blocks  = 0         # distribution image is fully packed

    # ── second pass: catalog with real block positions ─────────────────────────
    pairs = _make_catalog_pairs(entries, positions, label, now)
    cat   = _build_catalog(pairs)
    assert math.ceil(len(cat) / BLOCK) == cat_blocks, "catalog size changed"

    # ── allocation bitmap ─────────────────────────────────────────────────────
    bmap_bytes = bytearray(math.ceil(total_blocks / 8))
    def _mark(b: int) -> None:
        bmap_bytes[b // 8] |= 0x80 >> (b % 8)
    _mark(0)
    _mark(1)  # alloc bitmap itself at block 1
    _mark(2)  # extents B-tree at block 2
    for b in range(3, 3 + cat_blocks):
        _mark(b)
    for e in files:
        s, nb = positions[e.cnid]
        for b in range(s, s + nb):
            _mark(b)
    _mark(total_blocks - 1)  # alt VH block

    # ── extents overflow B-tree (header-only, empty tree) ─────────────────────
    ext_tree = _header_node(1, 0, 0, 0, 0, 0, 0, 0)

    # ── volume header ─────────────────────────────────────────────────────────
    vh = _volume_header(
        total_blocks, free_blocks, file_count, folder_count,
        now, next_cnid,
        alloc_start=1, alloc_blocks=1,
        ext_start=2, ext_size=BLOCK,
        cat_start=3, cat_size=len(cat),
    )

    # ── assemble image ────────────────────────────────────────────────────────
    img = bytearray(total_blocks * BLOCK)

    img[1024:1536] = vh                              # primary VH

    ab = 1 * BLOCK
    img[ab:ab + len(bmap_bytes)] = bmap_bytes        # allocation bitmap

    eb = 2 * BLOCK
    img[eb:eb + BLOCK] = ext_tree                    # extents header node

    cb = 3 * BLOCK
    img[cb:cb + len(cat)] = cat                      # catalog B-tree

    for e in files:
        s, nb = positions[e.cnid]
        if e.data:
            img[s * BLOCK:s * BLOCK + len(e.data)] = e.data

    # Alternate VH at image_size − 1024 (second-to-last 512-byte sector)
    img[len(img) - 1024:len(img) - 512] = vh

    return bytes(img)


def _make_catalog_pairs(
    entries: list[_Entry],
    positions: dict[int, tuple[int, int]],
    label: str,
    now: int,
) -> list[tuple[bytes, bytes]]:
    pairs = []
    # Root directory
    root_valence = sum(1 for e in entries if e.parent == _CNID_ROOT)
    pairs.append((_thread_key(_CNID_ROOT),
                  _thread_rec(0x0003, _CNID_ROOT_PARENT, label)))
    pairs.append((_catalog_key(_CNID_ROOT_PARENT, label),
                  _folder_rec(_CNID_ROOT, root_valence, now)))

    for e in entries:
        if e.kind == "dir":
            valence = sum(1 for x in entries if x.parent == e.cnid)
            pairs.append((_thread_key(e.cnid),
                          _thread_rec(0x0003, e.parent, e.name)))
            pairs.append((_catalog_key(e.parent, e.name),
                          _folder_rec(e.cnid, valence, now, e.mode)))
        else:
            s, nb = positions.get(e.cnid, (0, 0))
            sz = len(e.data) if e.data else 0
            if e.kind == "symlink":
                val = _file_rec(e.cnid, sz, s, nb, now,
                                e.mode, _SLNK_TYPE, _SLNK_CREATOR)
            else:
                val = _file_rec(e.cnid, sz, s, nb, now, e.mode)
            pairs.append((_thread_key(e.cnid),
                          _thread_rec(0x0004, e.parent, e.name)))
            pairs.append((_catalog_key(e.parent, e.name), val))
    return pairs


# ── GPT disk structure ─────────────────────────────────────────────────────────

def _protective_mbr(total_lba: int) -> bytes:
    """
    Protective MBR: sector 0 of a GPT disk.
    Contains one partition entry with type 0xEE covering the whole disk.
    The 0x55 0xAA boot signature is required at the last two bytes.
    """
    mbr = bytearray(512)
    off = 446   # first partition entry
    mbr[off]     = 0x00                      # status: not bootable
    mbr[off+1:off+4] = b'\x00\x02\x00'      # CHS start (irrelevant for GPT)
    mbr[off+4]   = 0xEE                      # type: GPT protective
    mbr[off+5:off+8] = b'\xFF\xFF\xFF'       # CHS end (irrelevant)
    struct.pack_into('<I', mbr, off+8, 1)    # first LBA = 1
    struct.pack_into('<I', mbr, off+12, min(total_lba - 1, 0xFFFF_FFFF))
    mbr[510] = 0x55
    mbr[511] = 0xAA
    return bytes(mbr)


def _gpt_partition_entry(type_guid: bytes, unique_guid: bytes,
                          start_lba: int, end_lba: int, name: str) -> bytes:
    """
    128-byte GPT partition entry.
    type_guid and unique_guid are in mixed-endian GUID byte order (uuid.bytes_le).
    Attributes = 0 (no special flags).
    Partition name is UTF-16LE, padded with zeros to 72 bytes.
    """
    entry = bytearray(128)
    entry[0:16]  = type_guid
    entry[16:32] = unique_guid
    struct.pack_into('<Q', entry, 32, start_lba)
    struct.pack_into('<Q', entry, 40, end_lba)
    name_utf16 = name.encode('utf-16-le')[:72]
    entry[56:56 + len(name_utf16)] = name_utf16
    return bytes(entry)


def _gpt_header(my_lba: int, alt_lba: int,
                first_usable: int, last_usable: int,
                disk_guid: bytes,
                part_table_lba: int, part_crc: int) -> bytes:
    """
    512-byte GPT header (92 bytes of content zero-padded to 512).
    CRC32 is computed over the first 92 bytes with the CRC field itself zeroed.
    128 entries, 128 bytes each (standard).
    """
    hdr = bytearray(512)
    hdr[0:8]  = b'EFI PART'
    struct.pack_into('<I', hdr, 8,  0x00010000)  # revision 1.0
    struct.pack_into('<I', hdr, 12, 92)           # header size
    # CRC32 at offset 16 = 0 initially (compute below)
    struct.pack_into('<Q', hdr, 24, my_lba)
    struct.pack_into('<Q', hdr, 32, alt_lba)
    struct.pack_into('<Q', hdr, 40, first_usable)
    struct.pack_into('<Q', hdr, 48, last_usable)
    hdr[56:72] = disk_guid
    struct.pack_into('<Q', hdr, 72, part_table_lba)
    struct.pack_into('<I', hdr, 80, 128)           # numberOfPartitionEntries
    struct.pack_into('<I', hdr, 84, 128)           # sizeOfPartitionEntry
    struct.pack_into('<I', hdr, 88, part_crc)      # CRC32 of partition array
    crc = zlib.crc32(bytes(hdr[:92])) & 0xFFFFFFFF
    struct.pack_into('<I', hdr, 16, crc)
    return bytes(hdr)


def _build_gpt_table(hfs_start_lba: int, hfs_end_lba: int) -> bytes:
    """
    Primary GPT partition table (32 sectors = 16 384 bytes, 128 entries × 128 bytes).
    Entry 0: HFS+ partition covering [hfs_start_lba, hfs_end_lba].
    Entries 1-127: zeros (unused).
    """
    table = bytearray(32 * 512)
    hfs_guid = uuid.uuid4().bytes_le
    entry = _gpt_partition_entry(
        _HFS_PART_TYPE_GUID, hfs_guid,
        hfs_start_lba, hfs_end_lba,
        'disk image',   # matches hdiutil's naming convention
    )
    table[0:128] = entry
    return bytes(table)


# ── mish block builder ─────────────────────────────────────────────────────────

def _mish(first_sec: int, sec_cnt: int, blk_desc: int,
          chunks: list[dict], raw_sector_bytes: bytes) -> bytes:
    """
    Build a binary mish block for one GPT partition.

    chunks: list of dicts with keys type, sec, cnt, coff, clen
      type  0x00000001 = RAW   (uncompressed, clen = cnt×512)
            0x00000002 = IGNORE (all zeros, clen = 0)
            0xFFFFFFFF = END    (auto-appended by this function)
    raw_sector_bytes: the decompressed sector data for this partition
      (used to compute the mish checksum = CRC32 of decompressed bytes)

    The END sentinel is appended automatically.
    """
    # Checksum = CRC32 of the DECOMPRESSED sector data (verified against real DMGs)
    ck = zlib.crc32(raw_sector_bytes) & 0xFFFFFFFF if raw_sector_bytes else 0

    # Build run entries
    runs = b""
    for c in chunks:
        runs += struct.pack(">II QQ QQ",
            c["type"], 0,
            c["sec"],  c["cnt"],
            c["coff"], c["clen"],
        )
    # END sentinel: sec = total sectors in partition, coff = total bytes consumed
    end_sec  = sum(c["cnt"]  for c in chunks)
    end_coff = sum(c["clen"] for c in chunks)
    runs += struct.pack(">II QQ QQ",
        0xFFFFFFFF, 0, end_sec, 0, end_coff, 0)
    n_chunks = len(chunks) + 1   # includes END

    header = struct.pack(
        ">II QQQ II IIIIII",
        0x6D697368,   # 'mish' signature
        1,            # version
        first_sec,    # firstSectorNumber (absolute LBA on disk)
        sec_cnt,      # sectorCount
        0,            # dataStart (always 0)
        0,            # decompressBufferRequested (0 for RAW/IGNORE)
        blk_desc,     # blockDescriptors = ordinal partition index
        0, 0, 0, 0, 0, 0,   # reserved[6]
    )
    ck_bytes = struct.pack(">II", 2, 32) + struct.pack(">I", ck) + b"\x00" * (31 * 4)
    return header + ck_bytes + struct.pack(">I", n_chunks) + runs


def _blkx_entry(name: str, entry_id: str, mish_data: bytes) -> dict:
    """One element of the blkx array in the UDIF plist."""
    return {
        "Attributes": "0x0050",
        "CFName":     name,
        "Data":       mish_data,
        "ID":         entry_id,
        "Name":       name,
    }


def _make_udif_plist(blkx: list[dict]) -> bytes:
    """
    Serialise the UDIF resource-fork plist.
    The top-level key MUST be "resource-fork" containing "blkx" and "plst".
    A bare array causes macOS DiskImages.framework to return EINVAL.
    The "plst" entry is a driver-descriptor placeholder (zeros).
    """
    plst_entry = {
        "Attributes": "0x0050",
        "Data": b"\x00" * 1024,
        "ID": "0",
        "Name": "",
    }
    return plistlib.dumps(
        {"resource-fork": {"blkx": blkx, "plst": [plst_entry]}},
        fmt=plistlib.FMT_XML,
    )


# ── koly trailer ───────────────────────────────────────────────────────────────

def _koly(data_fork_len: int, plist_offset: int, plist_len: int,
          total_sectors: int, df_crc: int) -> bytes:
    """
    512-byte UDIF koly trailer block.
    imageVariant = 1 (kUDIFDeviceImageType) — disk image with GPT.
    Both dataForkChecksum and masterChecksum are CRC32 of the data fork.
    """
    def _ck(crc: int) -> bytes:
        # UDIFChecksum: type(4) + size_bits(4) + value(4) + zeros(124) = 136 bytes
        return struct.pack(">III", 2, 32, crc) + b"\x00" * 124

    seg_guid = uuid.uuid4().bytes
    koly = (
        b"koly"
        + struct.pack(">III", 4, 512, 1)                        # ver, hdrSize, flags
        + struct.pack(">QQQ", 0, 0, data_fork_len)              # runOff, dataOff, dataLen
        + struct.pack(">QQ",  0, 0)                             # rsrcOff, rsrcLen (unused)
        + struct.pack(">II", 1, 1)                              # segNum, segCount
        + seg_guid                                               # 16-byte UUID
        + _ck(df_crc)                                           # DataForkChecksum (136)
        + struct.pack(">QQ", plist_offset, plist_len)           # xmlOffset, xmlLength
        + b"\x00" * 120                                         # reserved
        + _ck(df_crc)                                           # MasterChecksum (136)
        + struct.pack(">IQ", 1, total_sectors)                  # variant=1, sectors
        + b"\x00" * 12                                          # reserved
    )
    assert len(koly) == 512, len(koly)
    return koly


# ── public entry point ─────────────────────────────────────────────────────────

def build_dmg(staging: Path, output: Path, label: str) -> Path:
    """
    Build a proper Apple UDIF DMG from the contents of *staging*.

    Disk layout produced:
      LBA  0     : Protective MBR
      LBA  1     : Primary GPT Header
      LBA  2-33  : Primary GPT Partition Table
      LBA 34-39  : Apple_Free gap
      LBA 40-N   : HFS+ partition (volume label = *label*)
      LBA N+1-N+6: Apple_Free gap
      LBA N+7-N+38: Backup GPT Partition Table
      LBA N+39   : Backup GPT Header

    The HFS+ partition contains the full directory tree from *staging*,
    including symlinks (stored as HFS+ 'slnk'/'rhsf' files) and hidden
    directories (.background, .DS_Store, etc.).

    The .background/background.png and .DS_Store files (created by
    package.py before calling this function) are included verbatim so
    that Finder shows the custom background and icon layout when the
    DMG is opened.
    """
    # ── 1. build the HFS+ partition image ────────────────────────────────────
    hfs = _build_hfs_image(staging, label)
    hfs_sectors = len(hfs) // 512
    assert len(hfs) % 512 == 0

    # ── 2. calculate GPT geometry ─────────────────────────────────────────────
    hfs_start = HFS_START_LBA                    # = 40
    hfs_end   = hfs_start + hfs_sectors - 1

    # Disk sector map:
    #   [0..hfs_start-1] = GPT overhead (40 sectors)
    #   [hfs_start..hfs_end] = HFS+ partition
    #   [hfs_end+1..hfs_end+6] = Apple_Free gap
    #   [hfs_end+7..hfs_end+38] = Backup GPT table
    #   [hfs_end+39] = Backup GPT header
    total_sectors = hfs_end + 1 + _GPT_TAIL   # _GPT_TAIL = gap+table+hdr = 39
    disk_guid     = uuid.uuid4().bytes_le
    last_usable   = total_sectors - _GPT_TABLE_SECTORS - _GPT_HDR_SECTORS - 1

    # ── 3. GPT tables (primary and backup are identical content) ─────────────
    gpt_table    = _build_gpt_table(hfs_start, hfs_end)
    part_crc     = zlib.crc32(gpt_table) & 0xFFFFFFFF

    # Primary GPT header at LBA 1, backup at last LBA
    primary_hdr  = _gpt_header(
        my_lba=1, alt_lba=total_sectors - 1,
        first_usable=hfs_start, last_usable=last_usable,
        disk_guid=disk_guid, part_table_lba=2, part_crc=part_crc,
    )
    backup_hdr = _gpt_header(
        my_lba=total_sectors - 1, alt_lba=1,
        first_usable=hfs_start, last_usable=last_usable,
        disk_guid=disk_guid,
        part_table_lba=total_sectors - 1 - _GPT_TABLE_SECTORS,
        part_crc=part_crc,
    )
    mbr = _protective_mbr(total_sectors)

    # ── 4. assemble the UDIF data fork ────────────────────────────────────────
    # Each RAW chunk contributes its bytes to the data fork.
    # IGNORE chunks have clen=0 and contribute no bytes.
    # The coff values track the running byte offset in the data fork.
    coff = 0
    data_fork_parts = []
    blkx           = []

    def _raw_chunk(raw: bytes, first_sec: int, sec_cnt: int,
                   blk_desc: int, name: str, entry_id: str) -> None:
        nonlocal coff
        chunk = {"type": 0x00000001, "sec": 0, "cnt": sec_cnt,
                 "coff": coff, "clen": len(raw)}
        blkx.append(_blkx_entry(name, entry_id,
                                 _mish(first_sec, sec_cnt, blk_desc, [chunk], raw)))
        data_fork_parts.append(raw)
        coff += len(raw)

    def _ignore_chunk(first_sec: int, sec_cnt: int,
                      blk_desc: int, name: str, entry_id: str) -> None:
        chunk = {"type": 0x00000002, "sec": 0, "cnt": sec_cnt, "coff": coff, "clen": 0}
        raw = b"\x00" * (sec_cnt * 512)   # decompressed = all zeros
        blkx.append(_blkx_entry(name, entry_id,
                                 _mish(first_sec, sec_cnt, blk_desc, [chunk], raw)))
        # No bytes added to data fork for IGNORE

    # Entry -1 (blk_desc=0): Protective MBR, 1 sector
    _raw_chunk(mbr, first_sec=0, sec_cnt=1, blk_desc=0,
               name="Protective Master Boot Record (MBR : 0)",
               entry_id="-1")

    # Entry 0 (blk_desc=1): Primary GPT Header, 1 sector
    _raw_chunk(primary_hdr, first_sec=1, sec_cnt=1, blk_desc=1,
               name="GPT Header (Primary GPT Header : 1)",
               entry_id="0")

    # Entry 1 (blk_desc=2): Primary GPT Table, 32 sectors
    _raw_chunk(gpt_table, first_sec=2, sec_cnt=32, blk_desc=2,
               name="GPT Partition Data (Primary GPT Table : 2)",
               entry_id="1")

    # Entry 2 (blk_desc=3): Apple_Free before HFS+, 6 sectors, IGNORE
    _ignore_chunk(first_sec=34, sec_cnt=6, blk_desc=3,
                  name=" (Apple_Free : 3)", entry_id="2")

    # Entry 3 (blk_desc=4): HFS+ partition
    _raw_chunk(hfs, first_sec=hfs_start, sec_cnt=hfs_sectors, blk_desc=4,
               name=f"disk image (Apple_HFS : 4)",
               entry_id="3")

    # Entry 4 (blk_desc=5): Apple_Free after HFS+, 6 sectors, IGNORE
    _ignore_chunk(first_sec=hfs_end + 1, sec_cnt=6, blk_desc=5,
                  name=" (Apple_Free : 5)", entry_id="4")

    # Entry 5 (blk_desc=6): Backup GPT Table, 32 sectors
    _raw_chunk(gpt_table,
               first_sec=total_sectors - 1 - _GPT_TABLE_SECTORS,
               sec_cnt=32, blk_desc=6,
               name="GPT Partition Data (Backup GPT Table : 6)",
               entry_id="5")

    # Entry 6 (blk_desc=7): Backup GPT Header, 1 sector
    _raw_chunk(backup_hdr, first_sec=total_sectors - 1, sec_cnt=1, blk_desc=7,
               name="GPT Header (Backup GPT Header : 7)",
               entry_id="6")

    data_fork = b"".join(data_fork_parts)
    df_crc    = zlib.crc32(data_fork) & 0xFFFFFFFF

    # ── 5. plist and koly ─────────────────────────────────────────────────────
    plist_bytes  = _make_udif_plist(blkx)
    plist_offset = len(data_fork)
    koly_block   = _koly(len(data_fork), plist_offset, len(plist_bytes),
                         total_sectors, df_crc)

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(data_fork + plist_bytes + koly_block)
    return output


# ── Mac OS Alias blob for DS_Store background image ───────────────────────────

def make_background_alias(
    volume_label: str,
    folder_cnid: int,
    file_cnid: int,
    file_name: str = "background.png",
    folder_name: str = ".background",
    volume_created: datetime.datetime | None = None,
) -> bytes | None:
    """
    Create a Mac OS Alias blob pointing to a background image inside the DMG.

    This blob goes into the DS_Store icvp["backgroundImageAlias"] field.
    Finder uses it to locate and display the background image when the DMG
    is opened.  The CNIDs must match what was assigned in the HFS+ catalog.

    Returns None if mac_alias is unavailable (caller should fall back to color).

    Apple guidance on backgroundImageAlias:
      • Type: binary data (Mac OS Alias resource)
      • VolumeInfo must identify the HFS+ volume by name and creation date
      • TargetInfo must provide the file's CNID (catalog node ID) so Finder
        can find the file even if it has been renamed
      • The posix_path field is a hint for resolution; CNID takes priority
    """
    try:
        from mac_alias import Alias, VolumeInfo, TargetInfo
        from mac_alias import ALIAS_FILESYSTEM_HFSPLUS, ALIAS_FIXED_DISK, ALIAS_KIND_FILE
    except ImportError:
        return None

    if volume_created is None:
        volume_created = datetime.datetime.now(tz=datetime.timezone.utc)

    try:
        vol = VolumeInfo(
            name=volume_label,
            creation_date=volume_created,
            fs_type=ALIAS_FILESYSTEM_HFSPLUS,
            disk_type=ALIAS_FIXED_DISK,
            attribute_flags=0,
            fs_id=b"\x00\x00",
        )
        tgt = TargetInfo(
            kind=ALIAS_KIND_FILE,
            filename=file_name,
            folder_cnid=folder_cnid,
            cnid=file_cnid,
            creation_date=volume_created,
            creator_code=b"\x00\x00\x00\x00",
            type_code=b"\x00\x00\x00\x00",
            folder_name=folder_name,
            posix_path=f"/{folder_name}/{file_name}",
        )
        a = Alias(appinfo=b"\x00\x00\x00\x00", version=2)
        a.target = tgt
        a.volume = vol
        return a.to_bytes()
    except Exception:
        return None
