# RePKG_Re

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
- Convert TEX to image
- Dump PKG/TEX info

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
-c, --copyproject         Copy project.json and preview.jpg from beside the PKG into output
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
- batch - extracts many wallpapers in one process, driven by a JSON manifest
```
repkg batch --manifest manifest.json [--threads 8]
```
`--threads` overrides the manifest `threads`; when it is 0 the manifest value is used, and when that
is 0 as well the worker count falls back to the physical core count (hyper-threading adds no
throughput here and doubles memory). Failures never stop a batch: each one is reported as a JSON
event on stdout and the next wallpaper is started. A finished batch always exits 0; only a missing
or invalid manifest exits 1.

Progress is reported as one JSON object per line on stdout (wallpaper start/done, entry, error,
batch done); the batch continues on errors and always exits 0 unless the manifest is invalid.

#### Manifest schema

Manifest keys are matched case-insensitively (`onlyPaths` and `onlypaths` are the same key).

| Key | Type | Default | Meaning | CLI equivalent |
| --- | --- | --- | --- | --- |
| `threads` | int | 0 | worker threads, 0 = physical core count | `--threads` (wins over this) |
| `wallpapers` | array | - | jobs, one item per wallpaper; required | - |
| `wallpapers[].id` | string | - | echoed back in every event of this wallpaper | - |
| `wallpapers[].input` | string | - | a `.pkg`/`.mpkg` file, or a directory searched recursively for them | `<input>` |
| `wallpapers[].output` | string | - | output directory for this wallpaper | `--output` |
| `options.overwrite` | bool | false | overwrite existing files | `--overwrite` |
| `options.onlypaths` | string[] | none | directory prefixes to keep | `--onlypaths` |
| `options.ignorepaths` | string[] | none | directory prefixes to drop | `--ignorepaths` |
| `options.pathsDepth` | int | 0 | depth limit for the two above | `--paths-depth` |
| `options.onlyexts` | string[] | none | extract-level extension filter | `--onlyexts` |
| `options.ignoreexts` | string[] | none | extract-level extension filter | `--ignoreexts` |
| `options.outputOnlyExts` | string[] | none | write-level extension filter | `--output-onlyexts` |
| `options.outputIgnoreExts` | string[] | none | write-level extension filter | `--output-ignoreexts` |
| `options.keepSubfolderStructure` | bool | false | drives `--singledir` directly, so `true` flattens the entry paths into one directory - the key name says the opposite of what it does and is kept for compatibility | `-s, --singledir` |
| `options.noTexConvert` | bool | false | don't convert TEX to images | `--no-tex-convert` |
| `options.onlyTexImages` | bool | false | skip raw `.tex` writes | `-p, --only-tex-images` |
| `options.filterEffectImages` | int | 0 | read as an integer percent | `--filter-effect-images` |

Not expressible in a manifest: `--tex`, `--recursive`, `--usename`, `--copyproject`,
`--min-entry-size`, `--max-entry-size` (no manifest key exists for them), and `--lazy` - the batch
executor already reads entries on demand, so the flag is forced off (`BatchManifest.cs`).

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