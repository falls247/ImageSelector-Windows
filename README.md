# ImageSelector for Windows

Windows 10/11向けの **C# / WPF / .NET 10** デスクトップアプリ。画像の選別、男性・女性セリフ／キャプション／効果音／吹き出しの注釈入力、サイドカーJSONの保存を行う。元画像は保存時に変更しない。

## ビルドとEXEの発行

Windows 10/11（x64）と [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) が必要。以下のコマンドはリポジトリのルートでPowerShellから実行する。

### 開発用ビルドと起動

```powershell
dotnet build .\ImageSelector\ImageSelector.csproj -c Release
dotnet run --project .\ImageSelector\ImageSelector.csproj
```

ビルドした実行ファイルは `ImageSelector\bin\Release\net10.0-windows\ImageSelector.exe`。この形式で実行するPCには.NET 10 Desktop Runtimeが必要。

### 配布用の単一EXE

以下は.NETランタイムを含む自己完結型の単一ファイルを `publish` フォルダへ発行する。配布先に.NETのインストールは不要。

```powershell
dotnet publish .\ImageSelector\ImageSelector.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\publish
```

発行後は `publish\ImageSelector.exe` を起動する。対象はWindows 10/11のx64 PC。ARM64向けに発行する場合は `-r win-arm64` を指定する。アプリのウィンドウとEXEには `ImageSelector/Assets/ImageSelector.ico` を設定済み。

### GitHub Actionsから取得

GitHubの `Actions` → `Build Windows EXE` → `Run workflow` で発行を開始する。完了後、実行結果ページのArtifactsから `ImageSelector-win-x64` をダウンロードして展開する。push時はビルド・デコード上限テスト、pull request時はさらに配布用EXEを生成してArtifactsにアップロードする。

## 操作

| 場所 | キー／操作 | 動作 |
|---|---|---|
| メイン | `←` / `→` | 前・次の画像へ1枚移動 |
| メイン | `↑` / `↓` | サムネイルの同じ列で1行上・下へ移動（最終行に画像がなければ停止） |
| メイン | `Space` | 注釈入力を開く |
| メイン | `Enter` | 元画像をコピー＋注釈JSON保存。成功すると次の画像へ |
| メイン | `Delete` | 元画像をWindowsのごみ箱へ移動。保存先のコピーは保持 |
| メイン | `Alt+Delete` | 選択済み画像のコピー画像とJSONを削除（確認ダイアログあり）。元画像は保持 |
| メイン | `Ctrl+O` | フォルダを開く |
| メイン | `T` | サムネイル一覧を表示／非表示 |
| 注釈入力（ショートカット待機） | `M` / `F` / `C` / `E` / `H` | 男性セリフ／女性セリフ／キャプション／効果音／吹き出しの行を追加 |
| 注釈入力（本文入力中） | `Esc` | 文字欄のフォーカスを解除し、ショートカット待機へ移動 |
| 注釈入力（ショートカット待機） | `Esc` | 注釈ウィンドウを閉じる。未反映内容は破棄 |
| 注釈入力 | `Alt+Enter` | 現在の注釈を**メモリの下書きにだけ反映**。文字入力を解除してショートカット待機へ戻り、ウィンドウは閉じない |
| 注釈入力 | `Ctrl+Enter` | 注釈確定→入力ウィンドウを閉じる→画像＋JSON保存。成功すると次の画像へ |
| 注釈入力（本文入力中） | `Enter` | 改行 |

### 日本語IMEとショートカット

- 文字入力欄で日本語入力が有効でも、`Esc` でショートカット待機領域へフォーカスを移動する。
- `Alt+Enter` と「注釈だけ反映」ボタンでも、自動的に入力欄を離れてショートカット待機へ戻る。
- 文字入力欄以外（注釈一覧・ボタン・待機領域など）にフォーカスがあれば `M/F/C/E/H` を認識する。文字入力中の英字は通常の本文入力に使う。
- 待機領域はIMEを無効にして物理的な `M/F/C/E/H` キーを認識する。**Windowsの日本語IMEをOFFに切り替える必要はない**。
- ただしIME変換候補の選択中など、IME自身がEscを先に処理する場合は、Escをもう一度押す必要がある。
- ショートカットで行を追加した後は、本文欄に自動でフォーカスして日本語入力可能。


### 吹き出しのJSON互換性

- `H` を押すと新しい種類 `speech_bubble` の注釈を追加する。`speaker` は設定せず、本文 `text` を記録する。
- `schemaVersion` は従来の `1` を維持するが、許可する `type` に `speech_bubble` が増える。**既存の別アプリが種別を厳密に検証している場合は、新種別の受け入れ処理と同梱JSON Schemaへの更新が必要**。
- 吹き出しの位置・形状・スタイルは従来どおり後段アプリで指定する。

### サイドバー

左側：幅をドラッグで変更できるタイル型サムネイル一覧（倍率50～300%、選択済みはチェックマーク）。上下キーは現在の列数に合わせて1行上下へ移動（サムネイル非表示時は動作しない）。300%表示時にサムネイルがサイドバーより広い場合は横スクロール可能。下部：画像名、現在位置／選択済み件数／注釈件数（自動折り返し）、操作ボタンを2列で配置。フォルダ選択ボタンは最上段に半幅で配置。右側は画像のみ、縦横比維持でウィンドウ内にフィットする。

## 保存先（重要：以前の版から変更）

**開いた画像フォルダと同じ親ディレクトリに `<フォルダ名>_selected` を作る。**

```text
D:\pictures\
  album\
    img001.jpg
    img002.png
  album_selected\
    img001.jpg
    img001.jpg.json
```

- 例：`D:\pictures\album` を開いたら `D:\pictures\album_selected` が保存先。
- `Enter` / 注釈入力画面の `Ctrl+Enter` で原本コピー＋対応JSONを保存。コピー済み画像がある場合は画像自体を上書きせずJSONのみ更新。
- `Alt+Enter` / 「注釈だけ反映」はファイルに書き込まない。一度反映した内容は、その注釈画面をキャンセルしてもメイン画面の未保存下書きに残る。アプリ終了で未保存下書きは失われる。
- **旧バージョンの `album\selected` は自動移行しない。** 旧データを引き継ぐ場合、アプリ停止後に旧 `selected` フォルダ内の画像とJSONを新しい `album_selected` に移す（同名ファイル上書き注意）。
- 別アプリで再利用するJSONの形式は変えていない。`schema/image-annotation.schema.json` にスキーマあり。

JSON例：

```json
{
  "schemaVersion": 1,
  "imageFile": "img001.jpg",
  "annotations": [
    { "type": "dialogue", "speaker": "male", "text": "こんにちは" },
    { "type": "dialogue", "speaker": "female", "text": "おはよう" },
    { "type": "caption", "text": "翌朝" },
    { "type": "sound_effect", "text": "ドンッ" },
    { "type": "speech_bubble", "text": "それで？" }
  ],
  "savedAt": "2026-10-13T21:00:00+09:00"
}
```

## 修正レビューと画像表示の回帰不具合

- [高速再起動レビューWI-01〜WI-13の対応状況](docs/review-followup-status.md)
- 前回の「画像を表示できない」は、`BitmapImage.StreamSource` と `IgnoreImageCache` の併用による `ArgumentNullException` が原因。Windows上のWIC実画像デコード試験で再現後に修正済み。
- **安全制限:** 画像ファイル256MiBまで、元画像80MP・1辺50,000px・展開元512MiBまで。超過画像は個別に読み込みを拒否して次画像へ進める。
- 単一インスタンス化はWPF生成前に判定し、旧プロセスの終了を待つ。終了待機が長い場合は旧PIDを通知。
- 注釈JSONはバックグラウンド読み込み（読み込み中は件数が「…」）。保存・元画像削除・選択取消もUIスレッド上でストレージI/Oを実施しない。
- 保存中のクローズは最大10秒延期し、安全に終了を試みる。応答のないストレージには完全な中断保証はない。

## フリーズ調査・高速再起動検証

Windows EXEの連続起動、画像デコード上限、再起動ストレステスト、イベントログ調査手順は [docs/rapid-relaunch-diagnostics.md](docs/rapid-relaunch-diagnostics.md) に記載。

- タスクバーから連続起動しても既存ウィンドウを再利用する（2つ目のプロセスは起動を取り消す）。
- **仕様変更:** 引数なし起動ではフォルダ選択ダイアログを自動表示しない。Windows ShellやWSL共有フォルダの応答待ちを起動時に発生させないため、起動後に「フォルダを開く」または `Ctrl+O` を操作する。フォルダのパス引数を指定した場合は自動読み込み。
- 安全対策としてソース画像は最大80MP・長辺50,000pxまで。プレビューは最大3072px・600万画素、サムネイルは長辺256px。
- 終了時にバックグラウンドの画像読み込み・フォルダ走査・保存をキャンセル。
- 単一ファイル圧縮を無効化し、起動時の展開CPU負荷を削減する。EXEのサイズは増加する。

## 確認項目

1. フォルダを開くと右側に画像、左側にタイル型サムネイルが表示される。スプリッタをドラッグするとタイルが再配置される。
2. サムネイル倍率変更・Tによる表示切替、件数の横並び／折り返しを確認。
3. Space→M→男性セリフの本文へ入力→Esc→Fで女性セリフ行を追加できる。Esc→Hで吹き出し行も追加できる。
4. 日本語IME有効状態のまま、Escで文字入力を解除後、M/F/C/E/Hを利用できる（候補変換中はEsc再押下が必要な場合あり）。
5. Alt+Enterで下書きだけ反映し、注釈ウィンドウが閉じない。保存先には何も作られない。
6. Ctrl+Enterで画像＋JSONを `album_selected` に作り、次の画像へ進む。保存済みサムネイルにチェック。
7. Alt+Deleteで選択中の `album_selected` 内の画像とJSONだけ削除でき、原本は残る。
8. Deleteで元画像だけをごみ箱に移動し、コピーとJSONは残る。
9. 再起動後、`album_selected` のJSONを読み込み注釈の再編集ができる。

## 制約と検証状況

- 対象拡張子：JPG/JPEG, PNG, BMP, GIF, TIFF。選択フォルダ直下を検索。サブディレクトリは検索しない。
- 選択先の既存画像を元画像と比較して自動上書きはしない。編集中の同一フォルダを他プロセスから更新する運用は非推奨。
- 破損画像やJSONは例外を通知し、エラーログに記録。ログは `%LOCALAPPDATA%\ImageSelector\logs\error.log`。
- Windows上での実ビルドは提供環境では未実施。CIにWindowsビルド用ワークフローを同梱。


## 多数画像フォルダの読み込み高速化

- フォルダを開いた時点では、画像ファイル一覧の取得と選択済みファイルの一覧照合だけを実行。
- サムネイルのタイル表示は**行単位の仮想化**。画面に見える行だけを生成。
- サムネイル画像の縮小デコードはバックグラウンドで最大3枚ずつ実行。最大160枚をキャッシュ。
- メイン画像も非同期で読み込み、別の画像へ移動すると古い表示要求をキャンセル。
- サイドバー幅・倍率変更時は表示列数を計算し直す（画像の全件デコードなし）。
- データ保存先・JSON形式・各キーボードショートカットは変更なし。

### 大量画像のテスト手順

1. 500枚以上の画像を含むフォルダを開き、最初の画像が表示されるまでと、操作可能になるまでの時間を確認。
2. サムネイルを末尾までスクロールし、画像がスクロール位置に応じて読み込まれるか確認。
3. 左サイドバー幅、サムネイル倍率を変更し、タイルの折り返しと選択状態を確認。
4. 左右キー、上下キー（サイドバー幅と倍率を変えて再確認）、Space→Ctrl+Enter、Alt+Delete、Deleteでそれぞれ表示画像・選択タイル・JSON・件数が正しく更新されるか確認。
5. ログ `%LOCALAPPDATA%\ImageSelector\logs\error.log` の `LoadFolder UI ready` で一覧表示準備までの経過ミリ秒を確認。

**検証について：** Windows上での実ビルド・実測は必要。画像の保存場所（ローカルSSD／ネットワーク）やファイル形式により実測値は変動する。
