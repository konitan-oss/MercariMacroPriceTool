# MercariMacroPriceTool 配布手順 (HOTFIX-16)

## 事前準備
- このリポジトリを取得し、.NET 8 SDK が入っていることを確認。
- ms-playwright フォルダは git には含めない（publish で同梱する）。

## 配布用ビルド手順（自動）
1. PowerShell でリポジトリルートに移動
2. `tools\publish.ps1` を実行
   - Release ビルド
   - PLAYWRIGHT_BROWSERS_PATH を src/MercariMacroPriceTool.App/ms-playwright に設定
   - playwright.ps1 で Chromium をインストール（ビルド出力を利用）
   - self-contained / SingleFile で publish
   - publish フォルダに ms-playwright が含まれることを検証
3. 完成した `src/MercariMacroPriceTool.App/bin/Release/net8.0-windows/win-x64/publish` を丸ごと zip にする

## 配布先での使い方
- zip を展開し、`MercariMacroPriceTool.App.exe` を実行するだけ
- 環境変数 PLAYWRIGHT_BROWSERS_PATH を設定する必要なし（アプリが自動設定）

## 動作確認
- 初回起動でエラーが出ないこと
- 「商品取得」「価格改定」が動作すること
- ms-playwright を消した状態で起動すると、エラーメッセージが出て安全に終了すること

## トラブルシュート
- publish フォルダに ms-playwright が無い場合は、tools/publish.ps1 を再実行
- Playwright 初期化エラー表示時は、zip を丸ごと解凍して exe と同じ場所に ms-playwright があるか確認
