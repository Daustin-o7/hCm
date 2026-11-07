# English/Hindi prescription PDF fonts

These unmodified fonts are embedded in `Hospital_CRM.Api.dll`. Skia subsets them
into prescription PDFs; server and reader font installation is not required.
Keep this notice and `OFL.txt` with application distributions (the project copies
both into build and publish output).

## Provenance

Downloaded from the Noto Fonts repository on 2026-11-07:
`https://raw.githubusercontent.com/notofonts/noto-fonts/main/hinted/ttf/`.
The source path below is relative to that URL. Versions are from each TTF's name
table; SHA-256 identifies the exact bundled bytes even if upstream `main` changes.

| Source path | Version | SHA-256 |
| --- | --- | --- |
| `NotoSans/NotoSans-Regular.ttf` | 2.008 | `b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5` |
| `NotoSans/NotoSans-Bold.ttf` | 2.008 | `c976e4b1b99edc88775377fcc21692ca4bfa46b6d6ca6522bfda505b28ff9d6a` |
| `NotoSansDevanagari/NotoSansDevanagari-Regular.ttf` | 2.002 | `385e78e6359a9d88a0f243d53b1209d7548361ba2194e2b9ec779bcaa7e8949d` |

Noto Sans: Copyright 2015-2021 Google LLC. All Rights Reserved.
Noto Sans Devanagari: Copyright 2015-2022 Google Inc. All Rights Reserved.
All three fonts are licensed under SIL Open Font License 1.1, which permits
application bundling and document embedding. The unmodified repository license
is `https://raw.githubusercontent.com/notofonts/noto-fonts/main/LICENSE`, saved as
`OFL.txt` (SHA-256 `0dab92d0544f7b233403f14b84a663bdbfa746982eda629e7f4f9ffe1b036feb`).

## Rendering and coverage

`Topten.RichTextKit` 0.6.170 (Apache-2.0) supplies measured line layout and
Hindi conjunct/vowel shaping through SkiaSharp/HarfBuzz. The resolved SkiaSharp
packages are 4.153.1 and HarfBuzzSharp packages are 14.2.1.301 (MIT package
licenses). This dependency provides real shaping and embedding rather than
character substitution or a custom font engine.

The user-selected first-clinic languages are **English/Latin-script names and
Hindi/Devanagari only**. Other languages are future scope. Synthetic tests exercise
Latin accents (including decomposed accents), the rupee sign, Hindi conjuncts,
pre-base vowel positioning, joiners, and Hindi instructions with Latin doses.
Physical-glyph tests verify conjunct substitution rather than only extracted text.

The renderer limits input to its supported Latin ranges, combining accents,
Devanagari, shared punctuation/currency, and permitted whitespace/joiners, then
checks actual bundled glyph coverage. Latin ranges are U+0000–024F and U+1E00–1EFF;
combining accents are U+0300–036F, Devanagari U+0900–097F, punctuation U+2000–206F,
and currency U+20A0–20CF. Unsupported control/format characters still fail within
those ranges. Glyph coverage is required even within a permitted range.

Noto Sans includes some glyphs for other scripts; the explicit range check keeps
them outside the first-clinic scope. Arabic/Urdu, Greek, Cyrillic, Hebrew, Tamil,
Telugu, Kannada, Malayalam, Bengali, Gujarati, Punjabi, Odia, Chinese, and emoji
samples are tested as unsupported. Missing glyphs, malformed UTF-16, unsupported
controls, and OS font fallback throw `EncoderFallbackException`; the endpoint
returns 422 before creating a print log. No clinical text is transliterated or
silently replaced. This is script/glyph validation, not automatic language
identification or a claim of generic Unicode support.

PDF text runs include `/ActualText` to retain logical Unicode text for Indic
ligatures and reordered vowel signs. Accurate extraction requires a reader that honors it:
tests use PdfPig 0.1.16 with `ParsingOptions.UseActualText = true`. Readers that
ignore `/ActualText` can extract incomplete or reordered Hindi text despite correct
visual glyphs. Selection can operate at run rather than character granularity.
This output has not been certified as PDF/UA or PDF/A.

## Deployment requirement

RichTextKit brings in `SkiaSharp.NativeAssets.Linux` and
`HarfBuzzSharp.NativeAssets.Linux`. The standard Skia Linux asset requires
`libfontconfig.so.1`, even when application fonts are bundled. Debian/Ubuntu
runtime images must install `libfontconfig1`; retain the native libraries in
publish output. See the [SkiaSharp package guide](https://github.com/mono/SkiaSharp/blob/main/documentation/dev/packages.md).

The runtime stage of `backend/Hospital_CRM.Api/Dockerfile` must include
`libfontconfig1` in its existing `apt-get install -y --no-install-recommends curl`
command. Rebuild and smoke-test prescription generation inside the resulting
Linux image. Windows rendering has been tested; Linux execution, physical
printing, and clinic sign-off on the rendered samples remain unverified. The
English/Hindi language selection itself is confirmed.
