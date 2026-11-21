# Brand assets

The logo is a repayment schedule drawn as a ring of six instalments: three paid, one part-paid. The check mark at
the centre stands for every payment being accounted for.

| File | Use |
|---|---|
| `logo.svg` | The source: use it wherever SVG works |
| `logo-256.png` | The README header (an absolute URL, so it also shows on nuget.org) |
| `icon.png` | The NuGet package icon (128 × 128) |
| `social-preview.svg`, `social-preview.png` | GitHub's social preview (1280 × 640; upload it under Settings → General → Social preview) |

Colours: background `#0F3D3E`, paid `#2DD4BF`, unpaid `#1E5A5B`, accent `#FBBF24`, text `#F8FAFC`. They stay clear
of any payment provider's brand colours.

The PNGs are rendered from the SVGs with headless Chrome, for example:

```sh
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --hide-scrollbars \
  --default-background-color=00000000 --window-size=1280,640 --screenshot=social-preview.png social-preview.svg
```
