# 言語判定・ルーティング・字幕整列の契約（言語中立）

## 言語判定

文字種から証拠を求める。

| 証拠 | 条件 |
|---|---|
| `Japanese` | ひらがな/カタカナ `U+3040–U+30FF`、CJK 拡張A `U+3400–U+4DBF`、CJK 統合漢字 `U+4E00–U+9FFF` を 1 文字でも含む |
| `English` | 日本語文字を含まず、ラテン語（`A–Z`/`a–z` の連続を 1 語と数える）が **2 語以上** |
| `AmbiguousLatin` | 日本語文字を含まず、ラテン語が **ちょうど 1 語** |
| `None` | ラテン語 0 語 |

`Detect` は `Japanese`→日本語、`English`→英語、それ以外→`Unknown`。
`AmbiguousLatin` を英語と断定しない（日本語話者のローマ字発話・固有名詞のため）。

### 末尾ウィンドウ判定

言語切替検出には**末尾 16 個の Unicode scalar（Unicode code point）**の範囲で `Evidence` を評価する。
単位は Swift の `Character`（grapheme cluster）でも C# の UTF-16 `char` でもない。

手順:

1. 文字列を Unicode scalar 列として末尾から走査する。
2. 空白・改行でない scalar を最大 16 個数える（空白 scalar 自体はカウントしない）。
3. その 16 個の非空白 scalar のあいだ／前後に挟まる空白 scalar は**残したまま**切り出す。
4. 切り出した部分文字列に対して `Evidence` を評価する。

空白を残すのは語境界を保つため（残さないとラテン語が 1 語に潰れ `AmbiguousLatin` に落ちる）。

## ルーティング

- 原文音声は常に transcription 接続へ送る。
- 直近 **4 秒（40 フレーム）** の rolling preroll を常時保持する。
- 判定前は原文接続のみへ送る。判定後は日本語なら `target=en`、英語なら `target=ja` の 1 本だけへ送る。
- 言語切替時は旧 target の pending を破棄し、**新 target へ preroll を flush** する。
- 翻訳送信が 3 連続失敗したら、transport error を 1 回だけ emit し翻訳ポンプを停止する。
  セッション側が再接続して epoch を進める（Dual 自体は失敗時点の epoch を維持したまま停止する）。
  成功した翻訳送信は連続失敗カウンタを 0 に戻す。

## 字幕 lane 選択

一次信号はセッションが設定した**期待 lane ヒント**（`ExpectLane`）。
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
| 原文 delta の受理 | transcription 接続（`target=en` 側）のみ |

- 確定直後は `awaitingSourceAfterFinalize` を立て、次の原文 delta が来るまで訳文 delta を破棄する（保持中の完全ペアを旧 segment の訳文で壊さないため）。
- 言語切替時は完全ペアがあれば確定し、無ければ buffer をクリアして次 segment を待つ。
- epoch が一致しない stream event は無視する。

## 字幕表示

| 項目 | 値 |
|---|---|
| 日本語（CJK を含む）上限 | 60 文字 |
| 英語上限 | 120 文字 |
| 省略記号 | `…`（先頭に付ける） |

上限超過時は末尾 N 文字を残す。英語は先頭の欠け語を落とす（最初の空白までを捨てる）。
trim 後が空文字（空白のみ）なら入力をそのまま返す。

期待値は `shared/fixtures/v1/language.json`、`routing.json`、`subtitle.json` が正本。
