"""
Pure-Python Apple UDIF DMG creator with embedded HFS+ filesystem.

Zero system dependencies beyond the Python standard library.  Produces the
same file format that `hdiutil create -format UDRO` outputs on macOS: an
Apple Universal Disk Image Format (UDIF) file containing an HFS+ volume.
macOS Disk Arbitration mounts it natively, complete with Finder window
customisation (custom background, icon positions, Applications symlink).

Limitations vs hdiutil:
  • UDRO (raw/uncompressed) format only — no zlib block compression.
    Files are larger but functionally identical; compress the .dmg with
    a general-purpose compressor (zip, zstd) if size is a concern.
  • No HFS+ journaling — acceptable for distribution media.
  • Symlink targets must be ≤ 255 UTF-8 bytes.
  • Maximum image size ≈ 4 GB (simple single-extent block addressing).

Public entry point:
    build_dmg(staging_dir: Path, output_path: Path, volume_label: str) -> Path
"""

from __future__ import annotations

import math
import os
import stat
import struct
import time
import uuid
import zlib
import plistlib
from pathlib import Path

# ── HFS+ epoch ─────────────────────────────────────────────────────────────────
_HFS_EPOCH_DELTA = 2082844800   # seconds between 1904-01-01 and 1970-01-01 UTC

def _hfs_now() -> int:
    return int(time.time()) + _HFS_EPOCH_DELTA


# ── geometry ───────────────────────────────────────────────────────────────────
BLOCK = 4096   # allocation block size and B-tree node size

# Fixed block assignments in the HFS+ image we generate:
#   block 0        → bytes 0–4095  (two 512-byte reserved sectors + VolumeHeader)
#   block 1        → allocation bitmap
#   block 2        → extents overflow B-tree (minimal: header node only)
#   block 3+       → catalog B-tree (header node + leaf nodes)
#   block 3+NL+    → file data
#   last block     → alternate VolumeHeader (byte 1024 of that block)
_ALLOC_BLOCK   = 1
_EXTENTS_BLOCK = 2
_CAT_START     = 3

# HFS+ reserved CNIDs (1–15)
_CNID_ROOT_PARENT = 1
_CNID_ROOT        = 2
_CNID_EXTENTS     = 3
_CNID_CATALOG     = 4
_CNID_BITMAP      = 5
_CNID_FIRST_USER  = 16

# Catalog record types
_FOLDER_REC        = 0x0001
_FILE_REC          = 0x0002
_FOLDER_THREAD_REC = 0x0003
_FILE_THREAD_REC   = 0x0004

# HFS+ symlink file type / creator ('slnk' / 'rhsf')
_SLNK_TYPE    = 0x736C6E6B
_SLNK_CREATOR = 0x72687366


# ── verified struct sizes ───────────────────────────────────────────────────────
# Run once at import time so bugs surface immediately.

def _vsz(fmt: str, expected: int) -> str:
    got = struct.calcsize(fmt)
    assert got == expected, f"struct '{fmt}' is {got} bytes, expected {expected}"
    return fmt

# HFSPlusVolumeHeader scalar portion: 2+2 + 17×4 + 8 + 8×4 = 112 bytes
_VH_FMT = _vsz(">HH 17I Q 8I", 112)

# HFSPlusForkData: 8+4+4 + 8×(4+4) = 80 bytes
_FORK_HDR_FMT = _vsz(">QII", 16)
_EXTENT_FMT   = _vsz(">II",   8)

# BTNodeDescriptor: 4+4+1+1+2+2 = 14 bytes
_NODE_DESC_FMT = _vsz(">IIbbHH", 14)

# BTHeaderRec: 2+4+4+4+4+2+2+4+4+2+4+1+1+4+64 = 106 bytes
_BTH_FMT = _vsz(">HIIIIHHIIHIBBI16I", 106)

# HFSPlusBSDInfo: 4+4+1+1+2+4 = 16 bytes
_BSD_FMT = _vsz(">II BBH I", 16)

# HFSPlusCatalogFolder: 32 + 16 + 16 + 16 + 8 = 88 bytes
# 32 = 2+2+4+4+4+4+4+4+4  (recordType,flags,valence,cnid,4×dates,backupDate)
_FOLDER_FIXED_FMT = _vsz(">hH II IIIII", 32)   # actually 2+2+4+4+4+4+4+4+4 wait...

# Let me carefully count the folder record fields:
# recordType(h:2)+flags(H:2)+valence(I:4)+folderID(I:4)+
# createDate(I:4)+contentModDate(I:4)+attributeModDate(I:4)+
# accessDate(I:4)+backupDate(I:4)
# = 2+2+4+4+4+4+4+4+4 = 32 bytes
_FOLDER_HDR_FMT = _vsz(">hH IIIIIII", 32)

# HFSPlusCatalogFile fixed header:
# recordType(h:2)+flags(H:2)+reserved1(I:4)+fileID(I:4)+
# createDate(I:4)+contentModDate(I:4)+attributeModDate(I:4)+
# accessDate(I:4)+backupDate(I:4)
# = 2+2+4+4+4+4+4+4+4 = 32 bytes
_FILE_HDR_FMT = _vsz(">hH IIIIIII", 32)

# textEncoding(I:4) + reserved(I:4) = 8 bytes (tail of both folder and file records)
_TAIL_FMT = _vsz(">II", 8)

# Thread record fixed portion: type(h:2)+reserved(H:2+H:2)+parentID(I:4) = 10 bytes
_THREAD_FMT = _vsz(">hHHI", 10)


# ── struct builder helpers ──────────────────────────────────────────────────────

def _unistr(name: str) -> bytes:
    """HFS+ Unicode string: uint16 char-count (not byte-count) + UTF-16-BE."""
    return struct.pack(">H", len(name)) + name.encode("utf-16-be")

def _empty_unistr() -> bytes:
    return struct.pack(">H", 0)

def _catalog_key(parent: int, name: str) -> bytes:
    """Variable-length catalog B-tree key: keyLen(2)+parentID(4)+name(var)."""
    body = struct.pack(">I", parent) + _unistr(name)
    return struct.pack(">H", len(body)) + body

def _thread_key(cnid: int) -> bytes:
    """Thread key: parentID=cnid, empty name → 2+4+2 = 8 bytes total."""
    body = struct.pack(">I", cnid) + _empty_unistr()
    return struct.pack(">H", len(body)) + body

def _bsd(mode: int) -> bytes:
    """HFSPlusBSDInfo (16 bytes): ownerID=0, groupID=0, mode, special=0."""
    return struct.pack(_BSD_FMT, 0, 0, 0, 0, mode & 0xFFFF, 0)

def _fork(logical: int, start: int, count: int) -> bytes:
    """HFSPlusForkData (80 bytes)."""
    hdr    = struct.pack(_FORK_HDR_FMT, logical, BLOCK, count)
    ext0   = struct.pack(_EXTENT_FMT, start, count)
    return hdr + ext0 + b"\x00" * (7 * 8)   # 7 empty extents

def _empty_fork() -> bytes:
    return b"\x00" * 80

def _folder_rec(cnid: int, valence: int, now: int, mode: int = 0o40755) -> bytes:
    """HFSPlusCatalogFolder — exactly 88 bytes."""
    r  = struct.pack(_FOLDER_HDR_FMT, _FOLDER_REC, 0, valence, cnid,
                     now, now, now, now, 0)   # 32
    r += _bsd(mode)                           # 16
    r += b"\x00" * 16                         # FolderInfo
    r += b"\x00" * 16                         # ExtendedFolderInfo
    r += struct.pack(_TAIL_FMT, 0, 0)         # textEncoding + reserved
    assert len(r) == 88, len(r)
    return r

def _file_rec(cnid: int, data_size: int, start: int, count: int, now: int,
              mode: int = 0o100644,
              ftype: int = 0, creator: int = 0) -> bytes:
    """HFSPlusCatalogFile — exactly 248 bytes."""
    # FileInfo (16 bytes): fileType(4)+fileCreator(4)+finderFlags(2)+
    #                      location.Point(4)+reservedField(2) = 16
    user_info = struct.pack(">II", ftype, creator) + b"\x00" * 8
    r  = struct.pack(_FILE_HDR_FMT, _FILE_REC, 0, 0, cnid,
                     now, now, now, now, 0)   # 32
    r += _bsd(mode)                           # 16
    r += user_info                            # 16  FileInfo
    r += b"\x00" * 16                         # ExtendedFileInfo
    r += struct.pack(_TAIL_FMT, 0, 0)         # 8
    r += _fork(data_size, start, count)       # 80  data fork
    r += _empty_fork()                        # 80  resource fork
    assert len(r) == 248, len(r)
    return r

def _thread_rec(rtype: int, parent: int, name: str) -> bytes:
    """HFSPlusCatalogThread — 10 bytes + Unicode name."""
    return struct.pack(_THREAD_FMT, rtype, 0, 0, parent) + _unistr(name)


# ── B-tree node assembly ────────────────────────────────────────────────────────

def _leaf_node(flink: int, blink: int, records: list[bytes]) -> bytes:
    """
    Build one 4096-byte HFS+ B-tree leaf node.

    Layout:
        BTNodeDescriptor  (14 bytes)
        record[0]…record[N-1]
        <free space padding>
        offset_table[N+1 entries, 2 bytes each, stored in REVERSE at the end>

    The offset table stores the byte-offset of each record from the node start,
    plus one extra "free space" entry.  Apple stores them in reverse order so
    that index 0 is the LAST entry in memory (closest to the node end).
    """
    desc = struct.pack(_NODE_DESC_FMT, flink, blink, -1, 1, len(records), 0)

    offsets = []
    pos = 14
    for r in records:
        offsets.append(pos)
        pos += len(r)
    offsets.append(pos)   # free-space sentinel

    table = b"".join(struct.pack(">H", o) for o in reversed(offsets))

    body    = desc + b"".join(records)
    padding = BLOCK - len(body) - len(table)
    if padding < 0:
        raise OverflowError(
            f"Leaf node too large: {len(body)} data + {len(table)} table "
            f"= {len(body)+len(table)} > {BLOCK}"
        )
    return body + b"\x00" * padding + table


def _header_node(total: int, free: int, root: int,
                 first: int, last: int, n_recs: int,
                 depth: int, max_key: int) -> bytes:
    """Build the 4096-byte B-tree header node (node index 0)."""
    desc = struct.pack(_NODE_DESC_FMT, 0, 0, 1, 0, 3, 0)   # kind=1 header

    # BTHeaderRec (106 bytes) — 16I in _BTH_FMT covers reserved3[16] already
    hdr = struct.pack(
        _BTH_FMT,
        depth,        # treeDepth
        root,         # rootNode
        n_recs,       # leafRecords
        first,        # firstLeafNode
        last,         # lastLeafNode
        BLOCK,        # nodeSize
        max_key,      # maxKeyLength
        total,        # totalNodes
        free,         # freeNodes
        0,            # reserved1
        BLOCK,        # clumpSize
        0,            # btreeType (0=hfs catalog)
        0xBC,         # keyCompareType (0xBC=case-folding)
        0x00000006,   # attributes: bigKeys|variableIndexKeys
        *([0] * 16),  # reserved3[16]
    )
    assert len(hdr) == 106, len(hdr)

    user = b"\x00" * 128   # user data record (unused)

    # Map record: one bit per node.  Mark header (0), root, first–last leaves used.
    map_size = BLOCK - 14 - len(hdr) - len(user) - (4 * 2)   # 4 offsets × 2 bytes
    bmap = bytearray(map_size)
    for idx in {0, root, *range(first, last + 1)}:
        byte, bit = divmod(idx, 8)
        if byte < map_size:
            bmap[byte] |= 0x80 >> bit

    records = [hdr, user, bytes(bmap)]
    offsets = [14]
    p = 14
    for r in records:
        p += len(r)
        offsets.append(p)
    table = b"".join(struct.pack(">H", o) for o in reversed(offsets))

    node = desc + hdr + user + bytes(bmap)
    assert len(node) + len(table) == BLOCK, \
        f"Header node: {len(node)}+{len(table)}={len(node)+len(table)} ≠ {BLOCK}"
    return node + table


# ── catalog B-tree builder ──────────────────────────────────────────────────────

def _key_order(key: bytes) -> tuple:
    """Sort key: (parentID, lowercase-name) — matches HFS+ catalog ordering."""
    parent = struct.unpack_from(">I", key, 2)[0]
    nlen   = struct.unpack_from(">H", key, 6)[0]
    name   = key[8 : 8 + nlen * 2].decode("utf-16-be", errors="replace").lower()
    return (parent, name)

def _build_catalog(pairs: list[tuple[bytes, bytes]]) -> tuple[bytes, int]:
    """
    Pack (key, value) pairs into a minimal HFS+ catalog B-tree.
    Returns (btree_bytes, max_key_len).
    """
    pairs = sorted(pairs, key=lambda kv: _key_order(kv[0]))
    max_key = max(len(k) for k, _ in pairs)

    # Pack into leaf nodes
    nodes: list[list[bytes]] = [[]]
    used = 14   # descriptor

    for key, val in pairs:
        rec = key + val
        # +2 for new offset entry, +2 for sentinel (always present)
        need = len(rec) + 2
        table_now = (len(nodes[-1]) + 2) * 2
        if nodes[-1] and used + need + table_now > BLOCK:
            nodes.append([])
            used = 14
        nodes[-1].append(rec)
        used += len(rec)

    n = len(nodes)
    leaf_bytes = []
    for i, recs in enumerate(nodes):
        flink = (i + 2) if i < n - 1 else 0
        blink =  i      if i > 0     else 0
        leaf_bytes.append(_leaf_node(flink, blink, recs))

    n_recs = sum(len(nd) for nd in nodes)
    first  = 1
    last   = n
    total  = 1 + n   # header + leaves
    head   = _header_node(total, 0, first, first, last, n_recs, 1, max_key)

    return head + b"".join(leaf_bytes), max_key


# ── HFS+ volume header ──────────────────────────────────────────────────────────

def _volume_header(
    total_blocks: int, free_blocks: int, file_count: int, folder_count: int,
    now: int, next_cnid: int,
    alloc_start: int,   alloc_blocks: int,
    ext_start: int,     ext_blocks: int,    ext_size: int,
    cat_start: int,     cat_blocks: int,    cat_size: int,
) -> bytes:
    """Return the 512-byte HFSPlusVolumeHeader."""
    scalars = struct.pack(
        _VH_FMT,
        0x482B,        # signature 'H+'
        0x0004,        # version 4
        (1 << 8),      # attributes: kHFSVolumeUnmountedMask — skip fsck on mount
        0x31302E30,    # lastMountedVersion '10.0'
        0,             # journalInfoBlock (0 = not journaled)
        now,           # createDate
        now,           # contentModDate
        0,             # backupDate
        now,           # checkedDate
        file_count,
        folder_count,
        BLOCK,         # blockSize
        total_blocks,
        free_blocks,
        total_blocks // 8,   # nextAllocation hint
        BLOCK * 4,     # rsrcClumpSize
        BLOCK * 4,     # dataClumpSize
        next_cnid,
        1,             # writeCount
        0,             # encodingsBitmap
        *([0] * 8),    # finderInfo[8]
    )
    forks = (
        _fork(alloc_blocks * BLOCK, alloc_start, alloc_blocks)
        + _fork(ext_size,           ext_start,   ext_blocks)
        + _fork(cat_size,           cat_start,   cat_blocks)
        + _empty_fork()   # attributes file
        + _empty_fork()   # startup file
    )
    vh = scalars + forks
    assert len(vh) == 512, f"VolumeHeader: {len(vh)} bytes (expected 512)"
    return vh


# ── staging directory walker ────────────────────────────────────────────────────

class _Entry:
    __slots__ = ("kind", "parent", "name", "cnid", "mode", "data")

    def __init__(self, kind: str, parent: int, name: str, cnid: int,
                 mode: int, data: bytes | None):
        self.kind   = kind    # "file", "dir", "symlink"
        self.parent = parent
        self.name   = name
        self.cnid   = cnid
        self.mode   = mode
        self.data   = data    # file/symlink bytes; None for dirs

def _walk(path: Path, parent_cnid: int, counter: list[int]) -> list[_Entry]:
    """Recursively collect filesystem entries, assigning CNIDs."""
    entries: list[_Entry] = []
    for item in sorted(path.iterdir(), key=lambda p: p.name.lower()):
        cnid = counter[0]
        counter[0] += 1
        name = item.name

        if item.is_symlink():
            target = os.readlink(item)
            entries.append(_Entry("symlink", parent_cnid, name, cnid,
                                  0o120777, target.encode("utf-8")))
        elif item.is_dir():
            entries.append(_Entry("dir", parent_cnid, name, cnid, 0o40755, None))
            entries.extend(_walk(item, cnid, counter))
        elif item.is_file():
            mode = item.stat().st_mode & 0xFFFF
            entries.append(_Entry("file", parent_cnid, name, cnid, mode,
                                  item.read_bytes()))
    return entries


# ── main image builder ──────────────────────────────────────────────────────────

def build_dmg(staging: Path, output: Path, label: str) -> Path:
    """
    Build a proper Apple UDIF DMG containing an HFS+ volume
    with the contents of *staging*.  Writes to *output* and returns it.
    """
    now          = _hfs_now()
    cnid_counter = [_CNID_FIRST_USER]

    # ── walk staging ─────────────────────────────────────────────────────────
    entries = _walk(staging, _CNID_ROOT, cnid_counter)
    next_cnid = cnid_counter[0]

    files    = [e for e in entries if e.kind in ("file", "symlink")]
    dirs     = [e for e in entries if e.kind == "dir"]
    file_count   = len(files)
    folder_count = len(dirs) + 1   # +1 for the root

    # ── lay out file data blocks ─────────────────────────────────────────────
    # We'll compute positions after we know how large the catalog is.
    # First, calculate catalog B-tree to know its block count.

    # Build catalog records (key, value) pairs
    pairs: list[tuple[bytes, bytes]] = []

    # Root directory thread
    pairs.append((_thread_key(_CNID_ROOT),
                  _thread_rec(_FOLDER_THREAD_REC, _CNID_ROOT_PARENT, label)))

    # Root directory folder record
    root_valence = len([e for e in entries if e.parent == _CNID_ROOT])
    pairs.append((_catalog_key(_CNID_ROOT_PARENT, label),
                  _folder_rec(_CNID_ROOT, root_valence, now)))

    # Placeholder start blocks for files (we fill real values below)
    file_starts: dict[int, int] = {}   # cnid → start block

    for e in entries:
        if e.kind == "dir":
            valence = len([x for x in entries if x.parent == e.cnid])
            pairs.append((_thread_key(e.cnid),
                          _thread_rec(_FOLDER_THREAD_REC, e.parent, e.name)))
            pairs.append((_catalog_key(e.parent, e.name),
                          _folder_rec(e.cnid, valence, now, e.mode)))
        else:
            # file or symlink — use placeholder start=0 for now
            if e.kind == "symlink":
                val = _file_rec(e.cnid, len(e.data), 0, 0, now,
                                e.mode, _SLNK_TYPE, _SLNK_CREATOR)
            else:
                nblocks = max(1, math.ceil(len(e.data) / BLOCK)) if e.data else 0
                val = _file_rec(e.cnid, len(e.data), 0, nblocks, now, e.mode)
            pairs.append((_thread_key(e.cnid),
                          _thread_rec(_FILE_THREAD_REC, e.parent, e.name)))
            pairs.append((_catalog_key(e.parent, e.name), val))

    # Build catalog to measure it
    cat_bytes, max_key = _build_catalog(pairs)
    cat_blocks = math.ceil(len(cat_bytes) / BLOCK)
    cat_size   = len(cat_bytes)

    # ── assign final block positions ─────────────────────────────────────────
    # Layout: [0:reserved+VH][1:bitmap][2:extents][3..3+cat:catalog][data...]
    data_start = _CAT_START + cat_blocks

    # Compute file positions and total data size
    pos = data_start
    positions: dict[int, tuple[int, int]] = {}   # cnid → (start, count)
    for e in files:
        sz = len(e.data) if e.data else 0
        nb = math.ceil(sz / BLOCK) if sz else 0
        positions[e.cnid] = (pos, nb)
        pos += nb

    total_data_end = pos
    # One extra block for the Alternate Volume Header.
    # Layout (fixed): [0:VH][1:bitmap][2:extents][3..3+cat-1:catalog][data...][last:altVH]
    # alloc_blocks=1 supports up to 32768 blocks = 128 MB.
    # ext_blocks=1  is sufficient for an empty extents overflow tree.
    total_blocks = total_data_end + 1
    alloc_blocks = 1
    ext_blocks   = 1
    ext_size     = BLOCK

    data_blocks = sum(nb for _, nb in positions.values())
    # All blocks are allocated; free_blocks=0 is valid for a distribution image.
    used_blocks = 1 + alloc_blocks + ext_blocks + cat_blocks + data_blocks + 1
    free_blocks = max(0, total_blocks - used_blocks)

    # ── rebuild catalog with real block positions ────────────────────────────
    pairs2: list[tuple[bytes, bytes]] = []

    pairs2.append((_thread_key(_CNID_ROOT),
                   _thread_rec(_FOLDER_THREAD_REC, _CNID_ROOT_PARENT, label)))
    pairs2.append((_catalog_key(_CNID_ROOT_PARENT, label),
                   _folder_rec(_CNID_ROOT, root_valence, now)))

    for e in entries:
        if e.kind == "dir":
            valence = len([x for x in entries if x.parent == e.cnid])
            pairs2.append((_thread_key(e.cnid),
                           _thread_rec(_FOLDER_THREAD_REC, e.parent, e.name)))
            pairs2.append((_catalog_key(e.parent, e.name),
                           _folder_rec(e.cnid, valence, now, e.mode)))
        else:
            s, nb = positions.get(e.cnid, (0, 0))
            sz = len(e.data) if e.data else 0
            if e.kind == "symlink":
                val = _file_rec(e.cnid, sz, s, nb, now,
                                e.mode, _SLNK_TYPE, _SLNK_CREATOR)
            else:
                val = _file_rec(e.cnid, sz, s, nb, now, e.mode)
            pairs2.append((_thread_key(e.cnid),
                           _thread_rec(_FILE_THREAD_REC, e.parent, e.name)))
            pairs2.append((_catalog_key(e.parent, e.name), val))

    cat_bytes, _ = _build_catalog(pairs2)
    cat_blocks2  = math.ceil(len(cat_bytes) / BLOCK)
    assert cat_blocks2 == cat_blocks, \
        "Catalog size changed between passes — logic error"

    # ── build allocation bitmap ──────────────────────────────────────────────
    bmap = bytearray(math.ceil(total_blocks / 8))
    def _mark(blk: int) -> None:
        bmap[blk // 8] |= 0x80 >> (blk % 8)

    _mark(0)   # reserved/VH block
    for b in range(_ALLOC_BLOCK, _ALLOC_BLOCK + alloc_blocks):
        _mark(b)
    for b in range(_EXTENTS_BLOCK, _EXTENTS_BLOCK + ext_blocks):
        _mark(b)
    for b in range(_CAT_START, _CAT_START + cat_blocks):
        _mark(b)
    for e in files:
        s, nb = positions[e.cnid]
        for b in range(s, s + nb):
            _mark(b)
    _mark(total_blocks - 1)   # alt VH block

    bmap_padded = bytes(bmap).ljust(alloc_blocks * BLOCK, b"\x00")

    # ── build extents overflow B-tree (header node only — tree is always empty) ─
    ext_tree = _header_node(1, 0, 0, 0, 0, 0, 0, 0)

    # ── build volume header ──────────────────────────────────────────────────
    vh = _volume_header(
        total_blocks, free_blocks, file_count, folder_count,
        now, next_cnid,
        _ALLOC_BLOCK,   alloc_blocks,
        _EXTENTS_BLOCK, ext_blocks,   ext_size,
        _CAT_START,     cat_blocks,   cat_size,
    )

    # ── assemble image ───────────────────────────────────────────────────────
    img = bytearray(total_blocks * BLOCK)

    # Block 0: two 512-byte reserved sectors, then VolumeHeader at byte 1024
    img[1024:1536] = vh

    # Allocation bitmap
    ab = _ALLOC_BLOCK * BLOCK
    img[ab : ab + len(bmap_padded)] = bmap_padded

    # Extents overflow B-tree
    eb = _EXTENTS_BLOCK * BLOCK
    img[eb : eb + len(ext_tree)] = ext_tree

    # Catalog B-tree
    cb = _CAT_START * BLOCK
    cat_padded = cat_bytes.ljust(cat_blocks * BLOCK, b"\x00")
    img[cb : cb + len(cat_padded)] = cat_padded

    # File data
    for e in files:
        s, nb = positions[e.cnid]
        if e.data:
            off = s * BLOCK
            img[off : off + len(e.data)] = e.data

    # Alternate Volume Header at (totalSize - 1024), i.e. start of last block + 1024
    alt_off = (total_blocks - 1) * BLOCK + 1024
    img[alt_off : alt_off + 512] = vh

    raw = bytes(img)
    assert len(raw) % 512 == 0

    # ── wrap in Apple UDIF format ────────────────────────────────────────────
    _wrap_udif(raw, output, label)
    return output


# ── UDIF wrapper ────────────────────────────────────────────────────────────────

def _mish_block(sector_count: int) -> bytes:
    """
    Build the binary 'mish' block that describes the block map for one partition.
    Two blkx_run entries: one raw data run + one end-of-descriptor sentinel.
    """
    # Each blkx_run: type(I)+reserved(I)+sector(Q)+count(Q)+offset(Q)+length(Q) = 40 bytes
    run_raw = struct.pack(">II QQ QQ",
        0x00000001,           # type: uncompressed raw
        0,
        0,                    # sector number
        sector_count,         # sector count
        0,                    # compressed offset
        sector_count * 512,   # compressed length (= uncompressed for raw)
    )
    run_end = struct.pack(">II QQ QQ",
        0xFFFFFFFF,           # type: end of descriptor
        0, sector_count, 0, sector_count * 512, 0,
    )
    runs = run_raw + run_end

    # CRC32 of the run data goes into the mish checksum
    crc = zlib.crc32(runs) & 0xFFFFFFFF

    # mish header: sig(I)+ver(I)+firstSector(Q)+sectorCount(Q)+dataStart(Q)+
    #              buffersNeeded(I)+blockDescriptors(I)+reserved(6I)+
    #              checksum_type(I)+checksum_size(I)+checksum_data(32I)+
    #              numberOfBlockChunks(I)
    header = struct.pack(
        ">II QQQ II IIIIII II",
        0x6D697368,   # 'mish'
        1,            # version
        0,            # firstSectorNumber
        sector_count,
        0,            # dataStart
        0,            # buffersNeeded
        2,            # blockDescriptors (2 runs)
        0, 0, 0, 0, 0, 0,   # reserved[6]
        2,            # checksum type: CRC32
        32,           # checksum size (bits)
    )
    checksum_data = struct.pack(">I", crc) + b"\x00" * (31 * 4)
    n_chunks = struct.pack(">I", 2)

    return header + checksum_data + n_chunks + runs


def _wrap_udif(raw_image: bytes, output: Path, label: str) -> None:
    """
    Append an Apple UDIF (koly) trailer to a raw disk image and write the DMG.

    File layout:
        [raw HFS+ image bytes]
        [blkx XML plist]
        [koly 512-byte trailer]
    """
    sector_count = len(raw_image) // 512
    plist_offset = len(raw_image)

    mish = _mish_block(sector_count)

    # The XML plist wraps the mish block as base64 <data>
    blkx_array = [
        {
            "Attributes": "0x0050",
            "CFName":     label,
            "Data":       mish,
            "ID":         "-1",
            "Name":       label,
        }
    ]
    plist_bytes = plistlib.dumps(blkx_array, fmt=plistlib.FMT_XML)

    # CRC32 of the raw image data (used for both checksums)
    data_crc = zlib.crc32(raw_image) & 0xFFFFFFFF

    def _ck(crc: int) -> bytes:
        """136-byte UDIFChecksum: type(4)+size(4)+crc(4)+zeros(124)."""
        return struct.pack(">III", 2, 32, crc) + b"\x00" * 124

    seg_guid = uuid.uuid4().bytes

    koly = (
        b"koly"
        + struct.pack(">III", 4, 512, 1)                       # version, hdr_size, flags
        + struct.pack(">QQQ", 0, 0, len(raw_image))            # run_off, data_off, data_len
        + struct.pack(">QQ",  0, 0)                            # rsrc_off, rsrc_len
        + struct.pack(">II", 1, 1)                             # seg_num, seg_count
        + seg_guid                                              # 16 bytes
        + _ck(data_crc)                                        # DataForkChecksum (136)
        + struct.pack(">QQ", plist_offset, len(plist_bytes))   # xml_off, xml_len
        + b"\x00" * 120                                        # reserved
        + _ck(data_crc)                                        # MasterChecksum (136)
        + struct.pack(">IQ", 1, sector_count)                  # variant=UDRO, sectors
        + b"\x00" * 12                                         # reserved
    )
    assert len(koly) == 512, f"koly block is {len(koly)} bytes"

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(raw_image + plist_bytes + koly)
