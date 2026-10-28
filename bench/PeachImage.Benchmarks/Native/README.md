# Native reference codecs

`LibraryImport` shims over the reference C/C++ codecs used as baselines in `LIBRARY_COMPARISON.md` where SkiaSharp has no codec:
libjxl (JPEG XL), OpenJPEG (JPEG 2000), libtiff (TIFF, CCITT fax) and libavif with dav1d (AVIF decode).

The DLLs are not checked in. Build them with vcpkg (x64-windows, dynamic):

    vcpkg install --triplet x64-windows libjxl openjpeg tiff "libavif[aom,dav1d]"

and point `PEACH_NATIVE_DIR` at the resulting `installed/x64-windows/bin` (default:
`C:\Sources\GoogleSource\vcpkg-native\x64-windows\bin`). Versions used for the published numbers: libjxl 0.12.0, OpenJPEG 2.5.4,
libtiff 4.7.2, libavif 1.4.2, dav1d 1.5.4.

`dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- native-smoke` decodes each asset through PeachImage and
the matching shim once, to confirm the DLLs load and both sides process the same workload before a long benchmark run.
