# Clipboard History for Command Palette / クリップボード履歴 (CmdPal 拡張)

[![CI](https://github.com/waigoma/cmdpal-clipboard-history/actions/workflows/ci.yml/badge.svg)](https://github.com/waigoma/cmdpal-clipboard-history/actions/workflows/ci.yml)

Clipboard History is an extension for PowerToys Command Palette (CmdPal). It records what you copy, lets you search the history from CmdPal, and pastes an entry back into the active app. The history is stored only on your PC, and the extension makes no network connections. Install it from a GitHub Release or build it from source; see [docs/RELEASING.md](docs/RELEASING.md) for how releases are made. This project is not affiliated with Microsoft or Raycast. It is released under the MIT License.

PowerToys Command Palette (CmdPal) の拡張です。コピーした内容を履歴として残し、CmdPal から検索して貼り付け直せます。Raycast の Clipboard History に近い使い心地を目指しています。本プロジェクトは Microsoft および Raycast とは提携していません。

## 概要

コピーした内容を記録し、CmdPal の一覧から検索して貼り付けます。履歴はこの PC の中だけに保存し、外部へは送りません。

## できること

- テキスト、リンク、メール、色、画像、ファイルのコピー内容を記録
- 名前の付与、ピン留め、種別での絞り込み、検索 (画像の文字と QR コードの内容も検索の対象)
- 貼り付け、コピー、プレーンテキストとしての貼り付け、形式を選んでの貼り付け
- 連続貼り付け (最新から古い方へ 1 件ずつ貼り付けます)
- 記録の一時停止、除外アプリ、保存期間の設定

## 導入

前提は次のとおりです。x64 版の Windows 11 と、PowerToys の Command Palette (CmdPal) です。動作は Command Palette 0.13.13096 / PowerToys 0.102 で確認しています。

1. CmdPal を Windows の起動時に自動で起動する設定にします。拡張は CmdPal が常駐している間だけ記録するためです。
2. 次のどちらかの方法で導入します。

署名済みの版は開発者モードが OFF のままで動きます。開発者モードを使っていた場合は、導入後に OFF へ戻せます。

### Releases から導入する (通常)

1. [Releases](https://github.com/waigoma/cmdpal-clipboard-history/releases) から、最新版の次の 4 つのファイルを 1 つのフォルダーに保存します。
   - `ClipboardHistory_<version>_x64.msix`
   - `waigoma.cer`
   - `install.ps1`
   - `SHA256SUMS.txt` (検証用)
2. 保存したファイルが改変されていないことを確認します。保存したフォルダーで次のコマンドを実行し、表示されたハッシュを `SHA256SUMS.txt` の行と見比べます。一致しないときは使わないでください。

   ```powershell
   Get-FileHash -Algorithm SHA256 .\ClipboardHistory_*_x64.msix, .\waigoma.cer, .\install.ps1
   ```

3. 管理者として開いた PowerShell で、証明書を「信頼されたユーザー」(`TrustedPeople`) に登録します。これは 1 回だけ行います。`C:\path\to` の部分は、保存した `waigoma.cer` の場所に置き換えます。

   ```powershell
   Import-Certificate -FilePath "C:\path\to\waigoma.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

4. 手順 1 のフォルダーで、通常の PowerShell から `install.ps1` を実行します。

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

5. CmdPal で「Reload Command Palette extensions」を実行するか、CmdPal を再起動します。

**証明書を信頼するとは、次のことを意味します。** この証明書は公的な認証局が発行したものではなく、作者が自分で作った自己署名証明書です (Subject は `CN=waigoma`)。手順 3 を行うと、この PC では `CN=waigoma` の証明書で署名されたパッケージが Windows に受け入れられます。同じ証明書で署名された別のパッケージも同じように扱われます。作者の身元を第三者が確認したものではありません。SHA256 の確認でわかるのは、ダウンロードしたファイルが公開された内容と同じであることだけです。信頼するかどうかは、ご自身で判断してください。信頼したくない場合は、次の「ソースから作る」の手順で、ご自身の証明書を使って作ってください。

### ソースから作る

前提は、.NET SDK 10.0.300 (`global.json` で指定)、Native AOT のビルドに必要な Visual Studio の構成、署名に使う `signtool.exe` (Windows SDK または NuGet の BuildTools) です。以下はリポジトリのルートで実行します。

1. 自分用の署名証明書を作ります。これは 1 回だけ行います。`CN=waigoma` の自己署名証明書を `Cert:\CurrentUser\My` に作り、公開部分を `artifacts\waigoma.cer` に出力します。

   ```powershell
   .\scripts\new-certificate.ps1
   ```

2. 拡張をビルドします。レイアウトは `artifacts\layout`、未署名のパッケージは `artifacts\unsigned` に作られます。

   ```powershell
   .\scripts\build.ps1
   ```

3. 署名済みのパッケージを作ります。

   ```powershell
   .\scripts\package.ps1
   ```

4. 管理者として開いた PowerShell で、リポジトリのルートに移動してから、証明書を「信頼されたユーザー」(`TrustedPeople`) に登録します。これは 1 回だけ行います。

   ```powershell
   Import-Certificate -FilePath .\artifacts\waigoma.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

   この操作は、その PC で `CN=waigoma` が署名したパッケージを信頼することを意味します。信頼した証明書の秘密鍵は非公開のまま、`Cert:\CurrentUser\My` に残ります。スクリプトは信頼の登録を自分では行いません。

5. パッケージを導入します。

   ```powershell
   .\scripts\install.ps1
   ```

   開発用の登録 (開発者モードでの `Add-AppxPackage -Register`) が残っているときは、先に削除が必要です。その場合は `-RemoveDevelopment` を付けます。対応している環境では履歴 (LocalState) も残ります。

   ```powershell
   .\scripts\install.ps1 -RemoveDevelopment
   ```

6. CmdPal で「Reload Command Palette extensions」を実行するか、CmdPal を再起動します。

## ホットキーの割り当て

ホットキーは拡張からは登録しません。CmdPal 側で割り当てます。

1. CmdPal の設定を開き、「拡張機能」→「クリップボード履歴」を選びます。
2. 「クリップボード履歴」 (一覧) に **Alt+V** を割り当てます。
3. 「連続貼り付け」にもホットキーを割り当てます。連続貼り付けは、画面を出さずにホットキーで実行する想定の機能です。

Alt+V は、ほかのアプリ (ランチャーなど) でも使われることがあります。同じキーを別のアプリが使っているときは、そちらの割り当てを外してから使ってください。

## 使い方

一覧で項目を選んで、次の操作をします。

| 操作 | キー | 対象 |
|---|---|---|
| アクティブなアプリへ貼り付け (既定) | Enter | すべて |
| クリップボードへコピー | Ctrl+Enter | すべて |
| プレーンテキストとして貼り付け | Ctrl+Shift+Enter | テキスト系、ファイル (パス文字列) |
| 形式を選んで貼り付け | なし | 複数の形式を持つもの |
| 内容を編集 (保存 / 保存して貼り付け) | なし | テキスト系 |
| 名前を変更 | Ctrl+E | すべて |
| ピン留め / 解除 | Ctrl+. | すべて |
| 画像から文字をコピー | なし | 画像 |
| QR コードの内容をコピー | なし | QR を含む画像 |
| 開く | Ctrl+O | リンク、メール、画像、ファイル |
| 保存場所を開く | なし (メニューから) | 画像、ファイル |
| このアプリを記録しない | なし | コピー元が分かるもの |
| 削除 | Ctrl+X | すべて |
| 期間で削除 (5 / 15 / 30 分、1 / 24 時間) | Ctrl+Shift+X | 最終コピー日時がその期間内のもの |
| すべて削除 | なし | 確認あり |

- 主操作 (Enter) は、設定で「貼り付け」と「コピー」を入れ替えられます。入れ替えると、もう一方が Ctrl+Enter になります。
- 期間での削除とすべての削除は確認を出します。ピン留めは消しません。
- 貼り付け後も、その内容はクリップボードに残ります。
- 一覧からの貼り付けで貼り付け先が確かでないときは貼り付けず、コピーに留めます。連続貼り付けでは、貼り付け先がないときは何もしません。

トップレベルのコマンドは次のとおりです。

- 「クリップボード履歴」: 履歴の一覧を開きます。
- 「連続貼り付け」: 最新から 1 件ずつ古い方へ貼り付けます。
- 「連続貼り付けをリセット」: 次の連続貼り付けを最新から始めます。
- 「記録を一時停止」 / 「記録を再開」: コピー内容の記録を止めたり再開したりします。

## 設定

設定は CmdPal の「拡張機能」→「クリップボード履歴」にあります。既定値は括弧内です。

- 保存期間: 最終コピー日時が期間を過ぎた項目を消します。ピン留めは消しません (3 か月)
- 主操作: 貼り付けかコピーか (貼り付け)
- プレーンテキストを優先: 書式付きの内容でも、テキストだけを貼り付けます (OFF)
- 操作後に履歴の先頭へ移動: 貼り付けとコピーの後、その項目を先頭へ移します (ON)
- 画像の文字認識: 画像を記録したあとに文字を読み取ります (ON)
- 除外アプリ: 記録しないアプリの実行ファイル名を 1 行に 1 つ書きます。例: `keepass.exe` (空)
- 連続貼り付けの制限時間: この時間を過ぎると、次の連続貼り付けは最新から始まります (1 分)
- 連続貼り付けで改行を付ける: ON のとき、テキスト系の項目はプレーンテキストに改行を付けて貼り付けます。画像とファイルは改行を付けず、記録したとおりに貼り付けます (ON)
- 1 形式の上限 / 全体の上限: 大きさの上限を MB で指定します (50 / 2048)
- 保存先: 履歴を保存するフォルダーのパスです。空のときは既定の場所を使います (空)

保存先の変更は CmdPal を再起動したあとに有効になります。既存の履歴は移動しません。

## 保存場所とプライバシー

- 既定の保存先は、パッケージのローカルデータ (`LocalState\data`) です。場所は `%LOCALAPPDATA%\Packages\` の下にある `waigoma.CmdPal.ClipboardHistory` で始まるフォルダーです。
- 履歴は暗号化せずに保存します。同じユーザーで動くほかのプログラムからも読めます。
- 設定ファイルは `LocalState` に置き、保存先が変わっても失われないようにしています。
- 動作のログは `%LOCALAPPDATA%\Packages\<パッケージのファミリー名>\LocalState\log.txt` に書きます。1 MB を超えると `log.1.txt` に移します。ファミリー名は `waigoma.CmdPal.ClipboardHistory_` で始まります。起動に失敗したときは、一覧の「クリップボード履歴 (起動に失敗しました)」に例外の種類と HResult、ログの場所を表示します。
- 記録しないもの:
  - 除外アプリからのコピー
  - Windows の非記録指定 (`ExcludeClipboardContentFromMonitorProcessing`、`Clipboard Viewer Ignore`、`CanIncludeInClipboardHistory` が 0) が付いた内容
  - 拡張が自分で書き込んだ内容
- 通信はしません。パッケージは外部への通信を要求せず、OCR と QR の読み取りもこの PC の中で行います。
- ログには、クリップボードの本文、プレビュー、名前、検索語、ファイルのパスを書きません。

## 制限

- CmdPal が停止している間は記録しません。
- 起動時に補えるのは、現在のクリップボードと、Windows 標準のクリップボード履歴 (有効な場合) にあるテキストとファイルだけです。画像は標準の履歴からは補いません。除外アプリを設定している間と、一時停止中は、標準の履歴から補いません。初回の起動でも補いません。
- 管理者権限で動くアプリへは自動で貼り付けられません。その場合はコピーまでに留めます。
- 次の機能は対象外です: AI 連携、スニペット保存、Share、クラウド同期、Raycast の既存履歴の取り込み、リンク先の画像やタイトルの取得 (通信しないため)、履歴の暗号化、ストア公開と winget 公開、arm64 版。
- 全体の上限を超えると、ピン留めのない古い項目から消します。ピン留めだけで上限を超えるときは消さず、記録は続けます。
- 削除した画像は、保存先のファイルとしては残ります。毎時の整理で消えます。
- Windows 標準のクリップボード履歴 (Win+V) と Raycast には手を加えません。

## 更新と削除

Releases で新しい版が出たら、その版の 4 つのファイルを新しいフォルダーに保存し、「Releases から導入する」の手順 4 と 5 を行います。証明書が同じものであれば、信頼の登録をやり直す必要はありません。導入済みと同じ版番号では上書きできません。

ソースから更新するには、`src\ClipboardHistory.Extension\Package.appxmanifest` の `Version` を上げてから、次の順で実行します。導入済みと同じ版番号では上書きできません。

```powershell
.\scripts\build.ps1
.\scripts\package.ps1
.\scripts\install.ps1
```

証明書は再利用されるため、更新のたびに信頼の登録をやり直す必要はありません。

削除は次のとおりです。最初の実行では、削除されるものを表示して止まります。

```powershell
.\scripts\uninstall.ps1
.\scripts\uninstall.ps1 -Force
```

証明書の信頼は次の手順で外せます。管理者として開いた PowerShell で、`Cert:\LocalMachine\TrustedPeople` から `CN=waigoma` の証明書を削除します。ソースから作った場合は、署名に使った秘密鍵が `Cert:\CurrentUser\My` にあり、それも不要なら削除します。どちらも利用者が行います。

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -ceq 'CN=waigoma' | Remove-Item
Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -ceq 'CN=waigoma' | Remove-Item
```

削除すると、既定の保存先の履歴 (`LocalState\data`) と設定も消えます。保存先を変更していた場合、その場所の履歴は消えません。取り出したい内容があれば、削除の前に確認してください。

## 開発

ビルドとテストは次のコマンドで行います。

```powershell
.\scripts\build.ps1
dotnet test tests\ClipboardHistory.Core.Tests -c Debug
```

- `build.ps1` は Release (x64、Native AOT) で拡張をビルドし、`artifacts\layout` にレイアウトを、`artifacts\unsigned` に未署名の `.msix` を作ります。
- `package.ps1` は `artifacts\unsigned` の `.msix` に、自分の証明書で署名した `artifacts\ClipboardHistory_<version>_x64.msix` を作ります。
- `new-certificate.ps1` は `CN=waigoma` の自己署名証明書を `Cert:\CurrentUser\My` に作ります。秘密鍵はエクスポートできません。公開部分だけを `artifacts\waigoma.cer` に出します。
- `install.ps1` と `uninstall.ps1` の詳細は、それぞれの「導入」と「更新と削除」を参照してください。
- CI (`.github/workflows/ci.yml`) は、push と pull request ごとにテストとビルドを行います。リリースの手順は [docs/RELEASING.md](docs/RELEASING.md) を参照してください。

必要なもの: .NET SDK 10.0.300、Native AOT のビルドに必要な Visual Studio の構成 (`build.ps1` は Visual Studio Installer のツールを PATH に加えます)、署名に使う `signtool.exe` (`package.ps1` が NuGet の BuildTools か Windows SDK から探します)。

`artifacts\`、`*.cer`、`*.pfx`、`*.msix` は Git の管理対象外です。秘密鍵をリポジトリに入れないでください。

## ライセンス

This project is licensed under the MIT License. See [LICENSE](LICENSE). Third-party components and their notices are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The full text of the Apache License 2.0, which applies to some of those components, is in [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt).

本プロジェクトは MIT License で公開しています (`LICENSE`)。同梱・利用しているサードパーティ製コンポーネントのライセンスとコピーライト表記は `THIRD-PARTY-NOTICES.md` に、Apache License 2.0 の全文は `licenses/Apache-2.0.txt` にあります。

## 関連ドキュメント

- [docs/RELEASING.md](docs/RELEASING.md): リリースの手順 (メンテナー向け)
