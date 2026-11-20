# 開発資料の目録

現行の利用方法は[利用者ガイド](../docs/USERS_GUIDE.md)、ソース配置・ビルド・デバッグ起動は[開発者ガイド](../docs/DEVELOPERS_GUIDE.md)、試験手順は[自動テスト](../docs/testing.md)、正式配布は[リリース手順](../docs/release.md)を正本とする。

## 版別資料

- [0.1.15 要求](history/v0.1.15/v0.1.15-req.md)・[計画と報告](history/v0.1.15/v0.1.15-imp.md)・[ケース一覧](history/v0.1.15/test-cases.json)：パスワード欄の読み取り診断で構造、ルート状態、構成変化を区別。

- [0.1.14 要求](history/v0.1.14/v0.1.14-req.md)・[計画と報告](history/v0.1.14/v0.1.14-imp.md)・[ケース一覧](history/v0.1.14/test-cases.json)：無効な背後窓を伴うログイン画面の診断と設定候補。

- [0.1.13 要求](history/v0.1.13/v0.1.13-req.md)・[計画と報告](history/v0.1.13/v0.1.13-imp.md)・[ケース一覧](history/v0.1.13/test-cases.json)：別ウィンドウへの工程引継ぎとパスワード欄の設定候補。

- [0.1.12 要求](history/v0.1.12/v0.1.12-req.md)・[計画と報告](history/v0.1.12/v0.1.12-imp.md)・[ケース一覧](history/v0.1.12/test-cases.json)：パスワード欄だけを保護するローカル証拠。

- [0.1.11 要求](history/v0.1.11/v0.1.11-req.md)・[計画と報告](history/v0.1.11/v0.1.11-imp.md)・[ケース一覧](history/v0.1.11/test-cases.json)：承認済み固定領域の厳密照合。

- [0.1.10 要求](history/v0.1.10/v0.1.10-req.md)・[計画と報告](history/v0.1.10/v0.1.10-imp.md)・[配布版の試験](history/v0.1.10/test-cases.json)・[外部証拠の開発試験](history/v0.1.10/test-cases-evidence-development.json)。配布版の上書き更新と、通常記録と並行する単発取得の開発記録。
- [0.1.9 要求](history/v0.1.9/v0.1.9-req.md)・[計画と報告](history/v0.1.9/v0.1.9-imp.md)・[進捗](history/v0.1.9/v0.1.9-task.md)・[配布版の試験](history/v0.1.9/test-cases.json)・[外部証拠の開発試験](history/v0.1.9/test-cases-evidence-development.json)。配布台帳の互換対応と、WGCによるウィンドウ群取得の開発記録。

- [0.1.8 要求](history/v0.1.8/v0.1.8-req.md)・[計画と報告](history/v0.1.8/v0.1.8-imp.md)・[ケース一覧](history/v0.1.8/test-cases.json)：録画エンジンのリファクタリング
- [0.1.7 要求](history/v0.1.7/v0.1.7-req.md)・[計画と報告](history/v0.1.7/v0.1.7-imp.md)・[進捗](history/v0.1.7/v0.1.7-task.md)：外部証拠API

- [0.1.6 要求](history/v0.1.6/v0.1.6-req.md)・[計画と報告](history/v0.1.6/v0.1.6-imp.md)・[ケース一覧](history/v0.1.6/test-cases.json)
- [0.1.5 要求](history/v0.1.5/v0.1.5-req.md)・[計画と報告](history/v0.1.5/v0.1.5-imp.md)
- その他の版は`history/vX.Y.Z/`に保存する。過去の配置やコマンドを含む記録は当時の証跡として扱う。

## 配置規則

スキル導入の要求と計画・結果は[要求](skill-integration/v0.1.0-req.md)・[計画と報告](skill-integration/v0.1.0-imp.md)を参照する。この文書版はアプリ版番号とは別に管理する。

- 要求・計画・進捗・結果は最初から`history/vX.Y.Z/`に置き、完了後も移動しない。
- 生ログ・動画・実行時生成物は`history/vX.Y.Z/logs/<実行識別子>/`へ保存し、Git対象外とする。
- 版に属さない調査は`research/<対象>/`、継続的に使う仕様・手順は`docs/`に置く。
- 開発用スクリプトは`scripts/`（本ディレクトリー配下）、配布用スクリプトはリポジトリ直下の`scripts/`に置く。
- 独立Gitを維持し、フェーズごとに適切な検証を行って日本語でコミットする。
