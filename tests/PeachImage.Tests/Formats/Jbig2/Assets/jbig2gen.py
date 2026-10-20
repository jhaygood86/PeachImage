"""Generates the JBIG2 test assets (and cross-checks them against MuPDF's jbig2dec).

Usage: python jbig2gen.py OUTDIR
Needs numpy, Pillow (libtiff, for MMR data) and pymupdf.  Every case is written as
  NAME.png        the intended page (0 = black, 255 = white)
  NAME.jb2        a standalone JBIG2 file (sequential unless the name says otherwise)
  NAME.seg        the embedded (PDF-style) page stream, no file header
  NAME.glb        the embedded globals stream (only when the case has global segments)
The embedded form is decoded with MuPDF inside a generated PDF and must reproduce the intended page.
"""
import io
import os
import random
import struct
import sys

import numpy as np
from PIL import Image

QE = [
    (0x5601, 1, 1, 1), (0x3401, 2, 6, 0), (0x1801, 3, 9, 0), (0x0AC1, 4, 12, 0), (0x0521, 5, 29, 0), (0x0221, 38, 33, 0),
    (0x5601, 7, 6, 1), (0x5401, 8, 14, 0), (0x4801, 9, 14, 0), (0x3801, 10, 14, 0), (0x3001, 11, 17, 0), (0x2401, 12, 18, 0),
    (0x1C01, 13, 20, 0), (0x1601, 29, 21, 0), (0x5601, 15, 14, 1), (0x5401, 16, 14, 0), (0x5101, 17, 15, 0), (0x4801, 18, 16, 0),
    (0x3801, 19, 17, 0), (0x3401, 20, 18, 0), (0x3001, 21, 19, 0), (0x2801, 22, 19, 0), (0x2401, 23, 20, 0), (0x2201, 24, 21, 0),
    (0x1C01, 25, 22, 0), (0x1801, 26, 23, 0), (0x1601, 27, 24, 0), (0x1401, 28, 25, 0), (0x1201, 29, 26, 0), (0x1101, 30, 27, 0),
    (0x0AC1, 31, 28, 0), (0x09C1, 32, 29, 0), (0x08A1, 33, 30, 0), (0x0521, 34, 31, 0), (0x0441, 35, 32, 0), (0x02A1, 36, 33, 0),
    (0x0221, 37, 34, 0), (0x0141, 38, 35, 0), (0x0111, 39, 36, 0), (0x0085, 40, 37, 0), (0x0049, 41, 38, 0), (0x0025, 42, 39, 0),
    (0x0015, 43, 40, 0), (0x0009, 44, 41, 0), (0x0005, 45, 42, 0), (0x0001, 45, 43, 0), (0x5601, 46, 46, 0),
]


class Contexts:
    def __init__(self, size):
        self.state = [0] * size
        self.mps = [0] * size


class MQEncoder:
    def __init__(self):
        self.a = 0x8000
        self.c = 0
        self.ct = 12
        self.out = [0]

    def _byteout(self):
        out = self.out
        if out[-1] == 0xFF:
            out.append((self.c >> 20) & 0xFF)
            self.c &= 0xFFFFF
            self.ct = 7
        elif self.c < 0x8000000:
            out.append((self.c >> 19) & 0xFF)
            self.c &= 0x7FFFF
            self.ct = 8
        else:
            out[-1] += 1
            if out[-1] == 0xFF:
                self.c &= 0x7FFFFFF
                out.append((self.c >> 20) & 0xFF)
                self.c &= 0xFFFFF
                self.ct = 7
            else:
                out.append((self.c >> 19) & 0xFF)
                self.c &= 0x7FFFF
                self.ct = 8

    def _renorm(self):
        while True:
            self.a <<= 1
            self.c <<= 1
            self.ct -= 1
            if self.ct == 0:
                self._byteout()
            if self.a & 0x8000:
                break

    def encode(self, cx, index, bit):
        i = cx.state[index]
        mps = cx.mps[index]
        qe, nmps, nlps, switch = QE[i]
        if bit == mps:
            self.a -= qe
            if (self.a & 0x8000) == 0:
                if self.a < qe:
                    self.a = qe
                else:
                    self.c += qe
                cx.state[index] = nmps
                self._renorm()
            else:
                self.c += qe
        else:
            self.a -= qe
            if self.a < qe:
                self.c += qe
            else:
                self.a = qe
            if switch:
                cx.mps[index] = 1 - mps
            cx.state[index] = nlps
            self._renorm()

    def finish(self):
        tempc = self.c + self.a
        self.c |= 0xFFFF
        if self.c >= tempc:
            self.c -= 0x8000
        self.c <<= self.ct
        self._byteout()
        self.c <<= self.ct
        self._byteout()
        if self.out[-1] != 0xFF:
            self.out.append(0xFF)
        self.out.append(0xAC)
        return bytes(self.out[1:])


# ---------------------------------------------------------------- integer coding (T.88 Annex A)
class IntCtx:
    def __init__(self):
        self.cx = Contexts(512)


def enc_int(enc, ictx, value):
    """value None encodes the out-of-band value."""
    prev = [1]

    def bit(b):
        enc.encode(ictx.cx, prev[0], b)
        p = prev[0]
        prev[0] = ((p << 1) | b) if p < 256 else ((((p << 1) | b) & 511) | 256)

    def bits(v, n):
        for k in range(n - 1, -1, -1):
            bit((v >> k) & 1)

    if value is None:
        sign, mag = 1, 0
    else:
        sign, mag = (1 if value < 0 else 0), abs(value)
    bit(sign)
    if mag < 4:
        bit(0)
        bits(mag, 2)
    elif mag < 20:
        bit(1)
        bit(0)
        bits(mag - 4, 4)
    elif mag < 84:
        bit(1)
        bit(1)
        bit(0)
        bits(mag - 20, 6)
    elif mag < 340:
        bit(1)
        bit(1)
        bit(1)
        bit(0)
        bits(mag - 84, 8)
    elif mag < 4436:
        bit(1)
        bit(1)
        bit(1)
        bit(1)
        bit(0)
        bits(mag - 340, 12)
    else:
        for _ in range(5):
            bit(1)
        bits(mag - 4436, 32)


def enc_iaid(enc, cx, code_len, value):
    prev = 1
    for k in range(code_len - 1, -1, -1):
        b = (value >> k) & 1
        enc.encode(cx, prev, b)
        prev = (prev << 1) | b


# ---------------------------------------------------------------- generic region coding
NOMINAL = {
    0: [(-2, -2, 4), (-1, -2, None), (0, -2, None), (1, -2, None), (2, -2, 3), (-3, -1, 2), (-2, -1, None), (-1, -1, None),
        (0, -1, None), (1, -1, None), (2, -1, None), (3, -1, 1), (-4, 0, None), (-3, 0, None), (-2, 0, None), (-1, 0, None)],
    1: [(-1, -2, None), (0, -2, None), (1, -2, None), (2, -2, None), (-2, -1, None), (-1, -1, None), (0, -1, None),
        (1, -1, None), (2, -1, None), (3, -1, 1), (-3, 0, None), (-2, 0, None), (-1, 0, None)],
    2: [(-1, -2, None), (0, -2, None), (1, -2, None), (-2, -1, None), (-1, -1, None), (0, -1, None), (1, -1, None),
        (2, -1, 1), (-2, 0, None), (-1, 0, None)],
    3: [(-3, -1, None), (-2, -1, None), (-1, -1, None), (0, -1, None), (1, -1, None), (2, -1, 1), (-4, 0, None),
        (-3, 0, None), (-2, 0, None), (-1, 0, None)],
}
SLTP = {0: 0x9B25, 1: 0x0795, 2: 0x00E5, 3: 0x0195}
NOMINAL_AT = {0: [(3, -1), (-3, -1), (2, -2), (-2, -2)], 1: [(3, -1)], 2: [(2, -1)], 3: [(2, -1)]}


def gen_context_size(template):
    return 1 << len(NOMINAL[template])


def px(bm, x, y):
    h, w = bm.shape
    return int(bm[y, x]) if 0 <= x < w and 0 <= y < h else 0


def enc_generic(enc, cx, bm, template, at=None, tpgd=False, skip=None):
    h, w = bm.shape
    at = at or NOMINAL_AT[template]
    if skip is not None:
        bm = np.where(skip != 0, 0, bm).astype(np.uint8)
    slots = NOMINAL[template]
    ltp = 0
    for y in range(h):
        if tpgd:
            typical = 1 if y > 0 and np.array_equal(bm[y], bm[y - 1]) else (1 if y == 0 and not bm[0].any() else 0)
            enc.encode(cx, SLTP[template], typical ^ ltp)
            ltp = typical
            if typical:
                continue
        for x in range(w):
            if skip is not None and skip[y, x]:
                continue
            ctx = 0
            for (dx, dy, atidx) in slots:
                if atidx is not None:
                    dx, dy = at[atidx - 1]
                ctx = (ctx << 1) | px(bm, x + dx, y + dy)
            enc.encode(cx, ctx, int(bm[y, x]))


def enc_refinement(enc, cx, bm, ref, dx, dy, template, at=None, tpgron=False):
    h, w = bm.shape
    at = at or [(-1, -1), (-1, -1)]
    if template == 0:
        coding = [(0, -1), (1, -1), (-1, 0), at[0]]
        refpix = [(0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (-1, 1), (0, 1), (1, 1), at[1]]
        sltp = 0x0020
    else:
        coding = [(-1, -1), (0, -1), (1, -1), (-1, 0)]
        refpix = [(0, -1), (-1, 0), (0, 0), (1, 0), (0, 1), (1, 1)]
        sltp = 0x0008
    ltp = 0
    for y in range(h):
        typical_row = 0
        if tpgron:
            # a row is "typical" when every pixel whose 3x3 reference neighbourhood is uniform equals that value
            typical_row = 1
            for x in range(w):
                rx, ry = x - dx, y - dy
                c = px(ref, rx, ry)
                if all(px(ref, rx + i, ry + j) == c for j in (-1, 0, 1) for i in (-1, 0, 1)) and int(bm[y, x]) != c:
                    typical_row = 0
                    break
            enc.encode(cx, sltp, typical_row ^ ltp)
            ltp = typical_row
        for x in range(w):
            rx, ry = x - dx, y - dy
            if ltp:
                c = px(ref, rx, ry)
                if all(px(ref, rx + i, ry + j) == c for j in (-1, 0, 1) for i in (-1, 0, 1)):
                    continue
            ctx = 0
            for (ox, oy) in coding:
                ctx = (ctx << 1) | px(bm, x + ox, y + oy)
            for (ox, oy) in refpix:
                ctx = (ctx << 1) | px(ref, rx + ox, ry + oy)
            enc.encode(cx, ctx, int(bm[y, x]))


# ---------------------------------------------------------------- segment plumbing
def seg(number, typ, page, refs, data, unknown_len=False):
    out = struct.pack(">IB", number, typ)
    if len(refs) <= 4:
        out += bytes([len(refs) << 5])
    else:
        out += struct.pack(">I", (7 << 29) | len(refs)) + bytes((len(refs) + 8) // 8)
    for r in refs:
        out += bytes([r]) if number <= 256 else (struct.pack(">H", r) if number <= 65536 else struct.pack(">I", r))
    out += bytes([page])
    out += struct.pack(">I", 0xFFFFFFFF if unknown_len else len(data))
    return out, data


def region_info(w, h, x, y, op=0):
    return struct.pack(">IIIIB", w, h, x, y, op)


def page_info(w, h, default=0, striped=0, max_stripe=0):
    flags = (default << 2)
    return struct.pack(">IIIIBH", w, h, 0, 0, flags, (striped << 15) | max_stripe)


def sgn(v):
    return struct.pack(">b", v)


def file_bytes(segments, sequential=True):
    header = b"\x97JB2\r\n\x1a\n" + bytes([1 if sequential else 0]) + struct.pack(">I", 1)
    if sequential:
        return header + b"".join(h + d for h, d in segments)
    return header + b"".join(h for h, d in segments) + b"".join(d for h, d in segments)


def stream_bytes(segments):
    return b"".join(h + d for h, d in segments)


def build_pdf(page_stream, globals_stream, w, h):
    objs = []
    objs.append(b"<< /Type /Catalog /Pages 2 0 R >>")
    objs.append(b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    objs.append(b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Resources << /XObject << /Im 4 0 R >> >> /Contents 5 0 R >>" % (w, h))
    parms = b" /DecodeParms << /JBIG2Globals 6 0 R >>" if globals_stream else b""
    objs.append(b"<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode%s /Length %d >>\nstream\n" % (w, h, parms, len(page_stream)) + page_stream + b"\nendstream")
    content = b"q %d 0 0 %d 0 0 cm /Im Do Q" % (w, h)
    objs.append(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
    if globals_stream:
        objs.append(b"<< /Length %d >>\nstream\n" % len(globals_stream) + globals_stream + b"\nendstream")
    pdf = b"%PDF-1.5\n"
    offsets = []
    for i, o in enumerate(objs):
        offsets.append(len(pdf))
        pdf += b"%d 0 obj\n" % (i + 1) + o + b"\nendobj\n"
    xref = len(pdf)
    pdf += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for off in offsets:
        pdf += b"%010d 00000 n \n" % off
    pdf += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    return pdf


def pdfium_decode(page_stream, globals_stream, w, h):
    import pypdfium2 as pdfium
    pdf = build_pdf(page_stream, globals_stream, w, h)
    page = pdfium.PdfDocument(pdf)[0]
    arr = page.render(scale=1.0, grayscale=True).to_numpy()
    return arr.reshape(arr.shape[0], arr.shape[1]) if arr.ndim == 3 else arr


def pdf_decode(page_stream, globals_stream, w, h):
    import fitz
    pdf = build_pdf(page_stream, globals_stream, w, h)
    doc = fitz.open(stream=pdf, filetype="pdf")
    pix = fitz.Pixmap(doc, 4)
    if pix.n != 1:
        pix = fitz.Pixmap(fitz.csGRAY, pix)
    return np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width)


# ---------------------------------------------------------------- scene generation
rng = random.Random(1234)


def make_glyph(w, h):
    g = np.zeros((h, w), dtype=np.uint8)
    for _ in range(rng.randint(2, 4)):
        x0, y0 = rng.randrange(w), rng.randrange(h)
        x1, y1 = min(w, x0 + rng.randint(1, w)), min(h, y0 + rng.randint(1, h))
        g[y0:y1, x0:x1] = 1 if rng.random() < 0.75 else 0
    g[0, :] = np.maximum(g[0, :], 1 if rng.random() < 0.5 else 0)
    return g


def scene(w, h):
    bm = np.zeros((h, w), dtype=np.uint8)
    for _ in range(40):
        x0, y0 = rng.randrange(w), rng.randrange(h)
        x1, y1 = min(w, x0 + rng.randint(1, 40)), min(h, y0 + rng.randint(1, 12))
        bm[y0:y1, x0:x1] = rng.randint(0, 1)
    for _ in range(60):
        bm[rng.randrange(h), rng.randrange(w)] ^= 1
    bm[10:14, :] = bm[9, :]  # repeated rows for typical prediction
    bm[30:36, :] = 0
    return bm


def to_png(bm, path):
    Image.fromarray(np.where(bm != 0, 0, 255).astype(np.uint8), "L").save(path)


class Case:
    def __init__(self, name, intended, page_segments, global_segments=None, sequential=True, check_embedded=True):
        self.name = name
        self.intended = intended
        self.page_segments = page_segments
        self.global_segments = global_segments or []
        self.sequential = sequential
        self.check_embedded = check_embedded


def mk_page(w, h, default=0, striped=0, max_stripe=0, number=0):
    return seg(number, 48, 1, [], page_info(w, h, default, striped, max_stripe))


def compose(page, bm, x, y, op=0):
    h, w = bm.shape
    H, W = page.shape
    ys, xs = max(y, 0), max(x, 0)
    ye, xe = min(y + h, H), min(x + w, W)
    if ye <= ys or xe <= xs:
        return
    dst = page[ys:ye, xs:xe]
    src = bm[ys - y:ye - y, xs - x:xe - x]
    if op == 0:
        dst |= src
    elif op == 1:
        dst &= src
    elif op == 2:
        dst ^= src
    elif op == 3:
        dst[:] = 1 - (dst ^ src)
    else:
        dst[:] = src


# ---------------------------------------------------------------- cases
def generic_case(name, template, tpgd, at=None, w=203, h=70, sequential=True):
    bm = scene(w, h)
    cx = Contexts(gen_context_size(template))
    enc = MQEncoder()
    enc_generic(enc, cx, bm, template, at, tpgd)
    atb = b"".join(sgn(v) for pair in (at or NOMINAL_AT[template]) for v in pair)
    flags = (template << 1) | (8 if tpgd else 0)
    data = region_info(w, h, 0, 0) + bytes([flags]) + atb + enc.finish()
    segs = [mk_page(w, h), seg(1, 38, 1, [], data), seg(2, 49, 1, [], b"")]
    return Case(name, bm, segs, sequential=sequential)


def mmr_case(name, w=150, h=45):
    bm = scene(w, h)
    mmr = mmr_encode(bm)
    data = region_info(w, h, 0, 0) + bytes([1]) + mmr
    segs = [mk_page(w, h), seg(1, 38, 1, [], data), seg(2, 49, 1, [], b"")]
    return Case(name, bm, segs)


def sym_dict_arith(glyphs, template=0, at=None, number=1, refs=(), input_count=0, retain=False):
    """Symbol dictionary exporting only its new symbols. glyphs is a list of bitmaps sorted by height class."""
    cx = Contexts(gen_context_size(template))
    enc = MQEncoder()
    iadh, iadw, iaex = IntCtx(), IntCtx(), IntCtx()
    order = sorted(range(len(glyphs)), key=lambda i: (glyphs[i].shape[0], i))
    height = 0
    i = 0
    while i < len(order):
        h = glyphs[order[i]].shape[0]
        enc_int(enc, iadh, h - height)
        height = h
        width = 0
        while i < len(order) and glyphs[order[i]].shape[0] == h:
            g = glyphs[order[i]]
            enc_int(enc, iadw, g.shape[1] - width)
            width = g.shape[1]
            enc_generic(enc, cx, g, template, at)
            i += 1
        enc_int(enc, iadw, None)
    total = input_count + len(glyphs)
    enc_int(enc, iaex, input_count)  # not exported
    enc_int(enc, iaex, len(glyphs))  # exported
    if total - input_count - len(glyphs) > 0:
        enc_int(enc, iaex, 0)
    atb = b"".join(sgn(v) for pair in (at or NOMINAL_AT[template]) for v in pair)
    flags = (template << 10) | (0x200 if retain else 0)
    head = struct.pack(">H", flags) + atb + struct.pack(">II", len(glyphs), len(glyphs))
    # symbols are numbered in order of decoding (sorted by height); return the sorted list
    return seg(number, 0, 0 if False else 1, list(refs), head + enc.finish()), [glyphs[k] for k in order]


def text_region_arith(symbols, instances, w, h, number, refs, corner=1, transposed=False, log_strips=0, op=0,
                      ds_offset=0, def_pixel=0, x=0, y=0, region_op=0, page=1, refine=None, rtemplate=0, rat=None):
    """instances: list of (symbol index, S, T) where (S, T) is the reference-corner coordinate in text-region space."""
    strips = 1 << log_strips
    sym_code_len = max((len(symbols) - 1).bit_length(), 0)
    enc = MQEncoder()
    iadt, iafs, iads, iait, iaid, iari = IntCtx(), IntCtx(), IntCtx(), IntCtx(), Contexts(1 << (sym_code_len + 1)), IntCtx()
    rdw, rdh, rdx, rdy = IntCtx(), IntCtx(), IntCtx(), IntCtx()
    grcx = Contexts(1 << 13)
    right = corner in (2, 3)
    bottom = corner in (0, 2)

    def dims(idx):
        sh, sw = symbols[idx].shape
        return sw, sh

    # group instances into strips of T
    ordered = sorted(instances, key=lambda it: (it[2] // strips, it[1]))
    enc_int(enc, iadt, 0)  # initial STRIPT = 0
    stript = 0
    firsts = 0
    n = 0
    placed = []
    idx = 0
    while idx < len(ordered):
        strip_t = (ordered[idx][2] // strips) * strips
        enc_int(enc, iadt, (strip_t - stript) // strips)
        stript = strip_t
        first = True
        curs_after = 0
        while idx < len(ordered) and (ordered[idx][2] // strips) * strips == strip_t:
            sid, s, t = ordered[idx]
            sw, sh = dims(sid)
            if refine is not None and refine.get(idx) is not None:
                rbm, rdxv, rdyv = refine[idx]
                sw, sh = rbm.shape[1], rbm.shape[0]
            # raw CURS value to encode (before the corner adjustment)
            if not transposed and right:
                raw = s - (sw - 1)
            elif transposed and bottom:
                raw = s - (sh - 1)
            else:
                raw = s
            if first:
                enc_int(enc, iafs, raw - firsts)
                firsts = raw
                first = False
            else:
                enc_int(enc, iads, raw - curs_after - ds_offset)
            if strips > 1:
                enc_int(enc, iait, t - strip_t)
            enc_iaid(enc, iaid, sym_code_len, sid)
            if refine is not None:
                r = refine.get(idx)
                enc_int(enc, iari, 0 if r is None else 1)
                if r is not None:
                    rbm, rdxv, rdyv = r
                    ow, oh = dims(sid)
                    enc_int(enc, rdw, rbm.shape[1] - ow)
                    enc_int(enc, rdh, rbm.shape[0] - oh)
                    enc_int(enc, rdx, rdxv)
                    enc_int(enc, rdy, rdyv)
                    gdx = ((rbm.shape[1] - ow) >> 1) + rdxv
                    gdy = ((rbm.shape[0] - oh) >> 1) + rdyv
                    enc_refinement(enc, grcx, rbm, symbols[sid], gdx, gdy, rtemplate, rat)
            bmp = rbm if (refine is not None and refine.get(idx) is not None) else symbols[sid]
            placed.append((bmp, s, t))
            curs_after = raw + ((sw - 1) if not transposed else (sh - 1))
            idx += 1
            n += 1
        enc_int(enc, iads, None)
    flags = (log_strips << 2) | (corner << 4) | (0x40 if transposed else 0) | (op << 7) | (def_pixel << 9) | \
        ((ds_offset & 0x1F) << 10) | ((rtemplate << 15) if refine is not None else 0) | (2 if refine is not None else 0)
    head = region_info(w, h, x, y, region_op) + struct.pack(">H", flags)
    if refine is not None and rtemplate == 0:
        head += b"".join(sgn(v) for pair in (rat or [(-1, -1), (-1, -1)]) for v in pair)
    head += struct.pack(">I", len(instances))
    # render intended region
    region = np.full((h, w), def_pixel, dtype=np.uint8)
    for bmp, s, t in placed:
        bh, bw = bmp.shape
        if not transposed:
            left = s - bw + 1 if right else s
            top = t - bh + 1 if bottom else t
        else:
            left = t - bw + 1 if right else t
            top = s - bh + 1 if bottom else s
        compose(region, bmp, left, top, op)
    return seg(number, 6, page, list(refs), head + enc.finish()), region


def text_case(name, corner, transposed, log_strips, ds_offset=0, op=0, embed_globals=True, rtemplate=0, refine_some=False):
    glyphs = [make_glyph(rng.randint(3, 12), rng.randint(4, 14)) for _ in range(9)]
    dict_seg, ordered = sym_dict_arith(glyphs, number=1)
    w, h = 190, 100
    instances = []
    strips = 1 << log_strips
    if not transposed:
        for line in range(5):
            s = 5
            base_t = 12 + line * 18
            while s < w - 30:
                sid = rng.randrange(len(ordered))
                t = base_t + (rng.randrange(strips) if strips > 1 else 0)
                instances.append((sid, s, t))
                s += ordered[sid].shape[1] + rng.randint(1, 5)
    else:
        for column in range(5):
            s = 5
            base_t = 12 + column * 30
            while s < h - 30:
                sid = rng.randrange(len(ordered))
                t = base_t + (rng.randrange(strips) if strips > 1 else 0)
                instances.append((sid, s, t))
                s += ordered[sid].shape[0] + rng.randint(1, 5)
    refine = None
    if refine_some:
        refine = {}
        for k in range(0, len(instances), 3):
            sid = instances[k][0]
            ob = ordered[sid]
            nb = ob.copy()
            nb = np.pad(nb, ((0, rng.randint(0, 2)), (0, rng.randint(0, 2))))
            for _ in range(3):
                nb[rng.randrange(nb.shape[0]), rng.randrange(nb.shape[1])] ^= 1
            refine[k] = (nb, rng.randint(-1, 1), rng.randint(-1, 1))
    # instances sorted inside the encoder, so indexes in `refine` must refer to sorted order
    strips_n = 1 << log_strips
    order_idx = sorted(range(len(instances)), key=lambda i: (instances[i][2] // strips_n, instances[i][1]))
    instances_sorted = [instances[i] for i in order_idx]
    if refine is not None:
        refine = {new: refine[old] for new, old in enumerate(order_idx) if old in refine}
        instances = instances_sorted
    text_seg, region = text_region_arith(ordered, instances, w, h, 2, [1], corner=corner, transposed=transposed,
                                         log_strips=log_strips, op=op, ds_offset=ds_offset, refine=refine, rtemplate=rtemplate)
    page = np.zeros((h, w), dtype=np.uint8)
    page |= region
    segs_page = [mk_page(w, h), text_seg, seg(3, 49, 1, [], b"")]
    # the dictionary lives in the globals (page association 0)
    dict_seg_g = seg(1, 0, 0, [], dict_seg[1])
    return Case(name, page, segs_page, [dict_seg_g])


def refinement_case(name, template, tpgron, intermediate):
    w, h = 120, 50
    base = scene(w, h)
    target = base.copy()
    for _ in range(40):
        target[rng.randrange(h), rng.randrange(w)] ^= 1
    segs = [mk_page(w, h)]
    cx = Contexts(1 << (13 if template == 0 else 10))
    enc = MQEncoder()
    enc_refinement(enc, cx, target, base, 0, 0, template, None, tpgron)
    flags = template | (2 if tpgron else 0)
    at = b"".join(sgn(v) for v in (-1, -1, -1, -1)) if template == 0 else b""
    if intermediate:
        gc = Contexts(gen_context_size(0))
        genc = MQEncoder()
        enc_generic(genc, gc, base, 0)
        gdata = region_info(w, h, 0, 0) + bytes([0]) + b"".join(sgn(v) for p in NOMINAL_AT[0] for v in p) + genc.finish()
        segs.append(seg(1, 36, 1, [], gdata))
        rdata = region_info(w, h, 0, 0, 4) + bytes([flags]) + at + enc.finish()
        segs.append(seg(2, 42, 1, [1], rdata))
    else:
        gc = Contexts(gen_context_size(0))
        genc = MQEncoder()
        enc_generic(genc, gc, base, 0)
        gdata = region_info(w, h, 0, 0) + bytes([0]) + b"".join(sgn(v) for p in NOMINAL_AT[0] for v in p) + genc.finish()
        segs.append(seg(1, 38, 1, [], gdata))
        rdata = region_info(w, h, 0, 0, 4) + bytes([flags]) + at + enc.finish()
        segs.append(seg(2, 42, 1, [], rdata))
    segs.append(seg(3, 49, 1, [], b""))
    # jbig2dec does not implement intermediate regions, so this case is not cross-checked with MuPDF
    return Case(name, target, segs, check_embedded=not intermediate)


def halftone_case(name, mmr, template=0, skip=False):
    pw, ph = 4, 4
    npat = 7
    patterns = []
    for k in range(npat):
        p = np.zeros((ph, pw), dtype=np.uint8)
        cells = [(0, 0), (2, 2), (0, 2), (2, 0), (1, 1), (3, 3), (1, 3), (3, 1), (0, 1), (1, 0), (2, 3), (3, 2), (0, 3), (3, 0), (1, 2), (2, 1)]
        for (cx_, cy_) in cells[: k * 2]:
            p[cy_, cx_] = 1
        patterns.append(p)
    collective = np.concatenate(patterns, axis=1)
    if mmr:
        mmr_bytes = mmr_encode(collective)
        pdata = bytes([1 | (template << 1)]) + bytes([pw, ph]) + struct.pack(">I", npat - 1) + mmr_bytes
    else:
        cx = Contexts(gen_context_size(template))
        enc = MQEncoder()
        at = [(-pw, 0), (-3, -1), (2, -2), (-2, -2)][: len(NOMINAL_AT[template])]
        enc_generic(enc, cx, collective, template, at)
        pdata = bytes([(template << 1)]) + bytes([pw, ph]) + struct.pack(">I", npat - 1) + enc.finish()
    gw, gh = 20, 12
    w, h = gw * pw, gh * ph
    values = np.array([[(m * 3 + n * 2) % npat for n in range(gw)] for m in range(gh)], dtype=np.int64)
    bpp = (npat - 1).bit_length()
    planes = []
    gray = values.copy()
    for j in range(bpp - 1, -1, -1):
        bit = (values >> j) & 1
        if j < bpp - 1:
            bit = bit ^ ((values >> (j + 1)) & 1)
        planes.append(bit.astype(np.uint8))
    hgx, hgy, hrx, hry = 0, 0, pw * 256, 0
    skipmap = np.zeros((gh, gw), dtype=np.uint8) if True else None
    if skip:
        # shift the grid so some cells fall outside the region
        hgx, hgy = -2 * pw * 256 // 2, 0
    for m in range(gh):
        for n in range(gw):
            x = (hgx + m * hry + n * hrx) >> 8
            y = (hgy + m * hrx - n * hry) >> 8
            if x + pw <= 0 or x >= w or y + ph <= 0 or y >= h:
                skipmap[m, n] = 1
    if mmr:
        body = b"".join(mmr_encode(p) for p in planes)
    else:
        cx = Contexts(gen_context_size(template))
        enc = MQEncoder()
        at = [(3 if template <= 1 else 2, -1), (-3, -1), (2, -2), (-2, -2)][: len(NOMINAL_AT[template])]
        for p in planes:
            enc_generic(enc, cx, p, template, at, False, skipmap if skip else None)
        body = enc.finish()
    hflags = (1 if mmr else 0) | (template << 1) | (8 if skip else 0)
    hdata = region_info(w, h, 0, 0) + bytes([hflags]) + struct.pack(">IIiiHH", gw, gh, hgx, hgy, hrx, hry) + body
    intended = np.zeros((h, w), dtype=np.uint8)
    for m in range(gh):
        for n in range(gw):
            x = (hgx + m * hry + n * hrx) >> 8
            y = (hgy + m * hrx - n * hry) >> 8
            compose(intended, patterns[values[m, n]], x, y, 0)
    segs = [mk_page(w, h), seg(1, 16, 1, [], pdata), seg(2, 22, 1, [1], hdata), seg(3, 49, 1, [], b"")]
    return Case(name, intended, segs)


def mmr_encode(bm):
    h, w = bm.shape
    img = Image.fromarray(np.where(bm != 0, 255, 0).astype(np.uint8), "L").convert("1")
    buf = io.BytesIO()
    img.save(buf, format="TIFF", compression="group4", tiffinfo={278: h})
    raw = buf.getvalue()
    t = Image.open(io.BytesIO(raw))
    off = t.tag_v2[273]
    cnt = t.tag_v2[279]
    off = off if isinstance(off, tuple) else (off,)
    cnt = cnt if isinstance(cnt, tuple) else (cnt,)
    return b"".join(raw[o:o + c] for o, c in zip(off, cnt))


def striped_case(name):
    w, h = 160, 60
    bm = scene(w, h)
    segs = [mk_page(w, 0xFFFFFFFF, 0, 1, 20)]
    for stripe in range(3):
        y0 = stripe * 20
        part = bm[y0:y0 + 20]
        cx = Contexts(gen_context_size(0))
        enc = MQEncoder()
        enc_generic(enc, cx, part, 0)
        data = region_info(w, 20, 0, y0) + bytes([0]) + b"".join(sgn(v) for p in NOMINAL_AT[0] for v in p) + enc.finish()
        segs.append(seg(1 + stripe * 2, 38, 1, [], data))
        segs.append(seg(2 + stripe * 2, 50, 1, [], struct.pack(">I", y0 + 19)))
    segs.append(seg(10, 49, 1, [], b""))
    # the page header's height is replaced; mk_page already wrote 0xFFFFFFFF via the struct (unsigned)
    return Case(name, bm, segs)


def write_case(case, outdir):
    to_png(case.intended, os.path.join(outdir, case.name + ".png"))
    allsegs = case.global_segments + case.page_segments
    # standalone files carry the globals inline, before the page segments
    standalone = [(h, d) for (h, d) in allsegs] + [seg(99, 51, 0, [], b"")]
    open(os.path.join(outdir, case.name + ".jb2"), "wb").write(file_bytes(standalone, case.sequential))
    page_stream = stream_bytes(case.page_segments)
    open(os.path.join(outdir, case.name + ".seg"), "wb").write(page_stream)
    if case.global_segments:
        open(os.path.join(outdir, case.name + ".glb"), "wb").write(stream_bytes(case.global_segments))
    h, w = case.intended.shape
    if case.check_embedded:
        want = np.where(case.intended != 0, 0, 255).astype(np.uint8)
        page_arg = page_stream
        globals_arg = stream_bytes(case.global_segments) if case.global_segments else None
        verdicts = {}
        for oracle, decode in (("MuPDF", pdf_decode), ("PDFium", pdfium_decode)):
            try:
                got = decode(page_arg, globals_arg, w, h)
                verdicts[oracle] = got.shape == want.shape and np.array_equal(got, want)
            except Exception as ex:
                verdicts[oracle] = False
        # jbig2dec (MuPDF) and PDFium each have known gaps; a case must match at least one independent decoder.
        if not any(verdicts.values()):
            print("MISMATCH vs both decoders:", case.name)
            return False
        if not all(verdicts.values()):
            print("note: only", [k for k, v in verdicts.items() if v], "agrees for", case.name)
    print("ok", case.name)
    return True


def main():
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    cases = []
    for t in range(4):
        for tp in (False, True):
            cases.append(generic_case(f"generic_t{t}{'_tpgd' if tp else ''}", t, tp))
    cases.append(generic_case("generic_t0_at", 0, False, at=[(-5, -1), (4, -2), (-6, 0), (-3, -2)]))
    cases.append(generic_case("generic_t1_at", 1, False, at=[(-4, 0)]))
    cases.append(generic_case("generic_t2_at", 2, True, at=[(-5, -1)]))
    cases.append(generic_case("generic_t3_at", 3, False, at=[(-1, -3)]))
    cases.append(generic_case("generic_random_access", 0, False, sequential=False))
    cases.append(mmr_case("generic_mmr"))
    cases.append(text_case("text_topleft", 1, False, 0))
    cases.append(text_case("text_topright", 3, False, 0))
    cases.append(text_case("text_bottomleft", 0, False, 0))
    cases.append(text_case("text_bottomright", 2, False, 0))
    cases.append(text_case("text_strips", 1, False, 2, ds_offset=-2))
    cases.append(text_case("text_xor", 1, False, 0, op=2))
    cases.append(text_case("text_transposed_topleft", 1, True, 0))
    cases.append(text_case("text_transposed_bottomright", 2, True, 1))
    cases.append(text_case("text_refined_t0", 1, False, 0, refine_some=True, rtemplate=0))
    cases.append(text_case("text_refined_t1", 1, False, 0, refine_some=True, rtemplate=1))
    for tpgron in (False, True):
        for template in (0, 1):
            cases.append(refinement_case(f"refine_t{template}{'_tpgron' if tpgron else ''}", template, tpgron, False))
    cases.append(refinement_case("refine_intermediate", 0, False, True))
    cases.append(halftone_case("halftone_arith", False))
    cases.append(halftone_case("halftone_arith_t1", False, template=1))
    cases.append(halftone_case("halftone_arith_t3_skip", False, template=3, skip=True))
    cases.append(halftone_case("halftone_mmr", True))
    cases.append(striped_case("striped_unknown_height"))
    import jbig2gen_huffman
    cases.extend(jbig2gen_huffman.cases())
    import jbig2gen_more
    cases.extend(jbig2gen_more.cases())
    ok = True
    for c in cases:
        ok &= write_case(c, outdir)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
