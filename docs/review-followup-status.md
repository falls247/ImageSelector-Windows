# ImageSelector 高速再起動レビュー対応状況

対象: ユーザーレビュー `ImageSelector-Windows 高速再起動クラッシュ対策レビュー & 追加修正SOW.md` の WI-01〜WI-13。

## 実際の画像表示不能: 再現原因と対処

従来CIは純粋なデコード寸法計算しか実行していなかった。

Windows GitHub Actions上で `ImageLoader.PreviewAsync` / `ThumbnailAsync` に本物のPNG/JPEGを渡す統合テストを追加した結果、次の実行時例外を確認:

```text
System.ArgumentNullException: Value cannot be null. (Parameter 'key')
System.Windows.Media.Imaging.ImagingCache.RemoveFromCache(Uri uri, Hashtable table)
System.Windows.Media.Imaging.BitmapImage.FinalizeCreation()
```

原因: `BitmapImage` の `StreamSource` と `BitmapCreateOptions.IgnoreImageCache` の併用。URI未設定のストリーム画像でもURIキャッシュ削除を試みて失敗した。

修正: `BitmapCreateOptions.None` とし、`BitmapCacheOption.OnLoad` + `Freeze` は維持。WIC実画像デコード統合テストは修正後に成功した。

## SOW対応一覧

| WI | 対応 | 残る検証・制約 |
|---|---|---|
| WI-01 | Overlap/Sequential、200ms応答監視、ピークメモリ/ハンドル/システムメモリ測定、乱数画素の負荷フィクスチャを追加 | Windowsデスクトップ上の20回実測、および16bit RGB/インターレースPNG等のハーネス生成は未実施 |
| WI-02 | 所有MutexをWPF起動前に取得し、終了時はプロセス終了まで保持。ready/activateイベント、10秒+5秒待機、PID付き通知、終了遅延環境変数 | 旧プロセスの終了メッセージと新規起動が完全に競合する実機での時系列検証が必要 |
| WI-03 | StartupUri解除、明示Main/StartupObject、所有確定後にWindowを構築 | 多重起動時のピークメモリ30MB目標は未測定 |
| WI-04 | 注釈JSON読込非同期化、選択状態キャッシュ、保存/削除/ごみ箱移動を非UIスレッド化、選択済み件数をメモリ集計 | Debug UI thread I/O ラッパー全経路のログ0件確認は未完 |
| WI-05 | 保存中のClosingキャンセル/終了延期（10秒後の強制クローズ）、コピーキャンセル時のロールバック、24時間超の作業一時ファイル回収 | 非応答ストレージで強制終了した場合の一時ファイル即時削除は保証不可 |
| WI-06 | Unloadedキャンセル、可視要素保護のLRUキャッシュ、再表示時の再要求 | 500枚高速スクロールでの件数・表示品質は実機未計測 |
| WI-07 | WIC用ストリームのオープンを1回に変更、256MB上限、UNC/ネットワークは非同期読み込みを先行。10秒タイムアウトで同じリモートフォルダの新規デコードを抑止 | ネットワーク共有のカーネルI/O中断は保証不可。タイムアウト済み処理が背景に残る場合がある |
| WI-08 | 512MiBデコード元データ上限、WICネイティブHRESULT別のスキップ/全体終了判定、テスト追加 | 80MP制限は維持。特定のWindowsコーデックが使う内部メモリは制御対象外 |
| WI-09 | PID/TID、プロセス間Mutex/再試行/退避ログ、起動/終了トレースを追加 | 実機でのログ完全性を検証 |
| WI-10 | 登録済みWaitによる前面化イベント受信、LibraryImportのフォアグラウンド権限許可 | Windowsフォアグラウンド制限により100%前面化は保証できない |
| WI-11 | 128件間隔のキャンセル検出、列挙最大20,000件、不正アクセススキップ | OS/ネットワーク側でブロックされた1件の列挙自体は中断不可 |
| WI-12 | PNG/JPEG/Gray16/縦横比/破損/大容量ファイルの本物のWICテスト、別プロセスMutex検証をCIに追加 | 対話ウィンドウ連続起動の確認は実機専用 |
| WI-13 | 80MP境界/50,000px/64bpp/WIC HRESULTの純関数テスト拡張、コメントとREADMEを更新 | 一部フィクスチャ未整備 |

## 推奨確認順序

1. PRのWindows EXE成果物をダウンロードして展開、タスクバーの旧版ピン留めを外して新EXEを固定。
2. ローカル画像フォルダ（PNG/JPEGを混在）を開き、プレビューとサムネイルが表示されること。
3. 空フォルダ、破損画像、極端な縦横比、500枚での連続操作と読み込み中のクローズを確認。
4. `powershell -ExecutionPolicy Bypass -File .\scripts\stress-relaunch.ps1 -ExePath .\publish\ImageSelector.exe -Mode Overlap -Iterations 20 -ImageCount 500`
5. 前回版のEXEについては別の安全な検証環境でのみ同じ試験を行い、CSVを比較すること。OS全体が固まった版を業務用PCで再実行しない。

ログ場所: `%LOCALAPPDATA%\ImageSelector\logs\error.log`（タイムスタンプ/PID/TID付き）。

## 検証上の重要な注意

静的コードレビュー、CIビルド・単体試験・Windows runner上の実画像デコード試験は、対話型Windowsデスクトップで起動→閉じる→即起動時に絶対にフリーズしないことの証明ではない。タスクマネージャーから終了できない状況はWICやストレージ、WSL共有、ファイルシステムフィルタなどのOS側事象も含めてログ・ダンプ分析する。
