# Windows EXE 高速再起動・リソース枯渇: 調査と検証

## 報告された症状

- EXEを起動→終了→タスクバーから即再起動するとウィンドウ出現後にフリーズ
- タスクマネージャから終了不能、Windows全体の動作低下とWSL応答停止
- 原因の確定には当該PCのプロセスカウンタとイベントログが必要。コード解析だけではメモリリークと断定できない

## コード上の問題と対策

| 疑い・危険箇所 | 旧実装 | 修正 |
|---|---|---|
| EXE二重起動 | 毎回WPFインスタンス生成 | Windowsユーザーセッション内Mutexで1プロセス化。既存ウィンドウへの表示要求 |
| Shellダイアログ | 起動直後にWindowsフォルダ選択を自動表示 | 自動表示廃止。起動後に手動のCtrl+Oを選択。UNC/WSLのShellハングを起動時に持ち込まない |
| 終了中に残る処理 | フォルダ選択キュー、画像デコード、タイマーを明示停止しない | Closedで停止・キャンセル・画像参照解除。終了時に待機しない |
| デコード負荷 | プレビュー同時2、サムネイル同時3 | それぞれ同時1、合計2以下 |
| 異常な縦横比 | サムネイルの横幅256のみ指定 | 長辺256指定、デコード上限を事前計算 |
| 巨大画像 | ソース画素数、プレビュー画素数に上限なし | ソース80MP/長辺50,000、プレビュー長辺3072/600万画素 |
| WIC画像キャッシュ | URIなしStreamSourceへのIgnoreImageCache指定が画像読み込み失敗を誘発 | `BitmapCreateOptions.None` + `OnLoad` + `Freeze` に修正。実画像デコード統合試験で確認 |
| フォルダ一覧 | UIスレッド上でディスク走査 | Task.Runに移し、キャンセル後の結果を破棄 |
| 毎回のファイル存在確認 | キー操作時に同期File.Existsを繰り返す | 読み込み時に構築した選択状態を参照 |
| OOM継続 | WPF例外を常にhandled扱い | OutOfMemoryExceptionは握りつぶさない |

**注意:** WICコーデックによっては指定した縮小解像度より大きい一時メモリを利用する。上限値は完全なプロセスメモリ保証ではない。またOS全体が固まる場合、GPUドライバ、ファイルシステム、セキュリティソフト、WPFネイティブコード側のハングも疑う。

詳細なレビュー対応状況は [review-followup-status.md](review-followup-status.md) を参照。初期修正版のWPF `IgnoreImageCache` によりすべての画像読み込みが失敗する回帰不具合をWindows CIの実画像試験で再現し、解消した。

## 再現・計測

1. WindowsのRelease版でEXEを再ビルド。別名で上書きせず、旧プロセスが終了していることを確認
2. `powershell -ExecutionPolicy Bypass -File .\scripts\stress-relaunch.ps1 -ExePath .\publish\ImageSelector.exe -Mode Overlap -Iterations 20 -ImageCount 120` を管理者権限不要の通常PowerShellから実行
3. 高負荷の画像を含む500枚の実フォルダでも、少ない回数で慎重に確認。ハング時は強制再試行せず、Windows再起動とログ採取
4. `%LOCALAPPDATA%\ImageSelector\logs\error.log` に異常ログ、Windows「信頼性モニター」(`perfmon /rel`) とイベントビューアー Application / Systemのタイムスタンプを照合
5. タスクマネージャーの**メモリ（プライベートバイト）・コミット済み・CPU・ディスク・GPU専用メモリ・ハンドル**を記録。Windows全体のCommit Limitに接近していないか確認。WSL側は `wsl --status` と `wsl --shutdown` 実行前の動作を記録
6. 再現時にUser-mode dump/WERを取得し、`dotnet-dump` / WinDbgでmanaged heap、ネイティブWIC、COM、待機中スレッドを比較

## 合格基準

- 二重起動で新しいWindow/デコード処理が増殖しない
- 20回の起動→終了→起動が完了（終了10秒以内）。デコード画素数の境界テスト合格
- 反復後もImageSelectorプロセスが残らず、OSが操作可能
- 前半・後半でプライベートバイト、ハンドル、スレッド数が一方向に増え続けない
- 120枚と500枚でフォルダ切り替えと保存・注釈・削除の既存操作が正常

## 制約

GitHub ActionsのWindows runnerには対話型デスクトップがないため、WPFのウィンドウを使った連続起動はCIでは自動検証できない。CIはビルドとデコード予算単体テストを実行。実PCでの連続起動とOS診断は上記スクリプトで別途検証する。
