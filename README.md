# ModArchiveBrowser

> **This is a fork. The original plugin was written by [Noevain](https://github.com/Noevain), at
> [Noevain/ModArchiveBrowser](https://github.com/Noevain/ModArchiveBrowser).**
>
> The idea, the architecture and the whole first implementation are theirs. This fork updates it and
> builds on it — it did not start from a blank page.
>
> GitHub does not display the relationship, because this repository was created separately rather
> than forked through the site. Hence this notice.

Browse [xivmodarchive.com](https://www.xivmodarchive.com/) from inside FFXIV and install mods into
Penumbra in one click.

## Why this fork exists

The original stopped being updated in December 2024 and no longer loads: Dalamud has moved from API
11 to 15 and from .NET 8 to .NET 10 since, and two of its page selectors broke when xivmodarchive
changed its markup. This fork brings it back, then adds update checking, availability badges,
adult-content handling and a rebuilt interface.

Licensed AGPL-3.0, like the original, and it stays that way.

## !!Disclaimer!!

This is a 3rd party plugin. If you are having problems with it, open an issue on GitHub,
**do NOT go to the Dalamud discord asking for help**.

## Features

- Browse, search and filter the archive without leaving the game
- One-click install into Penumbra, with the button telling you what will actually happen:
  install, update, already installed, or not installable — and why
- **Update checker**: compares your installed mods against what xivmodarchive publishes today.
  Penumbra records where each mod came from, so the check costs one request per installed mod
  rather than crawling the whole site
- **Update all**, replacing each mod in place rather than stacking versions: your settings and
  option choices are carried across every collection, and the old version is removed. Mods hosted
  elsewhere cannot be updated from here, and are listed by name instead of silently skipped
- **Version history** with the author's patch notes, so an update is not a leap in the dark
- **Enable in a collection on install**, so you do not have to open Penumbra and tick the mod
  yourself. Off by default — a mod installs the way it always did until you pick a collection
- **Availability badges** on the grid: whether a mod can be installed from here is known before
  you click, not after — and one button hides what is known to be out of reach
- **The whole catalogue**, not the slice the site returns by default: asking for no mod type gets
  you xivmodarchive's own restrictive list, which leaves out poses alone — 30,381 mods
- Adult content is off by default; when enabled it is mixed in with the rest, thumbnails
  obscured until hovered
- Mod thumbnails are saved and shown in Penumbra, [a la Heliosphere](https://github.com/heliosphere-xiv)

## What it cannot do

**Roughly a quarter of the catalogue is not installable from here.** Many mod pages are only a
shop window pointing at Mega, Google Drive or Patreon; the file never touches xivmodarchive.
Measured on a 45-mod sample spread across the catalogue: 76% hosted by xivmodarchive, of which
69% are `.pmp` or `.ttmp2` and install directly. The rest is named and greyed out rather than
failing silently.

**Mods published on Heliosphere are not a dead end**, but they install through their own plugin.
The button opens their page instead.

**Adult detection depends on the mod author.** Nothing forces them to flag their mod, so obscuring
only what is declared misses whatever was not. If that matters to you, the settings offer
"Obscure every thumbnail", which cannot miss anything.

**An archive is not always a modpack.** A `.zip` on xivmodarchive may hold the author's Photoshop
or Blender sources rather than something Penumbra can read. The button says so before you download.

## Bug reporting

If you found a bug please open an issue on GitHub detailing

- steps to reproduce the bug if possible
- logs from /xllog or dalamud.log when the bug/crash occurred

## How to install

You need [Penumbra](https://github.com/xivdev/Penumbra) installed first.

1. `/xlsettings` → **Experimental** tab
2. Paste this into the custom plugin repositories list:
   ```
   https://raw.githubusercontent.com/vhub59/ModArchiveBrowser/master/repo.json
   ```
3. Click the **+** button, then **Save and Close**
4. `/xlplugins` → search for **XIV Mod Archive Browser** → Install

### Building from source

1. Install the .NET 10 SDK
2. `dotnet build ModArchiveBrowser/ModArchiveBrowser.csproj -c Release`
3. `/xlsettings` → Experimental → Dev Plugin Locations → add the built `ModArchiveBrowser.dll`

No submodule or sibling checkout is needed: `Penumbra.Api` comes from NuGet, where the original
expected a `../Penumbra.Api` project on disk.

## Commands

| Command | What it does |
| --- | --- |
| `/archive` | Open the browser on the homepage |
| `/modsearch` | Open it on the search tab |
| `/modid <id>` | Open a specific mod straight away |
| `/archiveconfig` | Settings |

## Being a good guest

xivmodarchive has no public API. Everything here is read from the pages their own site serves, so
the plugin tries not to be a burden: requests are spaced out, results and thumbnails are cached
between sessions, leaving a page cancels whatever it was still loading, and nothing is fetched
twice if it has not changed.

If you fork this further, please keep that part.

## Tests

```
dotnet test ModArchiveBrowser.Tests/ModArchiveBrowser.Tests.csproj --filter "Category!=Live"
```

The suite is aimed at the one failure this plugin cannot detect on its own. xivmodarchive has no
API, so every field is read out of its HTML with an XPath selector — and when the site changes its
markup, nothing throws. Parsing quietly falls back to `Untitled`, `Unknown`, `none`, logs a warning
nobody reads, and hands back a grid of empty-looking cards. That is how the original repository
died.

So the tests assert the *absence* of those placeholders, against pages of the site saved under
`ModArchiveBrowser.Tests/Fixtures/`. They also pin the search URL, where every bug so far has been
invisible: a wrong sort key the site silently ignores, a compatibility threshold that quietly
halved the results, a missing `types` parameter that hid a third of the catalogue.

```
dotnet test ModArchiveBrowser.Tests/ModArchiveBrowser.Tests.csproj --filter "Category=Live"
```

`Category=Live` runs the same checks against xivmodarchive as it is right now. Saved pages prove
the code could parse the site on the day they were captured; only the live run can tell you it
changed since.

**This one has to be run from a normal connection.** xivmodarchive answers 403 to requests from
datacentre addresses, so it cannot run in CI — the only way around that would be to pretend to be
a browser, which is the opposite of what the section below promises. Run it by hand before
cutting a release, and whenever the plugin starts showing empty cards.

When the site does change: fix the selector, then refresh the saved pages so the offline suite
matches reality again.

## Contributing

Contributions are welcome. Please open an issue discussing what you want to add before you start,
to see if it is within scope.

Pushing to `master` builds and runs the offline tests. Pushing a `vX.Y.Z` tag builds the plugin,
publishes the release, and points `repo.json` at it — so an installable release never lags behind
the code.

## Credits

The original plugin is the work of [Noevain](https://github.com/Noevain). Everything this fork does
rests on their design: the in-game grid, the Penumbra integration, the page parsing. Where this fork
fixed bugs, they were bugs in code that already worked well enough to be worth fixing.

Upstream is kept as a git remote, and the migration can be offered back as a pull request if they
ever want it.

## Supporting

Support [Noevain](https://ko-fi.com/noevain), who wrote the original, and the contributors of
[Penumbra](https://github.com/xivdev/Penumbra), [Dalamud](https://github.com/goatcorp/Dalamud) and
[Heliosphere](https://www.patreon.com/lojewalo) — as well as the
[xivmodarchive patreon](https://www.patreon.com/xivmodarchive), without whom this plugin could not
exist.
