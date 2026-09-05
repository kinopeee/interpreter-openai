# RealtimeTranslator 開発ガイド

OpenAI Realtime Translation によるリアルタイム字幕アプリ。macOS 26以降（Swift / `RealtimeTranslator/`）と Windows（.NET / `windows/`）の2実装があり、翻訳ペアは `ja-en`、`ja-es`、`en-es`。

## 対象別の規約を読む

このファイルの共通規約に、対象プラットフォームの規約を追加して適用する。作業を始める前に下表の文書を読む。起動ディレクトリによる自動読込だけに依存しない。

| 作業対象 | 追加で読む文書 |
| --- | --- |
| `RealtimeTranslator/**`、`project.yml`、Xcode 生成物、macOS の実行・配布スクリプト、macOS workflow | [macOS 規約](RealtimeTranslator/AGENTS.md) |
| `windows/**`、Windows のビルド・配布スクリプト、Windows workflow | [Windows 規約](windows/AGENTS.md) |
| `shared/**`、両実装に関わる仕様・CI・配布 workflow | 上記の両方 |
| 文書のみ | 文書が説明する対象の規約。共通ガイドの変更は上記の両方 |

`project.yml` や `scripts/publish-windows.ps1` のように実装ディレクトリ外にあるファイルも、対象プラットフォームの規約に従う。子の AGENTS.md は共通規約を複製せず、固有の制約を補足する。以下のパスとコマンドはリポジトリルートを基準とする。

## プライバシーと製品の不変条件

- 外部通信先は `api.openai.com/v1/realtime` と `api.openai.com/v1/realtime/translations` に限定する。それ以外の外部翻訳APIは追加しない。
- 利用者自身のOpenAI APIキー（BYOK）をOSの資格情報ストアへ保存する。事業者キーは同梱しない。
- APIキー、Authorization、音声、原文、訳文をログ・status file・アラートへ出さない。
- オプトイン時のみ、確定した原文・訳文ペアをローカル字幕記録ファイルへ保存する（ログ・status・アラートへは出さない）。字幕記録の形式（`原文:` / `訳文:` / `=== 録音開始`）は表示言語に依存せず固定する。
- アプリ枠の表示言語は設定の `uiLanguage`（`system` / `ja` / `en`）。翻訳ペア `languagePair` とは独立で、反映はプロセス再起動後。文言正本は `shared/locales/ui.json`。スレッドカルチャは変更しない。
- 現在の仕様では翻訳音声を再生しない。`session.output_audio.delta` は受信するがデコードしない。

## 原文・翻訳ストリームの契約

- 原文音声は専用transcriptionへ常時送信する。直近4秒のrolling prerollを常時保持し、判定前は原文のみ送信、判定後は選択された `languagePair` の相手言語をtargetとして送る。
- 言語切替を確定する時点と、原文を分割する位置を分離する。原文上の境界候補を追跡し、切替確定時に候補位置で分割する。候補がない場合の扱い、句読点・Unicode・英西の境界規則は `shared/fixtures/v2/subtitle.json` と両実装の `SourceBoundaryTracker` を参照する。切替判定時点を一律に原文境界にしない。
- Translationセッション付属のtranscriptionを原文authorityにしない。専用transcriptionの低遅延deltaを使い、原文と訳文を常にペア表示する。
- 原文送信は翻訳送信と分離し、翻訳側の停滞・失敗に巻き込まない。判定後は選択されたtargetだけが同じ音声frame列を受信し、切替時はrolling prerollを新targetへflushする。
- WebSocket `send` は約5秒でtimeoutし、回復可能なtransport障害として再接続する。
- 原文は `wss://api.openai.com/v1/realtime?intent=transcription` と `gpt-live-transcribe` を使う。`delay` は既定 `low`（設定で `minimal`〜`xhigh` に変更可）、noise reduction は既定 `far_field`。
- 専用エンドポイント `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate` を使う。
- `response.create`、会話turn、tool callは使わない。連続音声ストリームとして扱う。
- WebSocket入力は base64-encoded 24 kHz PCM16 mono little-endian。字幕開始を早めるため100 ms frameを使う。
- 録音中は無音frameも送り続ける。VADで無音を捨てない。
- いずれかの接続が壊れたら全体を再接続する。再接続時は言語判定をリセットする。
- 正常停止はtranscriptionをcommitし、Translation両セッションへ `session.close` を送り、完了イベントを待ってからsocketを閉じる。
- lane選択の一次信号は「セッションが設定した期待laneヒント」とし、補助に原文文字種とfirst-outputを使う。同言語echo（英語入力が `target=en` から英語で戻る等）が発生し得るため、echoだけでlaneを確定しない。
- 原文deltaの末尾ウィンドウの証拠を言語切替とルーティングの信号として使う。日英・日西は文字種、英西は語などの証拠を使い、全ペアを文字種反転として扱わない。判定・ルーティングの契約は `shared/fixtures/v1/language.json` と `shared/fixtures/v1/routing.json` を参照する。
- 受信イベントの上限超過を検知し、欠落した接続世代の未確定字幕を無効化する。欠落後のペアを確定・記録せず、既に確定した字幕は保持する。終了理由・エラー通知を通常イベントの混雑や正常終了通知で失わない。優先順位と容量の正本は `shared/fixtures/v1/receive-queue.json`。送信キューの上限とは区別する。
- 古い接続epochのdeltaは画面へ反映しない。

## 字幕UIの不変条件

- 字幕本文はクリック透過にし、録音操作のUIと分離する。
- スライドを隠す全面黒背景へ戻さない。文字周辺の薄い背景と黒いハローで可読性を確保する。
- 字幕は単一のcurrentスロットのみ。録音中の確定ペアもその場に残し、次発話開始で上書きする（履歴ブロックなし、録音中のタイマー消去なし）。録音停止後は約5秒でcurrentを消す。原文だけを確定しない。
- 更新待ちの旧訳文は`isTranslationCurrent = false`かつ`canFinalize = false`にする。
- 発話途中の原文表示は約160ms間隔に抑え、行高を維持してちらつきを防ぐ。
- パネル高は複数行の1ブロックを収め、ベースラインをクリップしない。

## 共有契約の正本

- `shared/fixtures/v<N>/` は両実装のバージョン付き契約正本。現行の subtitle 契約は v2、その他は v1 とし、Swift テストと Windows 版の同値性を保つ。既存の subtitle v1 も保持し、`scripts/ci-shared-contracts.sh` で全バージョンを検査する。

## 検証の選び方

| 変更内容 | 必要な検証 |
| --- | --- |
| 文書のみ | 記述と実装の整合性、参照先、規約の移設漏れ、`git diff --check`。新規ファイルも確認する |
| Swift / C# のロジック | 対象プラットフォームのビルド・テストと、変更した振る舞いの回帰検証 |
| 共有契約・両実装に関わる仕様 | `./scripts/ci-shared-contracts.sh` と両実装の関連テスト |
| UI・音声経路 | 対象プラットフォームのビルド・全テストに加え、影響する言語ペアの両方向で実際に1文ずつ話して確認 |

複数の区分に該当する場合は、その検証を組み合わせる。文書のみの変更にはアプリのビルド・実行テストを一律に要求しない。

- 最低限、イベントcodec、100 ms packet化、専用原文transcription、原文送信分離とrolling preroll、言語切替セグメント分割、送信timeout、受信欠落とエラー優先順位、字幕lane選択、旧epoch破棄、停止時close drainの回帰検証を維持する。
- 非同期境界、空文字、句読点、停止時finalize、多重起動、秘密情報非漏洩の回帰を優先する。
- 純粋ロジックは macOS で XCTest、Windows で xUnit を使い、各テストに日本語の Given/When/Then コメントを付ける。
- 権限、実API、実マイク、オンライン動作、実際のGUI表示はユニットテストだけでは検証できない。[VALIDATION.md](VALIDATION.md) の対象プラットフォームの項目を使う。
- 実行した検証と未実施の検証を区別して報告する。自動テストの成功を実機確認の代わりとしない。

## CI の責任範囲

- Origin の Depot CI（`.depot/workflows/`）は共有契約と Linux 上の Windows Core を検証する。Windows / macOS のサンドボックスはなく、WPF / Platform を検証済みとしない。Core 検査の正本は `scripts/ci-windows-core.sh`。
- GitHub Actions（`.github/workflows/`）は Windows 全体・macOS `xcodebuild`・タグ Release 用に維持する。Platform / App / publish / 署名・公証はこちらで検証する。
- Depot の初回接続手順は [README.md のテスト節](README.md#テスト) を参照する。
