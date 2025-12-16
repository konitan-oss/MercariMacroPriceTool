## 実行手順

1. 事前準備
   - .NET 8 SDK をインストール済みであること。
   - `C:\Program Files\dotnet` がパスに通っていない場合は、PowerShell セッション開始時に `setx /M PATH "C:\Program Files\dotnet;%PATH%"` などで設定する。

2. 初回セットアップ（Playwright ブラウザ取得）
   - ルートで `dotnet restore` を実行。
   - ルートで `dotnet build MercariMacroPriceTool.sln` を実行（初回は playwright.ps1 生成のため必須）。
   - `pwsh ./src/MercariMacroPriceTool.Automation/bin/Debug/net8.0/playwright.ps1 install chrome` を実行し、Chrome 用の Playwright ドライバを取得する。ヘッドレスは禁止設定。

3. 実行（WPF アプリ）
   - ルートで `dotnet run --project src/MercariMacroPriceTool.App/MercariMacroPriceTool.App.csproj` を実行する。
   - 画面に「商品取得」「価格改定を実行」「停止」「開発用: ダミー保存/読込」の4つのボタンが表示されれば起動確認完了。

4. 初回ログイン（storageState 保存）
   - 「商品取得」ボタン押下で Chrome が表示される。
   - ブラウザ内でメルカリに手動ログインする。ログイン完了後、アプリ側の案内ダイアログで OK を押すと `.local/storageState.json` に状態が保存される。

5. 2回目以降の起動
   - `.local/storageState.json` がある場合は自動で読み込まれ、ログイン状態を維持したまま Chrome を起動する。

6. 商品取得の使い方
   - StartRow/EndRow に 1 始まりの行範囲を指定（デフォルト 1〜500）。この範囲で listings のカードを読み込み、公開停止中でないものだけを表に表示する。
   - 検索文字でタイトルをフィルタ（大文字小文字区別なし、スペース区切り AND）。入力後「フィルタ適用」で反映する。
   - 取得が遅い場合は EndRow を小さくする、StartRow/EndRow を絞るなどしてスクロール量を減らす。
   - 「停止」ボタンで実行中の取得をキャンセルできる。

7. 価格改定サイクルの使い方（チェックした複数を上から順に処理）
   - DataGrid で対象商品にチェックを入れる（複数可、上から順に処理）。
   - RatePercent（値下げ率, デフォ10）、DailyDownYen（毎日値下げ, デフォ100）、待機①/②（秒, デフォ15/250）、商品間待機（秒, デフォ2）、リトライ回数（デフォ2）、リトライ待機秒（デフォ2）を必要に応じて変更する。
   - 「価格改定を実行」を押下すると、チェックされた行を順に、値下げ→一時停止→待機①→元の価格へ戻す→再開→待機② まで実行する。各操作はリトライ付きで実行される。
   - 実行ログは画面下部のログ欄に表示され、`.local/logs/YYYYMMDD-HHMM_price-run.csv` に1行1商品で保存される（ItemId, Title, Url, BasePrice, NewPrice, 結果, メッセージ, 実行時刻, Step, RetryUsed, EvidencePath）。
   - 停止ボタンで途中キャンセル可能。キャンセルすると処理中の1件は「キャンセル」となり、未処理の行はそのまま残る。
   - 完走した場合のみ RunCount/LastRunDate が更新される。同一日に2回実行しても RunCount は増えない（本日完走済みはスキップ）。

8. DB 保存場所と初期化
   - SQLite DB は `.local/app.db` に作成される（`.local` は自動生成）。
   - Items テーブル定義: ItemId TEXT PK, ItemUrl TEXT NOT NULL, Title TEXT, BasePrice INTEGER NOT NULL, RunCount INTEGER NOT NULL, LastRunDate TEXT, UpdatedAt TEXT。
   - 初回起動時や「開発用: ダミー保存/読込」ボタン押下で DB とテーブルが自動作成されるため手動作成は不要。

9. 途中再開（runstate.json）
   - 実行中に `.local/runstate.json` が更新される（SessionId, 対象件数, 現在インデックス, 各商品の状態）。
   - 中断後に再起動して同じ商品をチェックして実行すると、runstate が未完了の場合「未実行」の商品だけ処理される（過去の結果は表に反映）。

10. 設定保存（settings.json）
   - `.local/settings.json` に StartRow/EndRow/検索文字、価格改定・待機・リトライ設定が保存される（終了時自動保存、設定保存ボタンでも保存可）。
   - 設定ファイルが壊れた場合は削除して再起動すれば初期値に戻る（読み込み失敗時はデフォルトで起動し、ログに理由が出る）。

11. 停止
   - アプリを閉じるか、ターミナルで Ctrl+C を送る。

## トラブルシュート
- dotnet コマンドが見つからない場合: PowerShell を再起動し PATH を確認する。
- Playwright のダウンロードに失敗する場合: ネットワーク接続を確認し、`playwright.ps1 install chrome` を再実行する。
- ビルドに失敗する場合: 依存パッケージの復元を `dotnet restore` で試行し、エラー内容を確認する。
- 失敗時の証拠: `.local/logs/evidence/` にステップごとのスクリーンショット(PNG)とHTMLが保存される。CSVの EvidencePath で場所を確認。

## �|�b�v�A�b�v�Ή��i�����Ȃ��ꍇ�̎b��菇�j
- ���i�y�[�W�ɃI�[�N�V�����ē�Ȃǂ̃|�b�v�A�b�v���o�ăN���b�N��Ղ�ꍇ�A�c�[���͎����Łw����x���� Escape ����݂�B
- ����ł�����Ȃ��ꍇ�́A�ŏ���1�񂾂��蓮�Ń|�b�v�A�b�v����Ă���w���i�������s�x���������B

## ���i����t���[�̒��ӓ_�iHOTFIX-05�j
- �ҏW��ʂł́w�ύX����/�X�V����x�͉������A���i���͌�Ɂw�o�i��ꎞ��~����x�ŕۑ��m�肵�A�ĊJ����w�o�i��ĊJ����x�Ŋm�肷��t���[�ɂ��Ă��܂��B
- �|�b�v�A�b�v�ŎՂ���ꍇ�͎����ŕ��܂����A�����Ȃ��ꍇ�͈�x�蓮�Ł~����Ă���Ď��s���Ă��������B

## 価格確定フローの補足（HOTFIX-05/06）
- 編集画面では「変更する/更新する」ボタンは押さず、価格入力後に「出品を一時停止する」で保存確定し、再開側も「出品を再開する」で保存確定します。
- ポップアップで遮られる場合は自動で閉じますが、閉じられない場合は一度手動で閉じてから再実行してください。
- 停止ボタンでキャンセルした場合でも `.local/logs/evidence/` に Canceled_* の PNG/HTML が残ります。
