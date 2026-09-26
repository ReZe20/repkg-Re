# RePKG_Re

**English** | [简体中文](README.zh-CN.md)

A fork of [RePKG](https://github.com/notscuffed/repkg) by ReZe20.

Modified .pkg extractor and .tex converter for [Wallpaper Engine](https://www.wallpaperengine.io/) wallpapers.

Original author: NotScuffed (2019-2025)  
Fork maintainer: ReZe20 (2025)
Wallpaper engine PKG unpacker/TEX converter, written in C#.

PKG and TEX formats reverse engineered by me.

Feel free to report errors.

# Features
- Extract PKG files
- Convert PKG into wallpaper engine project
- Convert a wallpaper project directory back into a PKG
- Convert TEX to image
- Dump PKG/TEX info
- Batch mode: convert PC packages into mobile `.mpkg` packages, and back

### Platforms
Runs on Windows, Linux and macOS on .NET 10 (JIT), and ships NativeAOT single-file
binaries for win-x64 and linux-x64. macOS support (memory sampling) is implemented
but not yet verified on real hardware.

### Commands
- `--help` (or `-h`, `-?`) - lists the commands; `extract --help`, `info --help` and `batch --help` list the options of one command
- `--version` - prints the version and the source commit it was built from
- extract - extracts specified PKG/TEX file, or files from folder
```
-o, --output <DIR>        Output directory (default: ./output)
-i, --ignoreexts <EXTS>   Don't extract files with these extensions (comma-delimited)
-e, --onlyexts <EXTS>     Only extract files with these extensions (comma-delimited)
-t, --tex                 Convert all TEX files into images from the directory given as input
-s, --singledir           Put all extracted files in one directory instead of their entry path
-r, --recursive           Search all subfolders of the specified directory
-c, --copyproject         Copy project.json and the preview image (as declared in project.json)
                          from beside the PKG into output
-n, --usename             Use the title in project.json as the subfolder name instead of the id
--no-tex-convert          Don't convert TEX files into images while extracting PKG
-p, --only-tex-images     Skip raw .tex output, keep only converted images (the .tex-json sidecar
                          is still written)
-I, --output-ignoreexts   Don't write files with these extensions. Output-level filter: entries are
                          still parsed and TEX still converted, only the write is skipped; a
                          converted TEX image is judged by the format it converted into (.png/.jpg),
                          a raw entry by its own extension
-E, --output-onlyexts     Only write files with these extensions (judged the same way as -I)
--filter-effect-images <PERCENT>
                          Skip entries whose converted image is mostly transparent or black
                          (typical effect sprites); threshold percent 1-100, 0 = off, e.g. 85 = skip
                          when the transparent OR black ratio is >= 85%
--onlypaths <PREFIXES>    Only extract entries under these directory prefixes (comma-delimited,
                          subfolders included, both \ and / accepted; e.g. materials, materials/masks)
--ignorepaths <PREFIXES>  Don't extract entries under these directory prefixes (same syntax)
--paths-depth <N>         Limit --onlypaths/--ignorepaths to N path segments after the prefix
                          (1 = direct children only, subfolders excluded; 0 = unlimited, default)
--overwrite               Overwrite all existing files
--lazy                    Read entry bytes one at a time instead of loading the whole package
--max-entry-size <KB>     Skip entries larger than this, in KB
--min-entry-size <KB>     Skip entries smaller than this, in KB
```
- info - Dumps PKG/TEX info
```
-s, --sort                Sort entries a-z
-b, --sortby <KEY>        Sort entries by name, extension or size (default: name; an unrecognized
                          value falls back to name)
-t, --tex                 Dump info about all TEX files from the directory given as input
-p, --projectinfo <KEYS>  Keys to dump from project.json (comma-delimited, * for all)
-e, --printentries        Print entries in packages
--title-filter <TEXT>     Only list packages whose project.json title contains this text
                          (case-insensitive)
```
Exits 1 when the input path is missing/invalid or at least one file failed to parse (the failing
files are named on stderr / reported inline, e.g. `Failed to read package: …` — a corrupt package
no longer aborts the run with a raw exception); otherwise 0.
- batch - runs many wallpapers in one process, driven by a JSON manifest; `mode` selects unpacking
  them to files (`extract`), rewriting them as mobile packages (`mpkg`), converting mobile
  packages back to PC ones (`pkg`), or packing a project directory into a PC package (`pack`)
```
repkg batch --manifest manifest.json [--threads 8]
```
`--threads` overrides the manifest `threads`; when it is 0 the manifest value is used, and when that
is 0 as well the worker count falls back to the physical core count (hyper-threading adds no
throughput here and doubles memory) — narrowed to the cgroup CPU quota when the kernel gives one.
Failures never stop a batch: each one is reported as a JSON
event on stdout and the next wallpaper is started. A finished batch always exits 0; only a missing
or invalid manifest exits 1.

Progress is reported as one JSON object per line on stdout (wallpaper start/done, entry, error,
batch done); the batch continues on errors and always exits 0 unless the manifest is invalid.
stdout carries nothing but those events — the single diagnostic line a run prints
(`* gate: avail=503MB (cgroup v2:/user.slice/…) threads=1 (cgroup v2 cpu.max:…)`) goes to stderr,
which is how you tell whether the memory gate is seeing the container's limit or the host's.
- pack - packs a wallpaper project directory (the loose files the editor works on) back into a PKG
```
-o, --output <DIR>       Directory for the produced .pkg (default: ./output)
-n, --name <NAME>        File name stem of the produced .pkg (default: project folder name)
--magic <MAGIC>          Package magic to write (default PKGV0018)
--overwrite              Overwrite an existing target PKG instead of writing name_1.pkg
--keep-source-images     Also pack the source images alongside their .tex (real WE packages
                         carry none)
--no-tex-encode          Do not wrap source images into a passthrough .tex; ship them as their
                         own entries
--dxt <FORMAT>           Block-compress wrapped textures into DXT1, DXT3 or DXT5 instead of the
                         default passthrough PNG/JPEG blob (accepted: dxt1/dxt3/dxt5 or 1/3/5).
                         The result is a TEXB0002 .tex with a full mip chain (halved to 4x4),
                         every mip LZ4-compressed when that actually shrinks it. Multi-frame GIFs
                         and images smaller than 4x4 cannot be block-encoded and fall back to the
                         passthrough shape (or the raw entry), reported per file. An invalid
                         --dxt value exits 1 without packing anything
--no-loose-metadata      Do not copy project.json / the preview image next to the produced .pkg
--excludepaths <PREFIXES>
                         Also skip these relative path prefixes (comma-delimited)
```
`<input>` is either a project directory (it holds `project.json`, `scene.json`, `index.html` or
`assets.json` at its root) or a directory whose subfolders are projects — in the latter case every
subfolder is packed on its own. Several projects sharing one output directory is supported; see
`mode: "pack"` below for what happens to their `project.json`.

#### Manifest schema

Manifest keys are matched case-insensitively (`onlyPaths` and `onlypaths` are the same key).

| Key | Type | Default | Meaning | CLI equivalent |
| --- | --- | --- | --- | --- |
| `mode` | string | `extract` | `extract` = unpack to files; `mpkg` = rewrite the package as a mobile `.mpkg`; `pkg` = convert a mobile package back to a PC `.pkg`; `pack` = pack a project directory into a PC `.pkg` (see below); `inspect` = read-only health check, writes nothing (see below) | - |
| `threads` | int | 0 | worker threads, 0 = physical core count (narrowed by the cgroup CPU quota) | `--threads` (wins over this) |
| `wallpapers` | array | - | jobs, one item per wallpaper; required | - |
| `wallpapers[].id` | string | - | echoed back in every event of this wallpaper | - |
| `wallpapers[].input` | string | - | a `.pkg`/`.mpkg` file, or a directory searched recursively for them; `mode: pack` wants a project directory instead (or a parent of several); `mode: inspect` never writes, so it is the one mode that does not need `output` | `<input>` |
| `wallpapers[].output` | string | - | output directory for this wallpaper | `--output` |
| `wallpapers[].outputName` | string | source package name (pack: project folder name) | `mode: mpkg` / `mode: pkg` / `mode: pack` - file name (without extension) of the produced `.mpkg`/`.pkg`; invalid characters are replaced with `_`, and a wallpaper that yields several packages gets `_<source-name>` appended so they don't overwrite each other | `-n, --name` (pack) |
| `wallpapers[].options` | object | every key falls back to global `options` | `mode: mpkg` only (`mode: inspect` reads the same keys to describe what a tier would do) - per-entry overrides for the nine packing keys (`preset` / `mpkgMagic` / `keepAudio` / `noLz4` / `mpkgReduction` / `mpkgEtc2` / `mpkgNoShaderCompat` / `mpkgNoDematerialize` / `mpkgShrinkDx`). Only keys actually present override, so an entry can change just the reduction and inherit the rest; an entry `preset` fills only the two cells that entry did not write, so precedence is explicit key > entry preset > global (preset included) > default. A combination that is only illegal *after* merging (e.g. `mpkgEtc2` on an entry whose resolved `mpkgReduction` is 1) fails the manifest at load time - nothing gets packed. Lets a caller send one batch instead of one batch per preset | `-` |
| `options.overwrite` | bool | false | overwrite existing files | `--overwrite` |
| `options.onlypaths` | string[] | none | directory prefixes to keep | `--onlypaths` |
| `options.ignorepaths` | string[] | none | directory prefixes to drop | `--ignorepaths` |
| `options.pathsDepth` | int | 0 | depth limit for the two above | `--paths-depth` |
| `options.onlyexts` | string[] | none | extract-level extension filter | `--onlyexts` |
| `options.ignoreexts` | string[] | none | extract-level extension filter | `--ignoreexts` |
| `options.outputOnlyExts` | string[] | none | write-level extension filter | `--output-onlyexts` |
| `options.outputIgnoreExts` | string[] | none | write-level extension filter | `--output-ignoreexts` |
| `options.singleDir` | bool | false | `true` flattens the entry paths into one output directory (same meaning as `-s/--singledir`). The legacy key `keepSubfolderStructure` is still read — its name says the opposite of what it does — but it now prints a deprecation warning on stderr; when both keys are present `singleDir` wins | `-s, --singledir` |
| `options.noTexConvert` | bool | false | don't convert TEX to images | `--no-tex-convert` |
| `options.onlyTexImages` | bool | false | skip raw `.tex` writes | `-p, --only-tex-images` |
| `options.filterEffectImages` | int | 0 | read as an integer percent | `--filter-effect-images` |
| `options.mpkgMagic` | string | `PKGM0019` | `mode: mpkg` only - magic written into the output package | - |
| `options.keepAudio` | bool | false | `mode: mpkg` only - keep `sounds/*.mp3` instead of dropping them | - |
| `options.noLz4` | bool | false | `mode: mpkg` only - don't try to LZ4-compress materialized pixels | - |
| `options.preset` | string | - | `mode: mpkg` only - one of `1x` / `2x` / `4x` (case-insensitive; the `×` glyph is accepted too), expanding to WE's own three-tier pair: `1x` = `mpkgReduction: 1` + no ETC2, `2x` = `2` + ETC2, `4x` = `4` + ETC2. It only fills in keys you did not write - an explicit `mpkgReduction` / `mpkgEtc2` in the same block wins, which is how "2x but keep RGBA8" stays expressible. An unknown name is a manifest error (exit 1), never a silent fallback to `1x` | - |
| `options.mpkgReduction` | int | 1 | `mode: mpkg` only - texture downscale divisor (any value >= 1; the presets above use 1/2/4). Only entries that get materialized are resized; the size is also recorded as `"texturereduction"` in the scene file | - |
| `options.mpkgEtc2` | bool | false | `mode: mpkg` only - emit reduced pixels as ETC2 RGBA8 (`format=5`, 1 byte/pixel) instead of RGBA8. Requires `mpkgReduction > 1`; not yet verified on a phone, hence off by default | - |
| `options.mpkgNoShaderCompat` | bool | false | `mode: mpkg` only - `true` turns off the GLSL → GLSL ES rewrite that appends `.0` to integer literals sitting in a float context. Mobile GLSL has no implicit int→float, so a shader that fails to compile makes its material fall back to the base texture and the layer shows up as a plain white rectangle. Every rewrite is reported per package as `着色器改写 N条/M处` | - |
| `options.mpkgNoDematerialize` | bool | false | `mode: mpkg` only - `true` copies every `.tex` verbatim instead of materializing it to RGBA8/ETC2, i.e. "same container, same pixels". **It is the parent of `mpkgReduction` / `mpkgEtc2` / `mpkgShrinkDx`**: when it is set, those three are normalised to `1` / `false` / `false` during manifest parsing - they neither error nor do anything, and both the summary line and `mode: inspect` report the normalised values, so a manifest can no longer express "4x + fmt5 + copy" (four switches on, nothing happening). `noLz4` has no object either (LZ4 only applies to materialized mips), but its value is deliberately left alone: it changes no output bytes and appears in no reading, so rewriting it would only make the manifest contradict the log. Shader rewrites still happen (that is not pixel work). Callers that construct `MobilePackageOptions` directly bypass this derivation - the converter keeps an error-level warning plus the omitted `texturereduction` key as the backstop there. The reverse path spells the same idea without the prefix (`noDematerialize`, `mode: pkg` only) - the two keys are read from one `options` block, so writing the wrong one is silently ignored by the mode you asked for | - |
| `options.mpkgShrinkDx` | bool | false | `mode: mpkg` only - also decode-and-resize `DXT1/3/5` payloads instead of copying them byte for byte. Off by default because that path was previously reached only together with `mpkgEtc2` (whose bytes are the phone-verified ones), and DXT is cheaper than RGBA8: on a DXT1 wallpaper `mpkgReduction: 2` with ETC2 off makes the package roughly twice as large, not smaller. Inert when `mpkgReduction` is 1 - no error, since a global key plus a per-entry 1× is a normal manifest shape. Counted per package as `DXT重缩 N` | - |
| `options.pkgMagic` | string | `PKGV0018` | `mode: pkg` and `mode: pack` - magic written into the output PC package | `--magic` (pack) |
| `options.noDematerialize` | bool | false | `mode: pkg` only - `true` copies materialized RGBA8 textures verbatim instead of re-encoding them back to a PNG passthrough blob (useful when debugging the reverse path) | - |
| `options.keepReductionKey` | bool | false | `mode: pkg` only - `true` keeps the `"texturereduction"` key in `scene.json` instead of removing it | - |
| `options.packKeepSourceImages` | bool | false | `mode: pack` only - also pack source images next to their `.tex` | `--keep-source-images` |
| `options.packNoEncode` | bool | false | `mode: pack` only - don't wrap source images into a passthrough `.tex` | `--no-tex-encode` |
| `options.packDxt` | string | none | `mode: pack` only - block-compress wrapped textures: `dxt1`/`dxt3`/`dxt5` (or `1`/`3`/`5`), same semantics and fallback rules as `--dxt`; an invalid value makes the manifest invalid (exit 1) | `--dxt` |
| `options.packNoLooseMetadata` | bool | false | `mode: pack` only - don't copy `project.json` / the preview next to the produced `.pkg` | `--no-loose-metadata` |
| `options.packExcludePaths` | string[] | none | `mode: pack` only - extra relative path prefixes to skip | `--excludepaths` |

#### mode: "pack" - packing a project directory back into a PC package

```
{ "mode": "pack", "wallpapers": [ { "id": "1", "input": "D:/WE/projects/myprojects/my wp", "output": "D:/share", "outputName": "my wp" } ] }
```

The inverse of `extract`: the input is a **directory of loose files**, not a package, so there is no
source entry table to copy — the plan is built from a directory walk. The container is written the
same way the other two pack modes write it (one pass, offsets patched afterwards, nothing is ever
buffered as a whole package), entries are named with forward slashes relative to the project root,
and the produced file satisfies the same invariant the reader checks: table size + Σ entry lengths
= file size.

`wallpapers[].input` may be the project directory itself (it holds `project.json`, `scene.json`,
`index.html` or `assets.json`) or a parent of several — then every project subfolder is packed
separately. It is deliberately not recursed deeper: effect packages legitimately contain
`preview/project.json`, and treating that as a new wallpaper would invent wallpapers out of
sub-assets.

What goes in, and what does not, is decided from a census of 279 real local packages (8152 `.tex`
entries): the complete extension set inside a real WE package is `json / tex / frag / vert / mdl /
mp3 / ttf / otf / wav / ogg / flac / ttc / gif`. There is **no** `png`, `jpg`, `tga`, `obj`, `mtl`,
`dxs`, `tex-json`, `pkg`, or `mpkg` entry, and no `project.json` / preview entry either. So:

- a source image with a same-named `.tex` next to it is dropped and the `.tex` is packed verbatim —
  that `.tex` is what the editor compiled, so it is closer to the original than anything we could
  re-encode.
- a source image with **no** `.tex` is wrapped into a **passthrough `.tex`** (`TEXB0004`,
  `imageFormat = FIF_PNG`/`FIF_JPEG`, one mip, payload = the image file's own bytes, never
  re-encoded). This is not an invented shape: 1348 of the surveyed textures are exactly that,
  shipped by WE itself. Header flags are taken from the `<name>.tex-json` import-settings sidecar
  when one is there, because `clampuvs` decides whether the GPU samples across the edge.
- `.obj`/`.mtl` are dropped when a same-named `.mdl` exists; without one they are packed as-is and
  reported (there is no `.mdl` writer).
- `.tex-json`, `*.dxs` and `shaders/blobsSM*/` (the editor's compiled-shader cache), other
  `.pkg`/`.mpkg` files found inside the tree, `project.json` and the preview image are never
  packed.
- `project.json` and the preview image are written **next to** the `.pkg` instead, which is the
  layout Workshop subscription folders use and what makes the output folder loadable. Two projects
  packed into the same output directory both want a `project.json` there, so the second one is not
  overwritten — it is reported as an error event. Pass `--no-loose-metadata` to skip the copies.
- An existing target of the same name is never overwritten: `scene.pkg` → `scene_1.pkg` → …
  (pointing the output at a subscription folder would otherwise destroy a Steam-downloaded original
  and make WE re-download it on the next integrity check). `--overwrite` opts in.

Two things to know about the produced package:

- **The default `.tex` we compile is a passthrough blob, which is bigger than the editor's.** A
  passthrough PNG costs what the PNG costs, while the editor's DXT5 is 1 byte/pixel (4K: 8 MB
  against 33 MB uploaded). That shape is not invented — 1348 of the surveyed textures are exactly
  it — but if size matters, `--dxt dxt5` switches every wrapped texture to real block compression:
  a `TEXB0002` with a full mip chain, each mip LZ4'd when that pays. It is opt-in because the
  passthrough shape is what WE itself ships for imported PNGs, and because block compression is
  lossy in a way a PNG blob is not. Images it cannot encode (multi-frame GIFs, anything under 4x4)
  fall back to the passthrough shape or the raw entry, and the fallback is reported per file.
  Every wrapped texture is counted in the `封纹理 N` report line; `--no-tex-encode` turns wrapping
  off entirely if you would rather see the raw image entry than a texture you did not ask for.
- **A same-named `.tex` that the editor has not refreshed after an image edit is shipped as-is.**
  The packer compares file names, not image content, so it cannot notice that `foo.png` changed
  after `foo.tex` was compiled. WE itself renders from the `.tex`, so this matches the editor.

#### mode: "mpkg" - converting a PC package into a mobile one

```
{ "mode": "mpkg", "wallpapers": [ { "id": "1", "input": "C:/.../431960/123/scene.pkg", "output": "C:/out" } ] }
```

Each `.pkg`/`.mpkg` input becomes `<output>/<same-name>.mpkg`, or `<output>/<outputName>.mpkg` when the
job carries that key (callers typically pass the wallpaper title or the workshop id). The entry table is
rebuilt, the magic becomes `mpkgMagic`, and `project.json` / the preview image are embedded from the files
sitting next to the input package (a package that already carries them keeps its own copies).

Per entry, exactly one of these happens:

- a texture whose payload is a **passthrough encoded image** (TEX container `imageFormat != FIF_UNKNOWN`,
  i.e. the pixels are stored as a PNG/JPEG blob) is decoded to raw RGBA8 - straight alpha, one mip,
  `TEXB0004` with `imageFormat = FIF_UNKNOWN`, and LZ4 when that actually shrinks the payload. Mobile
  reads such an entry as `width*height*4` raw bytes, so a passthrough blob renders as garbage.
- **everything else is copied byte for byte**, including DXT1/3/5 textures with their full mip chains,
  R8/RG88 masks, video textures (an mp4 embedded in a `.tex`), models and JSON. Textures the
  reader cannot parse are copied as well, so an unknown format never blocks a conversion.
  The one way a `.tex` stops being copied is a reduction: with `mpkgReduction > 1` a DXT payload is
  decoded and resized too once `mpkgEtc2` is on (that pairing is the phone-verified shape) or once
  `mpkgShrinkDx` asks for it on its own. On DXT that decode is what makes a reduction possible at all -
  and DXT costs fewer bytes than RGBA8, so `mpkgShrinkDx` without `mpkgEtc2` can make the package bigger
  than it started. Set `mpkgNoDematerialize` to copy every `.tex` regardless -
  that is the "same pixels, new container" package.
- `.frag`/`.vert` sources are copied byte for byte **except** that an integer literal standing in a float
  context gets a `.0` suffix (the rewrite is insert-only: the output equals the input with `".0"` spliced in
  after `N` literals, and nothing else changes). Mobile GLSL has no implicit int→float, so an unpatched
  shader fails to compile and its material silently falls back to the base texture - on screen that is a
  white rectangle. Set `mpkgNoShaderCompat` to keep the sources untouched.

`mode: mpkg` runs one package at a time and caps wallpaper concurrency at 2: packing writes a single
output file, so entries cannot be spread over workers, and one materialized 8K texture alone can need
~230 MB of pixel buffer.

With `mpkgReduction = 1` (the default) the conversion is deliberately faithful rather than small: it never
rescales a texture, so an output package is typically about twice the size of a Wallpaper Engine export made
with a reduced quality setting (which halves every texture and re-encodes them).

Not expressible in a manifest: `--tex`, `--recursive`, `--usename`, `--copyproject`,
`--min-entry-size`, `--max-entry-size` (no manifest key exists for them), and `--lazy` - the batch
executor already reads entries on demand, so the flag is forced off (`BatchManifest.cs`).

#### mode: "pkg" - converting a mobile package back to a PC one

```
{ "mode": "pkg", "wallpapers": [ { "id": "1", "input": "C:/out/scene.mpkg", "output": "C:/pc" } ] }
```

The reverse of `mode: mpkg`: the entry table is rebuilt with the magic set to `pkgMagic`
(`PKGV0018` by default), and `project.json` / the preview image already inside the package are
kept as-is. Like `mpkg` it runs one package at a time with wallpaper concurrency capped at 2, and
its events use the same protocol.

Per entry:

- a texture that is **our own materialized output** (container `TEXB0004`, `imageFormat =
  FIF_UNKNOWN`, header format RGBA8, a single frame) is re-encoded losslessly back into a PNG
  passthrough blob and written as `imageFormat = FIF_PNG`. The pixel data round-trips byte for
  byte; the encoded blob is not identical to the original PNG bytes, because those were discarded
  by the forward conversion and cannot be recovered. Set `noDematerialize` to copy such entries
  verbatim instead.
- a `"texturereduction"` key in `scene.json` is removed, restoring the PC scene file (set
  `keepReductionKey` to leave it alone).
- **everything else is copied byte for byte**: DXT1/3/5 textures, R8/RG88 masks, video textures,
  models, JSON, and textures the reader cannot parse (reported as an error event, never a
  blocker).

Two things the reverse path deliberately does **not** attempt:

- **Shader `.0` rewrite is not undone.** The forward rewrite is insert-only and a PC GLSL source
  may legitimately contain the same `1.0` literals, so an appended `.0` cannot be told apart from
  a native one. Left-in `.0` suffixes are valid GLSL for the PC driver, so undoing them would be
  risk without benefit.
- **Dropped audio cannot come back**, and ETC2 (or downscaled) pixels cannot be restored to their
  original resolution — those inputs are gone by the time the `.mpkg` exists. Converting a
  `mpkgReduction > 1` package back yields a valid PC package with the smaller textures it carries.

A Wallpaper Engine *phone export* (as opposed to one produced by this tool) usually stores DXT or
already-encoded textures; for those the conversion is the container swap alone and is lossless.

Manifest format (0 = physical core count for threads; options match extract):
```
{
  "threads": 0,
  "wallpapers": [
    { "id": "0", "input": "C:/path/to/wallpaper_dir", "output": "C:/path/to/out/wallpaper_0" }
  ],
  "options": { "overwrite": true, "onlypaths": ["materials"], "filterEffectImages": 85 }
}
```
 
#### mode: "inspect" - read-only health check, writes nothing

```
{ "mode": "inspect", "wallpapers": [ { "id": "1", "input": "C:/.../431960/123", "options": { "preset": "4x" } } ],
  "options": { "preset": "2x" } }
```

The reason this mode exists is one silent behaviour of `mpkg`: a `.tex` it copies says nothing about
*why*. A wallpaper whose textures are all DXT5 comes out the same size at `1x` and at `4x`, and the only
reading is `缩小 0` - which does not distinguish "nothing here can shrink" from "the switch that would
have shrunk it is off". The probe replays the converter's own predicate (`MobileTextureMaterializer.WouldReduce`,
same class that decides what to materialize) over structure-only TEX reads, so a caller can say
"68.5% of this package's bytes are DXT5; without ETC2 or shrink-Dx no tier will move them" *before* the batch.

It reads no pixels and writes no file, so `wallpapers[].output` may be left out. The tier keys it understands
are exactly the `mode: mpkg` ones - `preset` / `mpkgReduction` / `mpkgEtc2` / `mpkgShrinkDx` /
`mpkgNoDematerialize`, per entry or global, same precedence - because a probe that disagreed with the
converter about what a tier means would be worse than no probe. The same derivation applies here: on a row where
`mpkgNoDematerialize` is true, the event reports the normalised `reduction`/`etc2`/`shrinkDx` (`1`/`false`/`false`) -
a probe claiming "4x will shrink 7 textures" while textures are being copied verbatim would be the lie.
One event per package, plus the usual `wallpaper` start/done and `error` lines:

```
{"id":"1","type":"inspect","file":".../scene.pkg","entries":12,"bytes":1355687,"tex":1,"texBytes":1343778,
 "passthrough":0,"dxt":1,"dxtBytes":1343778,"raw":0,"mask":0,"video":0,"noimages":0,"unreadable":0,
 "audio":0,"audioBytes":0,"scene":true,"reduction":4,"etc2":false,"shrinkDx":false,"dematerialize":true,
 "wouldReduce":0,"largestTex":1343778,"failed":false}
```

`passthrough + dxt + raw + mask + video + noimages + unreadable == tex` always holds. `wouldReduce` is the
number of `.tex` entries that tier really resizes - `0` with `tex > 0` is the "缩不动" signal, and with
`dematerialize: false` it is forced to `0` because that is what the package will show.

### Examples
Simply extract PKG and convert TEX entries into images to output folder created in current directory
```
repkg extract E:\Games\steamapps\workshop\content\123\scene.pkg
```
Find PKG files in subfolders of specified directory and make wallpaper engine projects out of them in output directory
```
repkg extract -c E:\Games\steamapps\workshop\content\123
```
Find PKG files in subfolders of specified directory and only convert TEX entries to png then put them in ./output omitting their paths from PKG:
```
repkg extract -e tex -s -o ./output E:\Games\steamapps\workshop\content\123
```
Convert all TEX files to images from specific folder
```
repkg extract -t -s E:\path\to\dir\with\tex\files
```