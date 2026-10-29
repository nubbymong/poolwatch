# PoolWatch artwork

A pair of binoculars keeping an eye on a swimming pool. The name is written PoolWatch.

## The files here

| File | Size (px) | Use it for |
|---|---|---|
| `poolwatch-wordmark-light.png` | 1200 x 320 | The binoculars and the name on an ivory card: a header on a light page |
| `poolwatch-wordmark-dark.png` | 1200 x 320 | The same on a navy card, for a dark page |
| `poolwatch-wordmark-header-light.png`, `poolwatch-wordmark-header-dark.png` | 1200 x 320 | The two cards above without their subtitle ("Windows kernel pool monitor"): the README's header |
| `poolwatch-wordmark-monochrome.png` | 1200 x 320 | The float and the name in navy on a clear background, for a light page where colour does not fit |
| `poolwatch-emblem-512.png` | 512 x 512 | The binoculars alone on a clear background: an avatar or a small mark |
| `poolwatch-splash.png` | 1200 x 700 | A wide banner, such as a link preview or an announcement |

A header that follows the reader's light or dark theme on GitHub, as the README has it:

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/brand/poolwatch-wordmark-header-dark.png">
  <img alt="PoolWatch" src="docs/brand/poolwatch-wordmark-header-light.png" width="440">
</picture>
```

Show the images at their own proportions, and at half their pixel width or less so they stay sharp on high-DPI screens.

## Elsewhere in the repository

- `src/PoolWatch/Assets/poolwatch.ico`: the program's icon, the binoculars from 16 to 256 px (a simpler drawing at the
  smallest sizes), on every PoolWatch window and its taskbar button.
- `src/PoolWatch/Assets/tray-*.ico`: the binoculars in the tray with a dot in the status's colour: `tray-healthy`
  green, `tray-warning` amber and `tray-critical` red; `tray-inactive`, grey binoculars while PoolWatch has no reading
  yet.
- `installer/`: the setup's side image and its small top-corner image, in the sizes of Setup's image areas, so it picks
  the closest one for the display scale: the side image at 202 x 386 and 269 x 515 (100% and 125%) and 328 x 628 (close
  to 150%), the corner image at 58, 77, 97 and 124 px square (100%, 125%, 150% and 200%).
- The side image's line under the name reads "Keeps your PC fast and stable for agentic coding".
- `installer/ready/`: the setup's Ready page icons (its three steps, the arrow between them and the shield), at the
  sizes Setup draws them at 100, 125, 150, 175 and 200% display scale. Setup scales by its font, so these are 30, 40,
  50, 60 and 64 px for a step. Their sources are in `setup-icons/`, drawn at 100%: each PNG is its SVG drawn by
  headless Microsoft Edge at that size on a clear background.

## Colours

| | Hex |
|---|---|
| Navy | `#102A43` |
| Red | `#E9363F` |
| Turquoise | `#00B8D4` |
| Ivory | `#FFF9EF` |
| Tray: healthy, warning and critical dots; inactive | `#2DA44E`, `#D4A72C`, `#CF222E`; `#8C959F` |
