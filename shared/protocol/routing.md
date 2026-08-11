# 言語判定・ルーティング・字幕整列の契約（言語中立）

## 言語ペア

言語ペアは `ja-en`（既定）、`ja-es`、`en-es` のいずれかである。ペアの宣言順を
transcription の `languages` にそのまま渡し、翻訳接続はペア内の2言語を target とする。
話者言語からの翻訳 target は、ペア内のもう一方の出力言語である。

## 言語判定

判定器は `(text, pair)` を受け取る純関数で、証拠値は次のいずれかを返す。

`japanese` / `english` / `spanish` / `ambiguousLatin` / `none`

### `ja-en` / `ja-es`

これらは CJK を含むペアであり、末尾 **16 個の Unicode scalar**（Unicode code point）
を判定窓とする。

- ひらがな・カタカナ・CJK 拡張 A・CJK 統合漢字を1文字でも含めば `japanese`。
- CJK を含まず、ラテン語がちょうど1語なら `ambiguousLatin`。
- CJK を含まず、ラテン語が2語以上なら、ペアの非日本語側を確定する。
  - `ja-en`: `english`
  - `ja-es`: `spanish`
- 数字、句読点、空白だけなら `none`。
- 初回の `ambiguousLatin` 特例は ja-* ペアでのみ許可し、現行の ja-en 挙動を維持する。

### `en-es`

`en-es` は末尾 **8語**を判定窓とする。アクセント文字
`á é í ó ú ü ñ`（大文字を含む）は語構成文字として扱い、`está` を分割しない。

- 窓内に `¿`、`¡`、`ñ`、`Ñ` のいずれかがあれば即時 `spanish`。
- アクセント母音 `á é í ó ú ü`（大文字を含む）を含む語は Spanish score に `+2`。
- 排他的機能語は該当側の score に `+1`。
  - Spanish: `el la los las es está que y de del con por para pero más sí`
  - English: `the and is are of to it that this with for you they`
- 両言語に存在する語（`no a me he son sin un` 等）は採点しない。
- `|esScore - enScore| >= 2` のとき高い側を確定し、それ未満は `ambiguousLatin`。
- CJK は `en-es` ではノイズとして無視し、score に影響させない。

### 末尾ウィンドウ判定

`ja-*` の判定窓は末尾 16 個の Unicode scalar、`en-es` の判定窓は末尾 8 語である。
空白は語境界を保つため、判定窓を切り出す際に残す。

## ルーティング

- 原文音声は常に source transcription 接続へ送る。
- 直近 **4 秒（40 フレーム）**の rolling preroll を常時保持する。
- 判定前は source lane のみへ送る。判定後はペア内の選択された translation target
  1本だけへ送る。
- 言語切替時は旧 target の pending を破棄し、新 target へ preroll を flush する。
- 翻訳送信が3連続失敗したら、transport error を1回だけ emit し翻訳ポンプを停止する。
  セッション側が再接続して epoch を進める（Dual 自体は失敗時点の epoch を維持したまま停止する）。
- 成功した翻訳送信は連続失敗カウンタを0へ戻す。

## en-es ヒステリシス

`en-es` の初回判定は確定 evidence で即時に target を選択する。以降の切替は、現在の
言語と逆の確定 evidence が**連続2回の delta 評価**で継続した場合のみ行う。
`ambiguousLatin`、`none`、または現在と同じ言語の evidence が1回でも挟まった場合は
切替カウンタをリセットする。`ja-en` / `ja-es` は確定 evidence で即時反転する。

## source lane と字幕 lane

原文イベントは translation target と同じ値で表さず、`source` lane として明示する。
translation event は `translation` lane と出力言語 wire 値（`en`、`ja`、`es`）を持つ。
source transcription は常に `source` lane でイベントを流し、assembler は lane が
`source` の原文だけを authority として受理する。

字幕の翻訳 buffer は出力言語 wire 値をキーとする lane map で保持する。

1. `expectedLane` があり、その lane が出力済みならその lane を選ぶ。
2. `expectedLane` があり、その lane が未出力なら他 lane の first-output では確定しない。
3. `expectedLane` が無く、片側だけ出力していればその lane を選ぶ。
4. それでも決まらなければ原文文字種を補助信号とする。

epoch が一致しない stream event は無視する。

## 字幕整列

| 項目 | 値 |
|---|---|
| idle finalize | 8 秒 |
| 重複除去 | `event_id` の集合で判定 |
| 確定後カットオフ | `elapsed_ms <= finalizedCutoff` の delta は破棄 |
| 原文 delta の受理 | `source` lane のみ |

確定直後は `awaitingSourceAfterFinalize` を立て、次の source delta が来るまで
translation delta を破棄する。言語切替時は完全ペアがあれば確定し、無ければ buffer を
クリアして次 segment を待つ。

## 字幕表示

| 項目 | 値 |
|---|---|
| 日本語（CJK を含む）上限 | 60 文字 |
| 英語・スペイン語上限 | 120 文字 |
| 省略記号 | `…`（先頭に付ける） |

期待値は `shared/fixtures/v1/language.json`、`routing.json`、`subtitle.json` が正本である。
