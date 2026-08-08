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
- help - shows those commands, use `help "extract"` and `help "info"` to see options for them
- extract - extracts specified PKG/TEX file, or files from folder
```
-o, --output          (Default: ./output) Output directory
-i, --ignoreexts      Don't extract files with specified extensions (delimited by comma ",")
-e, --onlyexts        Only extract files with specified extensions (delimited by comma ",")
-t, --tex             Convert all TEX files into images from specified directory in input
-s, --singledir       Should all extracted files be put in one directory instead of their entry path
-r, --recursive       Recursive search in all subfolders of specified directory
-c, --copyproject     Copy project.json and preview.jpg from beside PKG into output directory
-n, --usename         Use name from project.json as project subfolder name instead of id
--no-tex-convert      Don't convert TEX files into images while extracting PKG
--overwrite           Overwrite all existing files
--lazy                Read entry bytes one at a time instead of loading the whole package
--min-entry-size <KB> Skip entries smaller than this, in KB
--max-entry-size <KB> Skip entries larger than this, in KB
--filter-effect-images <percent>  Skip entries whose converted image is mostly transparent
                      or black (effect images); value = threshold percent 1-100 (0 = off),
                      e.g. 85 = skip when transparent OR black ratio >= 85%
--onlypaths           Only extract entries under the given directory prefix(es), comma-delimited;
                      subfolders included (e.g. materials or materials/masks); \ and / both accepted
--ignorepaths         Don't extract entries under the given directory prefix(es), comma-delimited
--paths-depth <N>     Limit directory filtering depth (1 = direct files only, 0 = unlimited, default)
-p, --only-tex-images Only save the image a TEX converts into, skip raw .tex and .tex-json
-I, --output-ignoreexts  Don't write output files with these extensions (converted images are
                      judged by their converted format, e.g. .png; raw files by their own extension)
-E, --output-onlyexts    Only write output files with these extensions (same judgement as -I)
```
- batch - Extracts multiple wallpapers from a manifest file in one process
```
repkg batch --manifest manifest.json [--threads 8]
```
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
Progress is reported as one JSON object per line on stdout (wallpaper start/done, entry, error,
batch done); the batch continues on errors and always exits 0 unless the manifest is invalid.
- info - Dumps PKG/TEX info
```
-s, --sort             Sort entries a-z
-b, --sortby           (Default: name) Sort by ... (available options: name, extension, size)
-t, --tex              Dump info about all TEX files from specified directory
-p, --projectinfo      Keys to dump from project.json (delimit using comma) (* for all)
-e, --printentries     Print entries in packages
--title-filter         Title filter
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