"""Generates the JPEG 2000 test assets with Pillow (OpenJPEG) and writes OpenJPEG's own decode of each as the reference PNG.

Usage: python j2kgen.py OUTDIR
Every case NAME produces NAME.jp2 (or NAME.j2k for raw codestreams) and NAME.png (the decode by OpenJPEG).
"""
import os
import random
import sys

import numpy as np
from PIL import Image

rng = random.Random(99)


def natural(w, h, mode):
    yy, xx = np.mgrid[0:h, 0:w]
    base = (np.sin(xx / 7.0) * 60 + np.cos(yy / 5.0) * 60 + 128).astype(np.float64)
    noise = np.array([[rng.random() * 12 for _ in range(w)] for _ in range(h)])
    chans = []
    for c in range(3):
        v = base * (0.6 + 0.2 * c) + noise + (xx * (c + 1) % 40) * (1.0 + c)
        chans.append(np.clip(v, 0, 255))
    rgb = np.stack(chans, axis=-1).astype(np.uint8)
    # a few hard edges and a flat region
    rgb[h // 4: h // 2, w // 3: w // 2] = [255, 20, 20]
    rgb[h // 2:, : w // 4] = 7
    if mode == "RGB":
        return Image.fromarray(rgb, "RGB")
    if mode == "L":
        return Image.fromarray(rgb[..., 1], "L")
    if mode == "RGBA":
        alpha = np.clip(xx * 255 // max(w - 1, 1), 0, 255).astype(np.uint8)
        return Image.fromarray(np.dstack([rgb, alpha]), "RGBA")
    if mode == "LA":
        alpha = np.clip(yy * 255 // max(h - 1, 1), 0, 255).astype(np.uint8)
        return Image.fromarray(np.dstack([rgb[..., 1], alpha]), "LA")
    if mode == "I;16":
        v16 = (rgb[..., 1].astype(np.uint16) * 257 + (xx % 251)).astype(np.uint16)
        return Image.fromarray(v16)
    raise ValueError(mode)


def to_array(im):
    if im.mode == "I;16":
        return np.array(im, dtype=np.uint16)
    return np.array(im)


def write_reference(path, im):
    arr = to_array(im)
    if arr.dtype == np.uint16:
        Image.fromarray(arr).save(path)  # 16-bit grayscale PNG
    else:
        Image.fromarray(arr).save(path)


def case(out, name, mode="RGB", size=(150, 110), raw=False, **params):
    im = natural(size[0], size[1], mode)
    ext = "j2k" if raw else "jp2"
    path = os.path.join(out, f"{name}.{ext}")
    save_args = dict(params)
    if raw:
        save_args["no_jp2"] = True
    im.save(path, "JPEG2000", **save_args)
    dec = Image.open(path)
    dec.load()
    write_reference(os.path.join(out, f"{name}.png"), dec)
    print("ok", name, dec.mode, dec.size)


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    case(out, "rgb_lossless")
    case(out, "rgb_lossless_nomct", mct=0)
    case(out, "gray_lossless", mode="L")
    case(out, "rgb_lossy", irreversible=True, quality_mode="rates", quality_layers=[12])
    case(out, "rgb_lossy_hq", irreversible=True, quality_mode="rates", quality_layers=[3])
    case(out, "gray_lossy", mode="L", irreversible=True, quality_mode="rates", quality_layers=[8])
    case(out, "rgba_lossless", mode="RGBA")
    case(out, "la_lossless", mode="LA")
    case(out, "gray16_lossless", mode="I;16")
    case(out, "rgb_raw_codestream", raw=True)
    case(out, "gray_raw_codestream", mode="L", raw=True)
    case(out, "rgb_layers", quality_mode="rates", quality_layers=[40, 12, 4, 1])
    case(out, "rgb_lossy_layers", irreversible=True, quality_mode="rates", quality_layers=[40, 15, 5])
    for levels in (1, 3, 6):
        case(out, f"rgb_levels{levels}", num_resolutions=levels + 1)
    case(out, "rgb_levels0", num_resolutions=1)
    case(out, "rgb_cblk16", codeblock_size=(16, 16))
    case(out, "rgb_cblk64x16", codeblock_size=(64, 16))
    case(out, "rgb_cblk4x256", codeblock_size=(4, 256))
    case(out, "rgb_precincts", precinct_size=(32, 32), num_resolutions=4)
    case(out, "rgb_precincts_mixed", precinct_size=(64, 32), num_resolutions=3, size=(210, 140))
    case(out, "rgb_tiles", tile_size=(64, 48))
    case(out, "rgb_lossy_tiles", irreversible=True, quality_mode="rates", quality_layers=[10], tile_size=(50, 50))
    for prog in ("LRCP", "RLCP", "RPCL", "PCRL", "CPRL"):
        case(out, f"rgb_prog_{prog.lower()}", progression=prog, quality_mode="rates", quality_layers=[30, 8, 2],
             precinct_size=(32, 32), num_resolutions=4)
        case(out, f"rgb_prog_{prog.lower()}_tiles", progression=prog, tile_size=(70, 64), precinct_size=(32, 32),
             num_resolutions=3, quality_mode="rates", quality_layers=[20, 5], mode="RGB")
    case(out, "rgb_sop_eph", sop=True, eph=True)
    case(out, "rgb_plt_tlm", plt=True, tlm=True)
    for dims in ((1, 1), (3, 5), (17, 1), (1, 23), (130, 1), (31, 33), (64, 64)):
        case(out, f"size_{dims[0]}x{dims[1]}", size=dims)
    case(out, "gray_tiny_lossy", mode="L", size=(5, 7), irreversible=True, quality_mode="rates", quality_layers=[2])
    case(out, "rgb_big", size=(517, 331), tile_size=(256, 256), num_resolutions=5)


if __name__ == "__main__":
    main()
