# 言語判定・ルーティング・字幕整列の契約（言語中立）

## 言語判定

判定器は `(text, pair)` を受け取り、ペアに応じた証拠を求める。

| 証拠 | 条件 |
|---|---|
| `Japanese` | ひらがな/カタカナ `U+3040–U+30FF`、CJK 拡張A `U+3400–U+4DBF`、CJK 統合漢字 `U+4E00–U+9FFF` を 1 文字でも含む（`en-es` では無視） |
| `English` / `Spanish` | ペアのラテン側。`ja-en` はラテン 2 語以上を `English`、`ja-es` は同じ条件を `Spanish` とする |
| `AmbiguousLatin` | 日本語文字を含まず、ラテン語が **ちょうど 1 語** |
| `None` | ラテン語 0 語 |

`Detect` の対応はペアごとに異なる。

| pair | Evidence → Detect |
|---|---|
| `ja-en` | `Japanese`→日本語、`English`→英語、それ以外→`Unknown` |
| `ja-es` | `Japanese`→日本語、`Spanish`→スペイン語、それ以外→`Unknown` |
| `en-es` | 下記スコアリングの結果だけを英語 / スペイン語 / `Unknown`（CJK は証拠にしない） |

`AmbiguousLatin` を英語やスペイン語と断定しない（ローマ字発話・固有名詞のため）。
証拠値は `japanese` / `english` / `spanish` / `ambiguousLatin` / `none` で表す。

### 末尾ウィンドウ判定

言語切替検出には**末尾 16 個の Unicode scalar（Unicode code point）**の範囲で `Evidence` を評価する。
単位は Swift の `Character`（grapheme cluster）でも C# の UTF-16 `char` でもない。

手順:

1. 文字列を Unicode scalar 列として末尾から走査する。
2. 空白・改行でない scalar を最大 16 個数える（空白 scalar 自体はカウントしない）。
3. その 16 個の非空白 scalar のあいだ／前後に挟まる空白 scalar は**残したまま**切り出す。
4. 切り出した部分文字列に対して `Evidence` を評価する。

空白を残すのは語境界を保つため（残さないとラテン語が 1 語に潰れ `AmbiguousLatin` に落ちる）。

### `en-es` の判定

`en-es` は末尾 **8 語**を判定窓とする。アクセント文字
`á é í ó ú ü ñ`（大文字を含む）は語構成文字として扱い、`está` を分割しない。
先頭語の直前の `¿` / `¡` は、空白を挟んでも窓に含める（直前のラテン語には踏み込まない）。

- `¿`、`¡`、`ñ`、`Ñ` のいずれかが窓内にあれば即時 `spanish`。
- アクセント母音 `á é í ó ú ü` を含む語は Spanish score に `+2`。
- Spanish 排他語 `el la los las es está que y de del con por para pero más sí` と、
  English 排他語 `the and is are of to it that this with for you they` は該当側に `+1`。
- 共通語（`no a me he son sin un` 等）は採点しない。
- `|esScore - enScore| >= 2` で高い側を確定し、それ未満は `ambiguousLatin`。
- CJK は `en-es` ではノイズとして無視する。

## ルーティング

- 原文音声は常に transcription 接続へ送る。
- 直近 **4 秒（40 フレーム）** の rolling preroll を常時保持する。
- 判定前は source lane のみへ送る。判定後はペア内の相手言語を target とする 1 本だけへ送る。
- 言語切替時は旧 target の pending を破棄し、**新 target へ preroll を flush** する。
- 翻訳送信が 3 連続失敗したら、transport error を 1 回だけ emit し翻訳ポンプを停止する。
- 翻訳送信待ち（pending）は最大 **80 フレーム（8 秒）**。in-flight の 1 フレームと 40 フレームの rolling preroll は数えない。
- 81 フレーム目を enqueue しようとしたら、そのフレームは捨て、pending を全消去し翻訳ポンプを停止し、`error.translationBacklog` を message とする `code = "transport"` の error を該当 target / epoch へ **1 回だけ** emit する。原文 lane は送り続け、セッションは既存の transport 経路で再接続する。
- 停止後は target 変更や routing reset でポンプを再開しない。再開は新しい start のみ。超過停止と 3 連続失敗停止は同じ停止/エラー経路を共有し、1 epoch に transport error が 2 回出ることはない。契約値は `translation-queue.json`。
  セッション側が再接続して epoch を進める（Dual 自体は失敗時点の epoch を維持したまま停止する）。
  成功した翻訳送信は連続失敗カウンタを 0 に戻す。

`en-es` は初回の確定 evidence で即時に target を選択する。以降の切替は、現在言語と
逆の確定 evidence が**連続2回の delta 評価**で継続した場合だけ行う。`ambiguousLatin`、
`none`、同一言語 evidence が挟まった場合はカウンタをリセットする。`ja-en` / `ja-es`
は確定 evidence で即時反転する。

判定に使う原文バッファは上限 `16 * recentEvidenceWindow`（UTF-16）で切り詰める。

- `ja-en` / `ja-es`: 末尾の非空白 scalar 窓。窓内の空白 run が異常に長い場合だけ 1 個の U+0020 へ圧縮する。非空白が無ければ空文字。
- `en-es`: 末尾 8 語窓のあと空白 run を圧縮し、なお上限を超える場合は Unicode scalar 境界で先頭から切る。

## 字幕 lane 選択

原文イベントは `source` lane、翻訳イベントは出力言語 wire 値（`en`、`ja`、`es`）の
translation lane として分離する。一次信号はセッションが設定した**期待 lane ヒント**
（`ExpectLane`）。
同言語 echo（英語入力が `target=en` から英語で戻る等）が起こり得るため、echo だけで lane を確定しない。

1. `expectedLane` があり、その lane が出力済み → その lane を選ぶ。出力前なら**他 lane の first-output では確定しない**。
2. `expectedLane` が無い場合、片側だけが出力していればその lane。
3. それでも決まらなければ原文の文字種で補助判定（日本語→`en` lane、英語→`ja` lane）。

## 字幕整列（Assembler）

| 項目 | 値 |
|---|---|
| idle finalize | 8 秒 |
| 重複除去 | `event_id` の集合で判定 |
| 確定後カットオフ | `elapsed_ms <= finalizedCutoff` の delta は破棄 |
| 原文 delta の受理 | `source` lane のみ |

- 確定直後は `awaitingSourceAfterFinalize` を立て、次の source delta が来るまで訳文 delta を破棄する（保持中の完全ペアを旧 segment の訳文で壊さないため）。
- 言語切替時は完全ペアがあれば確定し、無ければ buffer をクリアして次 segment を待つ。
- epoch が一致しない stream event は無視する。

## 字幕表示

| 項目 | 値 |
|---|---|
| 日本語（CJK を含む）上限 | 60 文字 |
| 英語・スペイン語上限 | 120 文字 |
| 省略記号 | `…`（先頭に付ける） |

上限超過時は末尾 N 文字を残す。英語は先頭の欠け語を落とす（最初の空白までを捨てる）。
trim 後が空文字（空白のみ）なら入力をそのまま返す。

期待値は `shared/fixtures/v1/language.json`、`routing.json`、`subtitle.json` が正本。
