## listings ページ判定用セレクタ候補
### PausedTextCandidates
- 公開停止中
- 出品を再開する
- 停止中

## URLs
- ListingsUrl: https://jp.mercari.com/mypage/listings
- HomeUrl: https://jp.mercari.com/

## 商品編集・価格改定用セレクタ候補
### EditButtonSelectors
- a[href^="/sell/edit/"]
- a:has-text("商品の編集")
- button:has-text("商品の編集")
- [data-testid="edit-button"]

### PriceInputSelectors
- input[name="price"]
- input[data-testid*="price"]
- input[type="number"]

### SaveButtonSelectors
- button:has-text("更新する")
- button:has-text("保存")
- button[data-testid="save-button"]

### PauseSelectors（停止確定用・Saveなしフロー）
- button:has-text("出品を一時停止")
- a:has-text("出品を一時停止")
- [role="button"]:has-text("出品を一時停止")
- [data-testid*="pause"]

### ResumeSelectors（再開確定用・Saveなしフロー）
- button:has-text("出品を再開")
- a:has-text("出品を再開")
- [role="button"]:has-text("出品を再開")
- [data-testid*="resume"]

### PopupCloseSelectors
- button[aria-label="閉じる"]
- [data-testid="modal-close"]
- [data-testid="close"]
- button:has-text("閉じる")
- button:has-text("×")

## 備考
- DOMの変化に応じて実際の要素を確認し、必要に応じてこのファイルを更新する。
- コードにセレクタを散らさず、このファイルに集約する方針を取る。
