"""Extra JBIG2 cases for jbig2gen.py: refinement/aggregate symbol dictionaries, retained contexts, custom Huffman tables, unknown-length regions."""
import struct

import numpy as np

import jbig2gen as g
import jbig2gen_huffman as hf


def _glyphs(n):
    return [g.make_glyph(g.rng.randint(3, 11), g.rng.randint(4, 12)) for _ in range(n)]


def _page_text(symbols, dict_refs, w, h, number):
    instances = []
    for line in range(4):
        s = 4
        while s < w - 25:
            sid = g.rng.randrange(len(symbols))
            instances.append((sid, s, 10 + line * 20))
            s += symbols[sid].shape[1] + g.rng.randint(1, 4)
    return g.text_region_arith(symbols, instances, w, h, number, dict_refs)


def refagg_case(name, rtemplate=0, retain_generic=False):
    base = _glyphs(6)
    dict1_seg, base_sorted = g.sym_dict_arith(base, number=1, retain=retain_generic)
    # Second dictionary: every new symbol is a refinement of an input symbol (1 instance) or an aggregate of two (2 instances).
    n_in = len(base_sorted)
    new_specs = []
    for k in range(4):
        if k % 2 == 0:
            ref_id = g.rng.randrange(n_in)
            ob = base_sorted[ref_id]
            nb = ob.copy()
            for _ in range(2):
                nb[g.rng.randrange(nb.shape[0]), g.rng.randrange(nb.shape[1])] ^= 1
            new_specs.append(("ref", ref_id, nb, g.rng.randint(-1, 1), g.rng.randint(-1, 1)))
        else:
            a, b = g.rng.randrange(n_in), g.rng.randrange(n_in)
            wa, ha = base_sorted[a].shape[1], base_sorted[a].shape[0]
            wb, hb = base_sorted[b].shape[1], base_sorted[b].shape[0]
            comp = np.zeros((max(ha, hb), wa + 2 + wb), dtype=np.uint8)
            g.compose(comp, base_sorted[a], 0, 0)
            g.compose(comp, base_sorted[b], wa + 2, 0)
            new_specs.append(("agg", (a, wa + 2 + 0, b), comp, 0, 0))
    # sort by height class then keep order; aggregates must come in height order, so sort specs by their bitmap height
    new_specs.sort(key=lambda sp: (sp[2].shape[0]))
    code_len = max((n_in + len(new_specs) - 1).bit_length(), 0)
    enc = g.MQEncoder()
    iadh, iadw, iaex, iaai = g.IntCtx(), g.IntCtx(), g.IntCtx(), g.IntCtx()
    iadt, iafs, iads, iari = g.IntCtx(), g.IntCtx(), g.IntCtx(), g.IntCtx()
    iardx, iardy = g.IntCtx(), g.IntCtx()
    iaid = g.Contexts(1 << (code_len + 1))
    gr = g.Contexts(1 << 13)
    symbols = list(base_sorted)
    height = 0
    i = 0
    while i < len(new_specs):
        hgt = new_specs[i][2].shape[0]
        g.enc_int(enc, iadh, hgt - height)
        height = hgt
        width = 0
        while i < len(new_specs) and new_specs[i][2].shape[0] == hgt:
            kind, info, bm, dx, dy = new_specs[i]
            g.enc_int(enc, iadw, bm.shape[1] - width)
            width = bm.shape[1]
            if kind == "ref":
                g.enc_int(enc, iaai, 1)
                g.enc_iaid(enc, iaid, code_len, info)
                g.enc_int(enc, iardx, dx)
                g.enc_int(enc, iardy, dy)
                g.enc_refinement(enc, gr, bm, symbols[info], dx, dy, rtemplate)
            else:
                a, off, b = info
                g.enc_int(enc, iaai, 2)
                # nested text region: STRIPT starts at 0, one strip at T=0, two TOPLEFT instances
                g.enc_int(enc, iadt, 0)
                g.enc_int(enc, iadt, 0)
                g.enc_int(enc, iafs, 0)
                g.enc_iaid(enc, iaid, code_len, a)
                g.enc_int(enc, iari, 0)
                g.enc_int(enc, iads, off - (symbols[a].shape[1] - 1) - 0)
                g.enc_iaid(enc, iaid, code_len, b)
                g.enc_int(enc, iari, 0)
                g.enc_int(enc, iads, None)
            symbols.append(bm)
            i += 1
        g.enc_int(enc, iadw, None)
    total = n_in + len(new_specs)
    g.enc_int(enc, iaex, n_in)
    g.enc_int(enc, iaex, len(new_specs))
    atb = b"".join(g.sgn(v) for pair in g.NOMINAL_AT[0] for v in pair)
    ratb = b"".join(g.sgn(v) for v in (-1, -1, -1, -1)) if rtemplate == 0 else b""
    flags = 2 | (rtemplate << 12)
    head = struct.pack(">H", flags) + atb + ratb + struct.pack(">II", len(new_specs), len(new_specs))
    dict2_seg = g.seg(2, 0, 0, [1], head + enc.finish())
    exported = symbols[n_in:]
    all_symbols = base_sorted + exported
    w, h = 170, 90
    text_seg, region = _page_text(all_symbols, [1, 2], w, h, 3)
    segs_page = [g.mk_page(w, h), text_seg, g.seg(4, 49, 1, [], b"")]
    dict1_g = g.seg(1, 0, 0, [], dict1_seg[1])
    return g.Case(name, region, segs_page, [dict1_g, dict2_seg])


def retained_contexts_case(name):
    first = _glyphs(5)
    second = _glyphs(5)
    cx = g.Contexts(g.gen_context_size(0))
    enc1 = g.MQEncoder()
    order1 = sorted(range(len(first)), key=lambda k: (first[k].shape[0], k))
    _encode_dict_body(enc1, cx, first, order1)
    seg1 = _dict_segment(1, first, enc1, retain=True, use=False)
    enc2 = g.MQEncoder()
    order2 = sorted(range(len(second)), key=lambda k: (second[k].shape[0], k))
    _encode_dict_body(enc2, cx, second, order2)  # continues with the contexts left by the first dictionary
    seg2 = _dict_segment(2, second, enc2, retain=False, use=True, refs=[1])
    symbols = [first[k] for k in order1] + [second[k] for k in order2]
    w, h = 170, 90
    text_seg, region = _page_text(symbols, [1, 2], w, h, 3)
    segs_page = [g.mk_page(w, h), text_seg, g.seg(4, 49, 1, [], b"")]
    return g.Case(name, region, segs_page, [seg1, seg2])


def _encode_dict_body(enc, cx, glyphs, order):
    iadh, iadw, iaex = g.IntCtx(), g.IntCtx(), g.IntCtx()
    height = 0
    i = 0
    while i < len(order):
        hgt = glyphs[order[i]].shape[0]
        g.enc_int(enc, iadh, hgt - height)
        height = hgt
        width = 0
        while i < len(order) and glyphs[order[i]].shape[0] == hgt:
            gl = glyphs[order[i]]
            g.enc_int(enc, iadw, gl.shape[1] - width)
            width = gl.shape[1]
            g.enc_generic(enc, cx, gl, 0)
            i += 1
        g.enc_int(enc, iadw, None)
    return iaex


def _dict_segment(number, glyphs, enc, retain, use, refs=()):
    # the export runs are encoded here, after the symbols, with a fresh integer context
    iaex = g.IntCtx()
    # all symbols of this dictionary are exported; the input symbols of a referring dictionary are not
    g.enc_int(enc, iaex, 0 if not refs else 5)
    g.enc_int(enc, iaex, len(glyphs))
    atb = b"".join(g.sgn(v) for pair in g.NOMINAL_AT[0] for v in pair)
    flags = (0x100 if use else 0) | (0x200 if retain else 0)
    head = struct.pack(">H", flags) + atb + struct.pack(">II", len(glyphs), len(glyphs))
    return g.seg(number, 0, 0, list(refs), head + enc.finish())


def custom_table_case(name):
    glyphs = _glyphs(9)
    dict_seg, ordered = hf.sym_dict_huffman(glyphs, 1)
    w, h = 190, 100
    instances = []
    for line in range(5):
        s = 5
        while s < w - 30:
            sid = g.rng.randrange(len(ordered))
            instances.append((sid, s, 12 + line * 18))
            s += ordered[sid].shape[1] + g.rng.randint(1, 5)
    # Custom table for the first-S deltas: [-32, 224) in four 64-wide ranges with 2-bit prefixes.
    table_data = struct.pack(">Bii", 0x22, -32, 224)
    bw = hf.BitWriter()
    for _ in range(4):
        bw.bits(2, 2)  # prefix length
        bw.bits(6, 3)  # range length
    bw.bits(0, 2)  # lower range line unused
    bw.bits(0, 2)  # upper range line unused
    table_data += bw.data()
    custom = hf.HuffTable([("N", -32, 2, 6), ("N", 32, 2, 6), ("N", 96, 2, 6), ("N", 160, 2, 6), ("L", -33, 0, 32), ("U", 224, 0, 32)])
    text_seg, region = _text_region_custom_fs(ordered, instances, w, h, 3, [1, 2], custom)
    segs_page = [g.mk_page(w, h), g.seg(2, 53, 1, [], table_data), text_seg, g.seg(4, 49, 1, [], b"")]
    return g.Case(name, region, segs_page, [g.seg(1, 0, 0, [], dict_seg[1])])


def _text_region_custom_fs(symbols, instances, w, h, number, refs, fs_table):
    # Same as hf.text_region_huffman with FS selection 3 (user table) and the FS values written through `fs_table`.
    seg_std, region = hf.text_region_huffman(symbols, instances, w, h, number, refs, 0, 0, 0, 0, 1, 0)
    # Re-encode with the custom table: patch by rebuilding with a table swap.
    original = hf.HuffTable.standard
    try:
        def swapped(n, _orig=original):
            return fs_table if n == 6 else _orig(n)
        hf.HuffTable.standard = staticmethod(swapped)
        seg_custom, region = hf.text_region_huffman(symbols, instances, w, h, number, refs, 0, 0, 0, 0, 1, 0)
    finally:
        hf.HuffTable.standard = original
    header, data = seg_custom
    # flip the FS selection bits of the Huffman flags word (offset: 17 region info + 2 flags) to 3
    patched = bytearray(data)
    patched[19 + 1] = (patched[19 + 1] & ~3) | 3
    return (header, bytes(patched)), region


def unknown_length_case(name):
    w, h = 140, 50
    bm = g.scene(w, h)
    cx = g.Contexts(g.gen_context_size(0))
    enc = g.MQEncoder()
    g.enc_generic(enc, cx, bm, 0)
    data = g.region_info(w, h, 0, 0) + bytes([0]) + b"".join(g.sgn(v) for p in g.NOMINAL_AT[0] for v in p) + enc.finish() + struct.pack(">I", h)
    segs = [g.mk_page(w, h), g.seg(1, 38, 1, [], data, unknown_len=True), g.seg(2, 49, 1, [], b"")]
    return g.Case(name, bm, segs)


def many_refs_case(name):
    # a text region referring to six dictionaries (long-form referred-segment count) with segment numbers above 256
    dicts = []
    symbols = []
    for k in range(6):
        glyphs = _glyphs(2)
        sd, ordered = g.sym_dict_arith(glyphs, number=300 + k)
        dicts.append(g.seg(300 + k, 0, 0, [], sd[1]))
        symbols.extend(ordered)
    w, h = 150, 70
    instances = []
    for line in range(3):
        s = 4
        while s < w - 25:
            sid = g.rng.randrange(len(symbols))
            instances.append((sid, s, 10 + line * 20))
            s += symbols[sid].shape[1] + 2
    text_seg, region = g.text_region_arith(symbols, instances, w, h, 400, [300 + k for k in range(6)])
    page = g.seg(299, 48, 1, [], g.page_info(w, h))
    segs_page = [page, text_seg, g.seg(401, 49, 1, [], b"")]
    return g.Case(name, region, segs_page, dicts)


def cases():
    return [
        refagg_case("refagg_t0", 0),
        refagg_case("refagg_t1", 1),
        retained_contexts_case("retained_contexts"),
        custom_table_case("huffman_custom_table"),
        unknown_length_case("generic_unknown_length"),
        many_refs_case("text_many_dictionaries"),
    ]
