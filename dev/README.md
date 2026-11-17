# 開発資料の目録

現行の利用方法は[利用者ガイド](../docs/USERS_GUIDE.md)、ソース配置・ビルド・デバッグ起動は[開発者ガイド](../docs/DEVELOPERS_GUIDE.md)、試験手順は[自動テスト](../docs/testing.md)、正式配布は[リリース手順](../docs/release.md)を正本とする。

## 版別資料

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
