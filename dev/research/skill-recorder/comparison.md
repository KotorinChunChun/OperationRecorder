# Skill RecorderとOperationRecorderの比較

2026年11月17日（JST）。比較対象は、このPCに導入された Microsoft Skill Recorder v0.9.2（`63bbb50`）と、OperationRecorderのローカルソース v0.1.6（`96354d6`）。インストールを行った[既存チャット](codex://threads/01a1e4d9-20f1-7460-9b93-e8c7382bc0a5)、両方のソースと仕様書、既存の試験記録を照合した。今回の比較では両アプリで同じ操作を録画する試験、AI生成結果の評価、性能測定は行っていない。[Skill Recorder v0.9.2のリリース記録](https://github.com/microsoft/skill-recorder/releases/tag/v0.9.2)

## 判定

**OperationRecorderを維持し、Skill Recorderの「操作を目的別に整理する」「根拠を見ながら直す」「確認した手順をスキルにする」の3点を取り込む。** 既存のWindows操作記録や手順書出力から得られる証跡を、AIが使える手順の入力にできる。まずは、保存済みの記録を開いて分析・編集し、別ファイルとして書き出す機能を追加する価値が高い。

製品の中心を「macOSも含めて作業を記録し、主にAIエージェント用のスキルを作る」に変更する場合は、Skill Recorderをベースにした別アプリを検討する。その場合は、配布条件と、既存のOperationRecorderの記録をどこまで移行するかを先に決める。

## 実装・成果物の比較

| 観点 | OperationRecorder | Skill Recorder v0.9.2 |
| --- | --- | --- |
| 記録と根拠 | クリック、ドラッグ、キー操作などに、対象ウィンドウ、取得できたUI Automationの要素ID、座標、操作前後のPNGをひも付ける。ウィンドウ・アプリ・画面など撮影範囲を選べる。 | 画面動画・選んだフレーム、アプリやウィンドウの切り替え、URL、クリップボードの短い抜粋、専用ターミナルなどを時系列に並べ、AIが操作の目的を推定する。WindowsのURLはブラウザーのアドレス欄から取得する表示値。 |
| 編集 | 手順単位で見出し・本文を修正し、複数の記録を並べ替え・除外して合成できる。元記録を残して保存・再開できる。 | 推定した作業目的と操作列を見直し、手順を追加・削除・並べ替えできる。確認した計画から成果物を生成する。 |
| 成果物 | 画像付きのMarkdown・HTML・PDF・DOCX。テンプレートによる書式変更、CLIによる再出力、操作再現用の設定JSONに対応する。 | 再利用できる `SKILL.md` と、エージェントが予定や条件に応じて実行する自動化の設定。スキル本文を確認し、確認した版を書き出したり追加したりできる。実装上の既定の追加先はCopilot側。 |
| 実行の考え方 | 登録したウィンドウ、UI要素、画像条件などが揃うまで待ち、条件が揃わなければ理由を示して停止する。複数記録を根拠に、Codex CLIで候補条件を生成できる。 | 1回の実演から同種の作業に使える手順を作り、実行時のエージェントにCLIやAPIなど対象に合った手段を選ばせる。 |
| 音声・動画 | 任意のMP4・WAV、録音中の区間ごとの文字起こし、保存後の手順単位の再認識。TranscriptionServerとの連携はこのPCの実サーバーで確認済み。 | ナレーションの文字起こしはローカルWhisper。MP4・WebMの取込から、映像・音声・補足説明をもとに手順案を作れる。動画から取得できなかったUI要素IDや操作ログは復元できない。 |
| 分析時の情報管理 | パスワード欄の入力値は記録対象外。選択した文字起こし方式に応じて音声を送信する。新たにAI分析を加える場合は、送信する画像や本文の扱いを決める必要がある。 | 記録はローカル。分析の開始時に選択したAI先へ映像の一部、活動ログ、文字起こしなどを送る。保護有効時にローカルの秘密情報検出と画像のぼかしを行う。 |
| 実行環境と配布 | WindowsのC#／.NETアプリ。KotorinToysとの起動・管理連携と独立配布がある。 | Electron／TypeScript・React。macOSが主対象で、Windows 11 x64・ARM64にも対応。v0.9.2はソース配布で、指定コミットを各PCでビルドする。 |

OperationRecorderの根拠は[利用者ガイド](../../../docs/USERS_GUIDE.md)、[リプレイ仕様](../../../docs/replay.md)、[複数記録からの再生条件作成](../../../docs/replay-learning.md)、[TranscriptionServer実機記録](../../history/v0.1.6/v0.1.6-imp.md)。Skill Recorderの根拠は[機能説明](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/README.md)、[Windowsで取得する情報](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/docs/windows-capture.md)、[スキル生成と保存先](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/electron/skillbuilder/builder.ts)。

### Windows版をそのまま土台にする場合の差

Skill RecorderにはWindows上でアプリやウィンドウ、URL、画面を取得する実装がある。一方、同プロジェクトのWindows仕様では、フォーカス・呼び出し・値変更などの意味を持つUIAイベントを取得する機能は未実装とされている。OperationRecorderの操作単位の撮影、対象UIの情報、画像照合で止まる再生をそろえるには、Windowsの記録・再生処理を別途移植する必要がある。さらに記録フォルダー、編集・合成、テンプレート、KotorinToysとの連携を引き継ぐ作業が残る。[Skill RecorderのWindows収録仕様](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/docs/windows-capture.md)・[OperationRecorderの開発者ガイド](../../../docs/DEVELOPERS_GUIDE.md)

両プロジェクトのルートにMITライセンスがある。Skill Recorderの公式リリースはソース配布で、そのインストーラーによるローカルビルドは再配布用と区別されている。フォークを配布する場合は、Copilot CLIを含む依存物や同梱する素材の条件と、上流の配布用手順を別途確認する。機能と設計の取り込みであれば、OperationRecorderのパッケージにElectronやCopilotの配布物を追加する必要はない。[Skill Recorderのライセンス](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/LICENSE)・[配布と依存物の扱い](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/INSTALL.md)・[第三者コンポーネント](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/THIRD-PARTY-NOTICES.md)

## 取り込む優先順

| 優先度 | 機能 | 取り込み方 |
| --- | --- | --- |
| 1 | 記録から目的と手順の案を作り、根拠を見ながら編集する | `events.jsonl` の操作番号、前後画像、承認した説明や文字起こしを使う。例として「設定を変更する」を、前提、設定値、操作、保存後の確認に整理する。どの記録・手順を根拠にしたかを案の各項目から参照できるようにする。案と原記録は別々に保存し、修正を反映したときだけ既存の手順書に適用する。 |
| 1 | AIに送る範囲を事前に確認する | 分析対象の手順、画像、文字起こし、送信先を一覧で確認する。除外や手動マスクを先に対応させ、後からローカルOCRと秘密情報検出を足す。Skill Recorderは保護中のOCR失敗時に画像を保留するが、テキストのスキャン失敗時は未マスクのテキストで分析が進む経路がある。こちらは保護処理に失敗した対象の送信を保留する設計を提案する。 |
| 2 | 確認済みの案をCodex向け `SKILL.md` に出力する | 目的、使用条件、可変項目、手順、完了条件を含める。入力した個別の値を変数に分け、安定した公式CLI・APIが確認できる操作では、その手段も案に載せる。UI操作が必要な箇所は既存のリプレイ設定を参照できるようにする。最初は本文プレビューとフォルダーへの書き出しまで。 |
| 2 | 手順と録画の位置を行き来する | 操作時刻、前後PNG、録画の区間、発言を同じ画面から見られるようにする。先に既存のMP4・WAVを使う。動画だけの分析を追加する前に、CapchunScreenのログ付き録画をChulipVideoからOperationRecorder形式で出力できる既存経路と役割を整理する。 |
| 3 | URLと専用ターミナルの記録 | URLは対象ブラウザーで手順を作る用途に限定して検討し、クエリーなどの記録範囲を先に決める。端末作業が主要用途になったら、アプリが開いた端末のコマンド・終了コードから始める。クリップボード本文の常時記録、全端末の監視、自動化の即時登録は初期対象から外す。 |

送信前処理の根拠は[Skill Recorderの分析時の処理](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/electron/ipc.ts)と[画像送信ツール](https://github.com/microsoft/skill-recorder/blob/63bbb50111713b02f3bc0ebbd5f354f4eb038f99/electron/describer/tools.ts)。動画連携は[ChulipVideoの利用ガイド](../../../../ChulipVideo/docs/guide.md)を参照。

## 最初の試作で判断すること

保存済み記録を入力に、`目的と前提 → 根拠付きの手順案 → 確認用のMarkdown` を出す小さな試作で判断する。まずテスト用の3例として、設定画面の変更、ウィンドウを切り替える作業、複数記録を合成した作業を使う。手順の抜け、推定で追加した操作、元記録との対応、利用者が修正した箇所を比較する。スキル生成に進んだら、値やフォルダー名を変えた未使用の例でも手順が成り立つか確認する。

既存のHTML出力と分析後のHTML出力を同じ記録で比較し、元の記録と手修正が保持されること、未承認の画像やテキストが送られないことを確認する。初回は操作の自動実行を行わず、実行が必要な案件で既存のリプレイに渡す手順を別に検証できる。試作の結果、用途の中心がスキル作成に移り、Windows以外の対応にも具体的な需要がある場合にフォーク案を再検討する。
