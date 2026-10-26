# Application artwork

`FluffHistory.png` is the 1068 × 854 transparent folder/history-clock composite used
to reproduce application assets. SHA-256:
`A5457E427F0502E9803B46E2BCB4EB4E1130F77292B944B28A4F5FFF149AF5D5`.

The base folder comes from Icons8 Flat Color Icons:
[source collection](https://github.com/icons8/flat-color-icons),
[reference SVG](https://de.wikipedia.org/wiki/Datei:Icons8_flat_folder.svg).
[LICENSE-Icons8.txt](LICENSE-Icons8.txt) retains its MIT notice and ships as
ThirdPartyNotices/Icons8.txt. That provenance does not attribute the added clock
artwork to Icons8. First-party composite additions are subject to the project's
first-party license status; see [licensing](../docs/LICENSING.md).

On Windows, run `scripts/Convert-ApplicationArtwork.ps1` from the repository root.
It scales/letterboxes existing pixels on transparent squares without redrawing.
ICO frames are 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixels. Small frames use
32-bit DIB/alpha/masks; 256 uses PNG. Package logos include scale and unplated
target-size variants. Preserve source aspect ratio.

Configuration icons are loaded from the installed OS: stock fixed drive, Explorer
application icon, shell32 folder-with-red-X resource 16715, Task Scheduler
miguiresource index 1 and shell32 gear resource 16826 with native fallback. The
battery glyph uses installed Segoe Fluent Icons. Known library icons also come from
Windows. None of these OS resources is redistributed here or represented as MIT.
The separately bundled Shell history SVG/ICO has its own
[Fluent MIT provenance](../native/Shell/Assets/README.md).
