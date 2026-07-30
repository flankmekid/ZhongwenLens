# Third-party notices

Zhongwen Lens is built on third-party data and models. None of it is committed to this
repository; `scripts/fetch-data.ps1` and `scripts/fetch-models.ps1` retrieve it from the
sources below.

**If you distribute a build of this app, the CC-CEDICT terms are a real obligation, not a
formality.** See the share-alike note below.

---

## CC-CEDICT — dictionary definitions

- **Source:** <https://www.mdbg.net/chinese/dictionary?page=cc-cedict>
- **License:** Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0)
- **Used for:** headwords, traditional/simplified forms, numbered pinyin readings, and
  the sense glosses shown in the result window. Roughly 124,700 entries.

CC BY-SA 4.0 requires that attribution be preserved and that derivative versions of the
*data* be shared under the same license. The generated `dictionary.db` is such a
derivative. In practice, for a distributed build:

1. Credit CC-CEDICT visibly in the app (the about box does this).
2. Ship this notice file.
3. Offer the `dictionary.db` (or the build script that produces it) under CC BY-SA 4.0.

The application *code* is unaffected — only the dictionary data carries share-alike.

## jieba — word frequency table

- **Source:** <https://github.com/fxsjy/jieba> (`jieba/dict.txt`)
- **License:** MIT
- **Used for:** unigram frequency priors that drive the segmenter's Viterbi scoring
  (DESIGN.md §3.3). Only the counts are used; none of jieba's code is included.

## complete-hsk-vocabulary — HSK levels

- **Source:** <https://github.com/drkameleon/complete-hsk-vocabulary>
- **License:** MIT
- **Used for:** HSK band tags (both HSK 2.0 and HSK 3.0 schemes) and character radicals.

## PaddleOCR PP-OCRv4 — OCR models

- **Source:** <https://github.com/PaddlePaddle/PaddleOCR>, ONNX exports via
  <https://huggingface.co/SWHL/RapidOCR>
- **License:** Apache License 2.0
- **Used for:** text detection (`det.onnx`), text-line orientation (`cls.onnx`), and text
  recognition (`rec.onnx`), plus the `ppocr_keys_v1.txt` character dictionary used to
  decode recogniser output.

Apache 2.0 requires the license text and attribution be retained on redistribution of the
model files.

## Vortice.Windows

- **Source:** <https://github.com/amerkoleci/Vortice.Windows>
- **License:** MIT
- **Used for:** managed Direct3D 11 and DXGI bindings behind `DesktopDuplicationCapture`,
  which reads the composited desktop from the GPU so games and hardware-decoded video capture
  correctly.

## Microsoft.ML.OnnxRuntime

- **Source:** <https://github.com/microsoft/onnxruntime>
- **License:** MIT

## Windows App SDK / WinUI 3

- **Source:** <https://github.com/microsoft/WindowsAppSDK>
- **License:** MIT
