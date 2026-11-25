# リリース手順

この文書は、リリースを作るメンテナー向けの手順です。利用者向けの導入手順は [README.md](../README.md) を参照してください。

リリースは、`release` ブランチのコミットに付けたタグ `vMAJOR.MINOR.PATCH` (例: `v0.2.0`) を push すると、GitHub Actions (`.github/workflows/release.yml`) が作ります。パッケージの署名は、GitHub Environment `release` に保存した専用の証明書で行います。

## 初回の準備 (一度だけ)

1. 署名用の証明書を作ります。

   ```powershell
   .\scripts\new-release-certificate.ps1
   ```

   - 証明書は `CN=waigoma` の自己署名証明書です。有効期間は 5 年です。
   - 既定では `Documents\waigoma-signing\release.pfx` に PFX を書き出します。リポジトリの中には作られません。
   - 最後に、PFX の base64 とパスワードを順にクリップボードへコピーします。画面の指示に従って、それぞれ GitHub に登録してください。クリップボード履歴 (Win+V と本拡張) には残らないように設定されています。
   - PFX とパスワードは、パスワードマネージャーなど安全な場所に保管してください。どちらかを失うと、今後のリリースは別の証明書で署名することになり、利用者は新しい `waigoma.cer` を信頼し直す必要があります。

2. GitHub で Environment `release` を作ります。リポジトリの Settings → Environments → New environment で作成し、次の 2 つをシークレットとして登録します。

   | シークレット名 | 中身 |
   |---|---|
   | `SIGNING_PFX_BASE64` | 1 で作った PFX の base64 |
   | `SIGNING_PFX_PASSWORD` | 1 で作った PFX のパスワード |

   - Deployment branches and tags は「Selected branches and tags」にし、タグ `v*` だけを許可します。
   - 必要に応じて Required reviewers を設定します。設定すると、署名のジョブは承認されるまで始まりません。

3. `release` ブランチを保護します。Settings → Rules で、`release` への直接 push を禁止し、変更はプルリクエストでだけ入るようにします。

4. タグのルールセットを作ります。Settings → Rules → Rulesets で、対象を `v*` のタグにし、作成、更新、削除を管理者だけに制限します。これで、リリースに使うタグを許可された人だけが作れるようになります。

## リリースごとの手順

1. 変更を `release` ブランチに入れて push します。

   ```powershell
   git switch release
   git merge <作業ブランチ>
   git push origin release
   ```

2. `release` の最新コミットに、版のタグを付けて push します。タグの形式は `vMAJOR.MINOR.PATCH` です。

   ```powershell
   git tag -a v0.2.0 -m "v0.2.0"
   git push origin v0.2.0
   ```

   マニフェストの版 (`Package.appxmanifest` の `Version`) は、ワークフローがタグから設定します。リポジトリ側の値を書き換える必要はありません。

3. GitHub の Actions タブで `release` のワークフローを確認します。Required reviewers を設定している場合は、`publish` ジョブの承認を行ってください。

4. 完了すると GitHub Release が作られます。次のファイルが添付されていることを確認します。

   - `ClipboardHistory_<version>_x64.msix`
   - `waigoma.cer`
   - `install.ps1`
   - `LICENSE`
   - `THIRD-PARTY-NOTICES.md`
   - `Apache-2.0.txt`
   - `SHA256SUMS.txt`

   リリースのノートには、導入の手順、証明書の拇印、証明書を信頼することの意味が書かれます。

## ワークフローが確認すること

ワークフローは次を確認してから、リリースを作ります。

- タグが `^v\d+\.\d+\.\d+$` の形式であること。
- タグのコミットが `origin/release` の祖先であるか、同じコミットであること。`release` に入っていないコミットに付けたタグでは、ワークフローが失敗します。
- テストが通ること。
- 版が `MAJOR.MINOR.PATCH.0` に設定され、パッケージがビルドされること。

ジョブの役割は次のとおりです。

- `build` ジョブは、読み取りだけの権限で動きます。未署名のパッケージと署名に必要なスクリプトを、3 日間の成果物として残します。シークレットは使いません。
- `publish` ジョブは、`release` Environment の中で動きます。シークレットは署名の手順だけに渡され、`SHA256SUMS.txt` を作って GitHub Release を作ります。同じ版のリリースがすでにあれば、失敗します。
- ワークフローで使う外部アクションは、バージョンのタグではなく完全なコミット SHA で固定しています。

## 鍵が漏えいした場合、失った場合

**秘密鍵 (PFX) や、そのパスワードが漏えいしたおそれがあるとき**

1. GitHub の Environment `release` からシークレットを削除します。
2. `new-release-certificate.ps1` で新しい証明書を作り、シークレットを登録し直します。
3. 漏えいした証明書で署名されたリリースを、GitHub Release から削除します。
4. 利用者には、新しい `waigoma.cer` を信頼することと、古い証明書の信頼を削除することを案内します。

自己署名証明書は失効リストで無効にできません。すでに古い証明書を信頼している利用者の PC では、古い証明書で署名されたパッケージは、信頼が削除されるまで有効なままです。

**PFX やパスワードを失ったとき**

新しい証明書を作り、シークレットを登録し直します。以後のリリースは新しい証明書で署名されるため、利用者は新しい `waigoma.cer` を信頼し直す必要があります。

## ローカル署名との違い

`new-certificate.ps1` で作る証明書 (ローカルで署名するビルド用) と、リリース用の証明書は別のものです。どちらも Subject は `CN=waigoma` なので、パッケージの発行者は同じです。そのため、利用者は両方の証明書を別々に信頼する必要があります。

ローカルのビルドは `Cert:\CurrentUser\My` の証明書で署名します。リリースは、CI 上で PFX を一時的に取り込んで署名し、署名が終わるとストアから証明書と秘密鍵を削除します。
