# 2026-10-08 パフォーマンス改善

変更前は c0ae7eb の比較・出力処理。変更後は本書と同じ作業差分です。
判定条件・変更行の選別・並び順・色・文字列型・NULL/空文字/行なし表示は変更しません。

## 測定結果

Windows / .NET 9 / Release。各ケースを1回ウォームアップ後、3回実測した中央値です。

| 処理 | 条件 | 改善前 | 改善後 | 処理中の割り当て量（前→後） |
|---|---|---:|---:|---:|
| 比較＋結果JSON化 | 各DB 30万行・16列・300行更新 | 191.31 ms | 175.93 ms | 0.66 → 0.66 MiB |
| 比較＋結果JSON化 | 各DB 1000行・300列・1000行更新 | 234.76 ms | 157.95 ms | 21.45 → 21.45 MiB |
| Excel用XML生成 | 上記300列の結果、両DB計2000行 | 972.58 ms | 747.62 ms | 393.65 → 232.12 MiB |

割り当て量はGC.GetTotalAllocatedBytesで測定した合計で、ピークメモリ使用量ではありません。
比較時間には検証用JSON化を含みます。DB取得・COUNT・通信・セッション保存・Excelアプリへの貼り付けは含みません。
実行時のPC負荷による時間のばらつきがあり、全件取得が同じ比率で速くなることを示すものではありません。

## 改善内容

- 列順が同じかの判定を、変更行ごとの繰り返しから比較1回につき各スナップショット1回へ移動。
- 変更セルの色付けを、セルごとの変更列配列探索から、再利用する列別の印の参照へ変更。
- 固定のスタイルIDをセルごとに文字列生成しないよう変更。
- XML宣言を完成文字列へ連結せず最初から同じバッファへ書き、完成XML全体の一時コピーを削減。複数テーブルの連結にも適用。

全列比較の別の最適化案は、一部ケースで速度低下が見られたため採用していません。

## 結果を固定する検証

MSTest `MeasureCurrentComparisonAndWideExport` は、比較結果JSONと、出力XMLのSHA-256を記録します。
既存デモ4ケースについてはHTML・テキスト・XMLすべてを照合します。
今回の変更前後で全7項目が完全一致しました。通常のMSTest 31件も成功しています。

```powershell
$env:EVIDENCE_TUNING_REPORT = "$PWD/artifacts/tuning-current.json"
# 変更前の実行時にはBASELINEを指定せず、同じテストでbefore.jsonを保存する。
$env:EVIDENCE_TUNING_BASELINE = "$PWD/artifacts/tuning-before-20261008.json"
dotnet test Tests/Tests.csproj -c Release --filter FullyQualifiedName~MeasureCurrentComparisonAndWideExport
```

測定生データはローカルの `artifacts/tuning-before-20261008.json` と `artifacts/tuning-after-20261008.json` です。

## Windows画面での確認

更新したexeで既存の設定・追跡対象の復元を確認しました。
テーブル絞り込み欄を削除し、一覧内の文字入力・部分一致候補への上下移動・Esc取消・Enter確定・未知の入力の拒否・マウス選択を確認しています。
選択後の検索列がDB定義（ordersのid/status）に対応することも確認しました。
Windows x64自己完結版のビルド成功、配置したexeとビルド成果物のSHA-256一致を確認済みです。
