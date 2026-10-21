"""Generates JPEG 2000 test assets with OpenJPEG's opj_compress / opj_decompress for features Pillow cannot reach
(code-block styles, SOP/EPH, progression order changes, tile-parts, subsampling, image/tile offsets, JP2 palette/channel boxes).

Usage: python j2kgen_opj.py OUTDIR OPJ_BIN_DIR
Writes NAME.j2k / NAME.jp2 and NAME.png (opj_decompress's own output). Needs numpy and Pillow.
"""
import os
import struct
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image

import j2kgen

OPJ = None


def run(exe, *args):
    result = subprocess.run([os.path.join(OPJ, exe)] + [str(a) for a in args], capture_output=True, text=True, stdin=subprocess.DEVNULL)
    if result.returncode != 0:
        raise RuntimeError(f"{exe} {args} failed: {result.stdout} {result.stderr}")
    return result.stdout


def compress(out, name, source, *options, ext="j2k"):
    target = os.path.join(out, f"{name}.{ext}")
    run("opj_compress.exe", "-i", source, "-o", target, *options)
    return target


def decode_reference(out, name, path, *options):
    ref = os.path.join(out, f"{name}.png")
    run("opj_decompress.exe", "-i", path, "-o", ref, *options)
    # opj_decompress writes 16-bit PNGs for deep images; normalise nothing here, the test compares pixel formats.
    print("ok", name, Image.open(ref).mode, Image.open(ref).size)


def save_source(tmp, name, im):
    ext = {"RGB": "ppm", "L": "pgm", "RGBA": "png", "I;16": "png"}[im.mode]
    path = os.path.join(tmp, f"{name}.{ext}")
    im.save(path)
    return path


# ---------------------------------------------------------------- JP2 box injection
def box(kind, payload):
    return struct.pack(">I", len(payload) + 8) + kind + payload


def make_jp2(codestream, width, height, comps, depth, colr=None, pclr=None, cmap=None, cdef=None, icc=None):
    ihdr = struct.pack(">IIHBBBB", height, width, comps, depth - 1, 7, 0, 0)
    inner = box(b"ihdr", ihdr)
    if colr is not None:
        inner += box(b"colr", bytes([1, 0, 0]) + struct.pack(">I", colr))
    if icc is not None:
        inner += box(b"colr", bytes([2, 0, 0]) + icc)
    if pclr is not None:
        inner += box(b"pclr", pclr)
    if cmap is not None:
        inner += box(b"cmap", cmap)
    if cdef is not None:
        inner += box(b"cdef", cdef)
    sig = box(b"jP  ", b"\x0d\x0a\x87\x0a")
    ftyp = box(b"ftyp", b"jp2 " + struct.pack(">I", 0) + b"jp2 ")
    return sig + ftyp + box(b"jp2h", inner) + box(b"jp2c", codestream)


def read_codestream(path):
    return open(path, "rb").read()


def main():
    global OPJ
    out = sys.argv[1]
    OPJ = sys.argv[2]
    os.makedirs(out, exist_ok=True)
    tmp = tempfile.mkdtemp()
    rgb = j2kgen.natural(110, 84, "RGB")
    gray = j2kgen.natural(110, 84, "L")
    rgb_src = save_source(tmp, "rgb", rgb)
    gray_src = save_source(tmp, "gray", gray)

    def go(name, source, *options, ext="j2k", decode=()):
        path = compress(out, name, source, *options, ext=ext)
        decode_reference(out, name, path, *decode)

    # Code-block coding style switches.
    for label, mode in (("bypass", 1), ("reset", 2), ("termall", 4), ("vsc", 8), ("erterm", 16), ("segmark", 32),
                        ("bypass_termall", 5), ("bypass_reset", 3), ("termall_vsc_segmark", 44), ("all", 63)):
        go(f"mode_{label}_rgb", rgb_src, "-M", mode)
        go(f"mode_{label}_gray_layers", gray_src, "-M", mode, "-r", "30,10,3,1", "-n", 4)
    go("mode_bypass_lossy", rgb_src, "-M", 1, "-I", "-r", "12,4")
    go("mode_all_lossy_cblk16", rgb_src, "-M", 63, "-I", "-r", "10", "-b", "16,16")
    go("mode_bypass_cblk32x32_layers", rgb_src, "-M", 1, "-r", "40,20,10,5,2,1", "-b", "32,32")

    # Marker features.
    go("sop", rgb_src, "-SOP")
    go("eph", rgb_src, "-EPH")
    go("sop_eph", rgb_src, "-SOP", "-EPH", "-r", "20,5,1")
    go("plt_tlm", rgb_src, "-PLT", "-TLM")
    go("roi_upshift", rgb_src, "-ROI", "c=0,U=10", "-mct", 0)
    go("roi_upshift_lossy", rgb_src, "-ROI", "c=1,U=12", "-mct", 0, "-I", "-r", "8")
    for part in ("R", "L", "C"):
        go(f"tileparts_{part.lower()}", rgb_src, "-TP", part, "-r", "30,8,1", "-n", 4)
        go(f"tileparts_{part.lower()}_tiled", rgb_src, "-TP", part, "-r", "30,8,1", "-t", "48,40", "-n", 3)
    go("poc_rpcl_lrcp", rgb_src, "-n", 5, "-POC", "T1=0,0,1,3,3,RPCL/T1=3,0,1,5,3,LRCP")
    go("poc_cprl_rlcp_layers", rgb_src, "-n", 4, "-r", "20,5,1", "-POC", "T1=0,0,2,2,3,CPRL/T1=2,0,3,4,3,RLCP")

    # Offsets.
    go("image_offset", rgb_src, "-d", "7,5")
    go("image_offset_tiles", rgb_src, "-d", "13,9", "-t", "40,30", "-T", "3,2", "-n", 3)
    go("tile_offset_only", rgb_src, "-t", "50,40", "-T", "0,0", "-n", 4)
    go("image_offset_precincts_rpcl", rgb_src, "-d", "11,6", "-c", "[32,32],[32,32]", "-p", "RPCL", "-n", 4)
    go("image_offset_pcrl_cprl", rgb_src, "-d", "9,3", "-c", "[16,32],[16,16]", "-p", "PCRL", "-n", 3, "-t", "60,50")
    go("precincts_cprl", rgb_src, "-c", "[64,32],[32,32],[16,16]", "-p", "CPRL", "-n", 5)

    # Irreversible with many options.
    go("lossy_all_progs_tiles", rgb_src, "-I", "-r", "25,8", "-t", "55,45", "-p", "RLCP", "-c", "[32,32]", "-n", 4)

    # Custom sample depths via raw files.
    rng = np.random.default_rng(3)

    def raw_case(name, w, h, comps, bits, signed, subsamples, options=(), ext="j2k", gen=None):
        sizes = []
        data = bytearray()
        for c in range(comps):
            dx, dy = subsamples[c]
            cw, ch = (w + dx - 1) // dx, (h + dy - 1) // dy
            yy, xx = np.mgrid[0:ch, 0:cw]
            base = (xx * 7 + yy * 3 + c * 41) % (1 << bits)
            noise = rng.integers(0, 1 << max(bits - 4, 1), size=(ch, cw))
            plane = ((base + noise) % (1 << bits)).astype(np.int64)
            if signed:
                plane = plane - (1 << (bits - 1))
            arr = plane.astype('<i2' if signed else '<u2') if bits > 8 else plane.astype('i1' if signed else 'u1')
            data += arr.tobytes()
            sizes.append(f"{dx}x{dy}")
        src = os.path.join(tmp, f"{name}.raw")
        open(src, "wb").write(bytes(data))
        fmt = f"{w},{h},{comps},{bits},{'s' if signed else 'u'}@{':'.join(sizes)}"
        target = os.path.join(out, f"{name}.{ext}")
        run("opj_compress.exe", "-i", src, "-F", fmt, "-o", target, "-n", 3, *options)
        # Reference as raw planes (native sample values), converted by the test harness; keep PGX-like binary dumps.
        if any(d != (1, 1) for d in subsamples):
            # Components of different sizes: take each decoded plane at its native size from the PGX files.
            pgx_base = os.path.join(tmp, f"{name}_ref.pgx")
            run("opj_decompress.exe", "-i", target, "-o", pgx_base)
            blob = bytearray()
            for c in range(comps):
                raw = open(os.path.join(tmp, f"{name}_ref_{c}.pgx"), "rb").read()
                blob += raw[raw.index(b"\n") + 1:]
            open(os.path.join(out, f"{name}.ref.raw"), "wb").write(bytes(blob))
            print("ok", name, "(native plane reference)")
        else:
            refbase = os.path.join(out, f"{name}.ref.raw")
            run("opj_decompress.exe", "-i", target, "-o", refbase)
            print("ok", name, "(raw reference)")

    raw_case("depth12_gray", 70, 50, 1, 12, False, [(1, 1)])
    raw_case("depth12_rgb", 60, 40, 3, 12, False, [(1, 1)] * 3, ("-mct", 1))
    raw_case("depth16_gray", 64, 40, 1, 16, False, [(1, 1)])
    raw_case("depth4_gray", 50, 30, 1, 4, False, [(1, 1)])
    raw_case("depth1_gray", 50, 30, 1, 1, False, [(1, 1)])
    raw_case("signed8_gray", 50, 30, 1, 8, True, [(1, 1)])
    raw_case("signed12_gray_lossy", 50, 30, 1, 12, True, [(1, 1)], ("-I", "-r", "6"))
    raw_case("subsampled_420", 61, 45, 4, 8, False, [(1, 1), (2, 2), (2, 2), (1, 1)], ("-mct", 0))
    raw_case("subsampled_422_lossy", 61, 45, 4, 8, False, [(1, 1), (2, 1), (2, 1), (1, 1)], ("-mct", 0, "-I", "-r", "6"))
    raw_case("subsampled_odd", 37, 29, 4, 8, False, [(1, 1), (3, 2), (2, 3), (1, 1)], ("-mct", 0))
    raw_case("four_components", 40, 30, 4, 8, False, [(1, 1)] * 4, ("-mct", 0))
    raw_case("five_components", 40, 30, 5, 8, False, [(1, 1)] * 5, ("-mct", 0))

    # JP2 boxes around OpenJPEG codestreams.
    cs_rgb = read_codestream(compress(out, "tmp_rgb_cs", rgb_src, "-mct", 1))
    os.remove(os.path.join(out, "tmp_rgb_cs.j2k"))
    cs_gray = read_codestream(compress(out, "tmp_gray_cs", gray_src))
    os.remove(os.path.join(out, "tmp_gray_cs.j2k"))

    def jp2_case(name, data):
        path = os.path.join(out, f"{name}.jp2")
        open(path, "wb").write(data)
        decode_reference(out, name, path)

    jp2_case("box_srgb", make_jp2(cs_rgb, 110, 84, 3, 8, colr=16))
    jp2_case("box_sycc", make_jp2(cs_rgb, 110, 84, 3, 8, colr=18))
    jp2_case("box_gray", make_jp2(cs_gray, 110, 84, 1, 8, colr=17))

    # Palette: a 1-component index image expanded to RGB through pclr/cmap.
    idx_img = Image.fromarray(((np.mgrid[0:84, 0:110][1] // 8 + np.mgrid[0:84, 0:110][0] // 6) % 16).astype(np.uint8), "L")
    idx_src = os.path.join(tmp, "idx.pgm")
    idx_img.save(idx_src)
    cs_idx = read_codestream(compress(out, "tmp_idx_cs", idx_src))
    os.remove(os.path.join(out, "tmp_idx_cs.j2k"))
    entries = 16
    pal = bytearray(struct.pack(">HB", entries, 3) + bytes([7, 7, 7]))
    for e in range(entries):
        pal += bytes([(e * 16) & 0xFF, (255 - e * 13) & 0xFF, (e * e) & 0xFF])
    cmap = b"".join(struct.pack(">HBB", 0, 1, col) for col in range(3))
    jp2_case("box_palette", make_jp2(cs_idx, 110, 84, 1, 8, colr=16, pclr=bytes(pal), cmap=cmap))

    # Alpha channel through cdef: 4-component codestream, channels 0-2 colour, 3 opacity.
    rgba = j2kgen.natural(110, 84, "RGBA")
    rgba_src = os.path.join(tmp, "rgba.png")
    rgba.save(rgba_src)
    cs_rgba = read_codestream(compress(out, "tmp_rgba_cs", rgba_src))
    os.remove(os.path.join(out, "tmp_rgba_cs.j2k"))
    cdef = struct.pack(">H", 4) + b"".join(struct.pack(">HHH", ch, 0 if ch < 3 else 1, ch + 1 if ch < 3 else 0) for ch in range(4))
    jp2_case("box_rgba_cdef", make_jp2(cs_rgba, 110, 84, 4, 8, colr=16, cdef=cdef))
    # Channels listed in reverse order of association (BGR).
    cdef_bgr = struct.pack(">H", 3) + b"".join(struct.pack(">HHH", ch, 0, 3 - ch) for ch in range(3))
    jp2_case("box_bgr_cdef", make_jp2(cs_rgb, 110, 84, 3, 8, colr=16, cdef=cdef_bgr))


if __name__ == "__main__":
    main()
