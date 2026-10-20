"""Huffman-coded JBIG2 cases (symbol dictionary + text region) for jbig2gen.py."""
import os
import re
import struct

import numpy as np

import jbig2gen as g

_STD = None


def _load_standard_tables():
    """The standard tables, read from the decoder's own source so the two cannot drift apart; MuPDF cross-checks them."""
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, "..", "..", "..", "..", "..", "src", "PeachImage", "Formats", "Jbig2", "Decoding", "Jbig2HuffmanTable.cs")
    src = open(path, encoding="utf8").read()
    body = src[src.index("return number switch"):]
    tables = {}
    for m in re.finditer(r"(\d+|_) =>\s*\[(.*?)\],", body, re.S):
        number = 15 if m.group(1) == "_" else int(m.group(1))
        lines = []
        for kind, args in re.findall(r"([NLUO])\(([^)]*)\)", m.group(2)):
            vals = [int(v) for v in args.replace(" ", "").split(",")]
            lines.append((kind, vals))
        tables[number] = lines
    return tables


def _assign_codes(lines):
    maxlen = max(l[2] for l in lines)
    counts = [0] * (maxlen + 2)
    for l in lines:
        if l[2] > 0:
            counts[l[2]] += 1
    codes = [None] * len(lines)
    first = 0
    prev = 0
    for length in range(1, maxlen + 1):
        first = (first + prev) << 1
        prev = counts[length]
        cur = first
        for i, l in enumerate(lines):
            if l[2] == length:
                codes[i] = cur
                cur += 1
    return codes


class BitWriter:
    def __init__(self):
        self.buf = bytearray()
        self.cur = 0
        self.n = 0

    def bits(self, value, count):
        for k in range(count - 1, -1, -1):
            self.cur = (self.cur << 1) | ((value >> k) & 1)
            self.n += 1
            if self.n == 8:
                self.buf.append(self.cur)
                self.cur = 0
                self.n = 0

    def align(self):
        while self.n:
            self.bits(0, 1)

    def raw(self, data):
        self.align()
        self.buf.extend(data)

    def data(self):
        self.align()
        return bytes(self.buf)


class HuffTable:
    def __init__(self, lines):
        self.lines = lines
        self.codes = _assign_codes(lines)

    @staticmethod
    def standard(n):
        global _STD
        if _STD is None:
            _STD = _load_standard_tables()
        lines = []
        for kind, v in _STD[n]:
            if kind == "N":
                lines.append(("N", v[0], v[1], v[2]))
            elif kind == "L":
                lines.append(("L", v[0], v[1], 32))
            elif kind == "U":
                lines.append(("U", v[0], v[1], 32))
            else:
                lines.append(("O", 0, v[0], 0))
        return HuffTable(lines)

    def write(self, bw, value):
        for (kind, low, plen, rlen), code in zip(self.lines, self.codes):
            if plen == 0:
                continue
            if value is None:
                if kind == "O":
                    bw.bits(code, plen)
                    return
                continue
            if kind == "N" and low <= value < low + (1 << rlen):
                bw.bits(code, plen)
                bw.bits(value - low, rlen)
                return
            if kind == "L" and value <= low:
                bw.bits(code, plen)
                bw.bits(low - value, 32)
                return
            if kind == "U" and value >= low:
                bw.bits(code, plen)
                bw.bits(value - low, 32)
                return
        raise ValueError("value %r has no code" % (value,))


def sym_dict_huffman(glyphs, number=1, dh_sel=0, dw_sel=0, mmr_bitmap=False):
    dh_t = HuffTable.standard(4 if dh_sel == 0 else 5)
    dw_t = HuffTable.standard(2 if dw_sel == 0 else 3)
    b1 = HuffTable.standard(1)
    order = sorted(range(len(glyphs)), key=lambda i: (glyphs[i].shape[0], glyphs[i].shape[1], i))
    bw = BitWriter()
    height = 0
    i = 0
    while i < len(order):
        h = glyphs[order[i]].shape[0]
        dh_t.write(bw, h - height)
        height = h
        width = 0
        group = []
        while i < len(order) and glyphs[order[i]].shape[0] == h:
            gl = glyphs[order[i]]
            dw_t.write(bw, gl.shape[1] - width)
            width = gl.shape[1]
            group.append(gl)
            i += 1
        dw_t.write(bw, None)
        collective = np.concatenate(group, axis=1)
        if mmr_bitmap:
            data = g.mmr_encode(collective)
            b1.write(bw, len(data))
            bw.raw(data)
        else:
            b1.write(bw, 0)
            bw.align()
            stride = (collective.shape[1] + 7) // 8
            for row in collective:
                padded = np.zeros(stride * 8, dtype=np.uint8)
                padded[:len(row)] = row
                for k in range(stride):
                    bw.bits(int("".join(str(int(v)) for v in padded[k * 8:k * 8 + 8]), 2), 8)
    b1.write(bw, 0)
    b1.write(bw, len(glyphs))
    flags = 1 | (dh_sel << 2) | (dw_sel << 4)
    head = struct.pack(">H", flags) + struct.pack(">II", len(glyphs), len(glyphs))
    return g.seg(number, 0, 1, [], head + bw.data()), [glyphs[k] for k in order]


def text_region_huffman(symbols, instances, w, h, number, refs, fs_sel=0, ds_sel=0, dt_sel=0, log_strips=0, corner=1, ds_offset=0):
    fs_t = HuffTable.standard(6 + fs_sel)
    ds_t = HuffTable.standard(8 + ds_sel)
    dt_t = HuffTable.standard(11 + dt_sel)
    strips = 1 << log_strips
    n = len(symbols)
    lengths = [3] * (n - 2) + [4, 4]
    assert sum(2.0 ** -l for l in lengths) <= 1.0
    sym_table = HuffTable([("N", i, l, 0) for i, l in enumerate(lengths)])
    run_lens = [0] * 35
    run_lens[3], run_lens[4], run_lens[32], run_lens[33], run_lens[34] = 2, 2, 2, 3, 3
    run_table = HuffTable([("N", i, l, 0) for i, l in enumerate(run_lens)])
    bw = BitWriter()
    for l in run_lens:
        bw.bits(l, 4)
    k = 0
    while k < n:
        l = lengths[k]
        if k > 0 and lengths[k - 1] == l:
            run = 1
            while k + run < n and lengths[k + run] == l and run < 6:
                run += 1
            if run >= 3:
                run_table.write(bw, 32)
                bw.bits(run - 3, 2)
                k += run
                continue
        run_table.write(bw, l)
        k += 1
    bw.align()
    right = corner in (2, 3)
    bottom = corner in (0, 2)
    ordered = sorted(instances, key=lambda it: (it[2] // strips, it[1]))
    dt_t.write(bw, 1)  # STRIPT starts at -strips
    stript = -strips
    firsts = 0
    idx = 0
    placed = []
    while idx < len(ordered):
        strip_t = (ordered[idx][2] // strips) * strips
        dt_t.write(bw, (strip_t - stript) // strips)
        stript = strip_t
        first = True
        curs_after = 0
        while idx < len(ordered) and (ordered[idx][2] // strips) * strips == strip_t:
            sid, s, t = ordered[idx]
            sh, sw = symbols[sid].shape
            raw = s - (sw - 1) if right else s
            if first:
                fs_t.write(bw, raw - firsts)
                firsts = raw
                first = False
            else:
                ds_t.write(bw, raw - curs_after - ds_offset)
            if strips > 1:
                bw.bits(t - strip_t, log_strips)
            sym_table.write(bw, sid)
            curs_after = raw + (sw - 1)
            placed.append((symbols[sid], s, t))
            idx += 1
        ds_t.write(bw, None)
    flags = 1 | (log_strips << 2) | (corner << 4) | ((ds_offset & 0x1F) << 10)
    hflags = fs_sel | (ds_sel << 2) | (dt_sel << 4)
    head = g.region_info(w, h, 0, 0, 0) + struct.pack(">HH", flags, hflags) + struct.pack(">I", len(instances))
    region = np.zeros((h, w), dtype=np.uint8)
    for bmp, s, t in placed:
        bh, bw_ = bmp.shape
        left = s - bw_ + 1 if right else s
        top = t - bh + 1 if bottom else t
        g.compose(region, bmp, left, top, 0)
    return g.seg(number, 6, 1, list(refs), head + bw.data()), region


def huffman_case(name, dh_sel=0, dw_sel=0, fs_sel=0, ds_sel=0, dt_sel=0, log_strips=0, corner=1, mmr_bitmap=False, ds_offset=0):
    glyphs = [g.make_glyph(g.rng.randint(3, 12), g.rng.randint(4, 14)) for _ in range(9)]
    dict_seg, ordered = sym_dict_huffman(glyphs, 1, dh_sel, dw_sel, mmr_bitmap)
    w, h = 190, 100
    instances = []
    strips = 1 << log_strips
    for line in range(5):
        s = 5
        base_t = 12 + line * 18
        while s < w - 30:
            sid = g.rng.randrange(len(ordered))
            t = base_t + (g.rng.randrange(strips) if strips > 1 else 0)
            instances.append((sid, s, t))
            s += ordered[sid].shape[1] + g.rng.randint(1, 5)
    text_seg, region = text_region_huffman(ordered, instances, w, h, 2, [1], fs_sel, ds_sel, dt_sel, log_strips, corner, ds_offset)
    segs_page = [g.mk_page(w, h), text_seg, g.seg(3, 49, 1, [], b"")]
    dict_seg_g = g.seg(1, 0, 0, [], dict_seg[1])
    return g.Case(name, region, segs_page, [dict_seg_g])


def cases():
    return [
        huffman_case("huffman_basic"),
        huffman_case("huffman_tables_b5_b3_b7_b9_b12", dh_sel=1, dw_sel=1, fs_sel=1, ds_sel=1, dt_sel=1),
        huffman_case("huffman_b10_b13_strips", ds_sel=2, dt_sel=2, log_strips=2),
        huffman_case("huffman_mmr_symbols", mmr_bitmap=True),
        huffman_case("huffman_bottomright", corner=2, ds_offset=3),
    ]
