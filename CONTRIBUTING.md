# Building Zhongwen Lens from source

Everything below runs from a clean checkout on Windows. There's no CI and no build server — if it
works here, it works.

## Prerequisites

- **Windows 11**, or Windows 10 2004+ (build 19041)
- **[.NET 10 SDK](https://dotnet.microsoft.com/download)**
- **Visual Studio 2022+** with *.NET Desktop Development* — optional, the CLI is enough
- **Windows 10/11 SDK** — only for building an installer (`makeappx`, `signtool`)

## First run

The dictionary and OCR models aren't in the repository. They're ~34 MB of third-party data with
their own licences, and the dictionary is generated rather than stored. Three commands:

```powershell
pwsh -File scripts/fetch-data.ps1      # CC-CEDICT, jieba frequencies, HSK word lists
pwsh -File scripts/fetch-models.ps1    # PP-OCRv4 ONNX models
dotnet run --project src/ZhongwenLens.DataBuild
```

The last step compiles the raw sources into `data/dictionary.db` (~26 MB, about four seconds) and
runs spot-checks against it — it exits non-zero if any fail, so a bad build can't slip through
unnoticed.

Then:

```powershell
dotnet run --project src/ZhongwenLens.App
```

Remember there's **no main window** — look for the tray icon, then press `Ctrl+Alt+Z`.

### In Visual Studio

Open `ZhongwenLens.sln`, set the platform to **x64** (or ARM64), right-click **ZhongwenLens.App**
→ *Set as Startup Project*, then F5.

The solution is a classic `.sln`, not the newer `.slnx`, on purpose: WinUI 3 cannot build Any CPU,
so the solution has to declare x64/ARM64 explicitly and map every project onto them. Expressed as
`.slnx`, Visual Studio reported "unknown project configuration mappings" and refused to launch
anything.

## Project layout

```
src/ZhongwenLens.Core        capture, OCR, text, dictionary, speech, study — no UI
src/ZhongwenLens.App         WinUI 3 shell: hotkey, overlay, result window, tray
src/ZhongwenLens.DataBuild   raw sources -> dictionary.db
scripts/                     data fetchers, icon generation, MSI packaging
installer/                   WiX authoring for the MSI
data/                        downloaded sources and the generated database (gitignored)
```

Everything that can be exercised without a window lives in `Core`; `App` is a thin shell over it.
That split exists because the OCR and segmentation pipelines are the parts most likely to be
subtly wrong, and they're much easier to reason about in isolation.

## Building an installer

Needs [WiX](https://wixtoolset.org/) — pinned to v5, because v6 and later require accepting the
Open Source Maintenance Fee EULA:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2
wix extension add --global WixToolset.Util.wixext/5.0.2
```

Then:

```powershell
pwsh -File scripts/make-icon.ps1     # .ico from assets/logo.png
pwsh -File scripts/package-msi.ps1   # publish, stage, build the MSI
```

This produces `artifacts/ZhongwenLens-<version>-x64.msi` (~94 MB). Both .NET and the Windows App
SDK are published self-contained, so the installed app needs no runtimes on the target machine.

Install it with a double-click, or:

```powershell
msiexec /i artifacts\ZhongwenLens-1.0.0.0-x64.msi
```

### Why MSI, and why per-user

This was MSIX first. MSIX must be signed, and Windows **hard-blocks** installing one whose
certificate it doesn't trust — no "install anyway" option. Getting past that needed either a
commercial certificate or an elevated PowerShell command to import a self-signed one, which is
far too much to ask before someone has even tried the app. An unsigned MSI raises a SmartScreen
warning instead, which is dismissible.

The MSI installs **per-user** into `%LOCALAPPDATA%`, so there's no UAC prompt at all. The app is
a personal tray utility that already stores its data per user, so a per-machine install into
Program Files would buy nothing and cost an elevation prompt. It also means uninstalling leaves
`%LOCALAPPDATA%\ZhongwenLens` — the saved words and log — alone, which the MSIX build did not.

`UpgradeCode` in `installer/ZhongwenLens.wxs` must never change: it's how Windows recognises a
new build as an upgrade rather than a second copy. Bump `Version` instead. Note MSI only compares
the first three version fields when deciding to upgrade, so `1.0.0.1` will not replace `1.0.0.0`.

## Things worth knowing before you change something

A few decisions look arbitrary until they bite you. The reasoning for each lives in a comment at
the relevant place in the source; these are the ones most likely to trip up a change.

**`dotnet publish` silently drops WinUI's XAML resources.** The compiled markup (`.xbf`) and the
app resource index (`.pri`) are produced by the build but not added to the publish set. A
published app starts fine and then throws `XamlParseException` the first time it opens a window.
There's a target in `ZhongwenLens.App.csproj` that adds them back. If you see that exception in a
packaged build and not from `bin/`, that's why.

**Coordinates are physical pixels, always.** The overlay tracks the selection with `GetCursorPos`
and converts to DIPs only for drawing. Pointer events arrive in DIPs relative to an element and
need the scale factor of whichever monitor the cursor is over, which is ambiguous the moment the
overlay spans two monitors at different DPI. A captured bitmap also starts at pixel (0,0) while
the desktop it came from usually doesn't — on a layout with a monitor to the left of the primary,
the primary's origin is well inside the bitmap. Those conversions live on `VirtualDesktop` as
named methods; use them rather than doing the arithmetic inline.

**The OCR post-processing constants are calibrated, not chosen.** The box expansion ratio is 2.2,
not PaddleOCR's 1.5, because at 1.5 characters lose a left radical or top stroke (他 read as 也,
文 as 又). The value came from a parameter sweep; if you change it, sweep it again rather than
guessing.

**The segmenter's vocabulary is the dictionary's own headword list.** That's what guarantees every
word token has a definition to display. It also means number + measure-word combinations split —
一杯 isn't a CC-CEDICT headword — which is a known consequence, not a bug.

**Don't reorder dictionary results by frequency alone.** Frequency is stored per spelling, so all
readings of a word tie and the tiebreak used to be an arbitrary row id. That made 书 lead with
"abbr. for 書經" instead of "book". `SqliteDictionary.RankForLearners` sorts abbreviations,
surnames and variant-of entries last.

## Data licensing

The code is MIT (see [LICENSE](LICENSE)), but the dictionary data is not. CC-CEDICT is
**CC BY-SA 4.0**, and the generated `dictionary.db` is a derivative work, so distributing a build
carries attribution and share-alike obligations on that data specifically. The application code
is unaffected. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
