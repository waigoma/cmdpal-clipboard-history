# CmdPal Clipboard History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raycast の Clipboard History 相当を、軽い CmdPal 拡張 (署名済み MSIX) として作る。

**Architecture:** 1 つの NativeAOT プロセスが記録と UI を担う。CmdPal に依存しない `Core` が受信、保存、検索、貼り付け、連続貼り付けを持ち、`Extension` は CmdPal SDK との橋渡しに徹する。画像の処理は同じ exe を `--worker` で起動した短命プロセスに任せる。

**Tech Stack:** C# / .NET 10 (`net10.0-windows10.0.22621.0`)、NativeAOT、CmdPal SDK 0.12.260812002、Microsoft.Data.Sqlite.Core + winsqlite3、Windows.Media.Ocr、ZXing.Net、xUnit。

**Spec:** `docs/superpowers/specs/2026-11-25-clipboard-history-design.md`

## Global Constraints

- 対象は Windows 11 x64 のみ。TFM は全プロジェクト `net10.0-windows10.0.22621.0`、`WindowsSdkPackageVersion` は `10.0.26100.68-preview`。
- 名前空間の根は `Waigoma.ClipboardHistory`。`Nullable`、`AllowUnsafeBlocks`、`IsAotCompatible`、`TreatWarningsAsErrors` を有効にする。`InvariantGlobalization` は true。
- 版の固定: `Microsoft.CommandPalette.Extensions` 0.12.260812002、`Microsoft.Windows.CsWinRT` 2.2.0、`Shmuelie.WinRTServer` 2.1.1、`Microsoft.Windows.SDK.BuildTools.MSIX` 1.7.20250829.1、`Microsoft.Windows.SDK.BuildTools` 10.0.26100.4188、`Microsoft.Data.Sqlite.Core` 10.0.15、`SQLitePCLRaw.bundle_winsqlite3` 2.1.11、`ZXing.Net` 0.16.11、`Microsoft.DotNet.ILCompiler` 10.0.15、`xunit` 2.9.3、`xunit.runner.visualstudio` 4.0.1、`Microsoft.NET.Test.Sdk` 18.12.0。ほかのパッケージを足さない。
- 履歴の本文、プレビュー、名前、検索語、ファイルのパスをログに書かない。件数、長さ、種別、プロセス名、例外の型とメッセージの HResult だけを書いてよい。
- 外部と通信しない。manifest に `internetClient` を宣言しない。capability は `runFullTrust` のみ。
- 貼り付け先に確信がないときは Ctrl+V を送らない。
- 例外で拡張を落とさない。COM から呼ばれる override と、スレッドやタイマーの入口は try / catch で囲み `Log.Error` へ渡す。
- AOT: リフレクションを使う JSON シリアライズ、`dynamic`、`Marshal.GetDelegateForFunctionPointer` を使わない。JSON は `JsonDocument` / `Utf8JsonWriter` か source generator。P/Invoke は `LibraryImport`。
- UI の文字列は日本語、コードの識別子とコメントは英語。コメントは「なぜ」だけを書く。
- テストは利用者のクリップボードに触れない。実機に触れるテストは作らない。
- `spikes/` は参照専用 (git 管理外)。API の使い方の見本は `spikes/ext/SpikeExt/*.cs`。コピーした後は命名と責務を本計画に合わせる。
- AOT ビルドの前に PATH の先頭へ `C:\Program Files (x86)\Microsoft Visual Studio\Installer` を足す。
- コミットは `type(scope): English subject (50 文字以内)`、本文は日本語。ブランチは `feature/clipboard_history`。push しない。

テストの実行: `dotnet test tests/ClipboardHistory.Core.Tests -c Debug`。以下「テスト実行」と書く。期待はいずれも「追加前は該当テストが FAIL またはコンパイル不能、実装後は全件 PASS、警告 0」。

## Review Focus

1. 連打: 貼り付けの途中で次の貼り付けが始まると、先の Ctrl+V が後の内容を貼る。後の要求は無視されるべき (Task 6)。
2. 検索語に `%` `_` `\` や全角英数が入る。文字どおりに一致すべき (Task 4)。
3. 記録後に元の画像ファイルや保存先のファイルが消えている。一覧は壊れず、貼り付けはエラー表示に留まるべき (Task 6、Task 10)。
4. 拡張を再起動すると、削除した項目や除外アプリの内容が補完で復活する。復活してはならない (Task 8)。
5. 巨大なテキスト、改行だけのテキスト、閉じていないサロゲートやバッククォートを含むテキスト。記録と詳細表示が壊れてはならない (Task 2、Task 10)。

---

## ファイル構成

```
ClipboardHistory.slnx / Directory.Build.props / Directory.Packages.props / nuget.config
src/ClipboardHistory.Core/
  Log.cs                     ログ (1 MB で 1 世代だけ退避)
  Model.cs                   EntryKind, ClipFormat, FormatNames, CapturedEntry, EntrySummary, EntryDetail ほか
  HistoryOptions.cs          設定値 (不変)
  HistoryService.cs          記録・保持・補完・ワーカー起動のまとめ役
  Capture/  ContentClassifier, ColorParser, ContentHasher, CaptureFilter, CaptureBuilder, ClipboardMonitor
  Store/    StorePaths, Database, BlobStore, HistoryStore (partial: 書き込み / 検索 / 保守), JobQueue
  Search/   SearchNormalizer, LikePattern
  Paste/    ClipboardPayload, PayloadFactory, PasteService, IWindowSystem, IClipboardWriter
  Sequence/ SequentialPaste
  Worker/   WorkerRunner, IImageProcessor, WinRtImageProcessor, DibCodec, OcrText
  Interop/  Native, WindowSystem, ClipboardReader, ClipboardWriter, DropFiles, HtmlFormat
src/ClipboardHistory.Extension/
  Program, ClipboardHistoryExtension, HistoryCommandsProvider, AppHost, SettingsManager
  Pages/    HistoryPage, HistoryFilters, EntryItemFactory, EntryDetailsBuilder, EditPage, RenamePage, FormatsPage, BulkDeletePage
  Commands/ PasteCommand, CopyCommand, EntryCommands (pin, delete, open, exclude, ocr, qr), TopLevelCommands
tests/ClipboardHistory.Core.Tests/
scripts/ build.ps1, new-certificate.ps1, package.ps1, install.ps1, uninstall.ps1
README.md
```

---

### Task 1: 土台、モデル、種別判定

**Files:** Create: ソリューション一式、`Log.cs`、`Model.cs`、`HistoryOptions.cs`、`Capture/ContentClassifier.cs`、`Capture/ColorParser.cs`。Test: `ContentClassifierTests.cs`

**Interfaces — Produces** (後の全タスクが使う。この型は変えない):

```csharp
public enum EntryKind { Text = 0, Link = 1, Email = 2, Color = 3, Image = 4, Files = 5 }
public static class FormatNames { // "HTML Format", "Rich Text Format", "PNG", "CF_DIB", "CF_DIBV5"
  public const string Html, Rtf, Png, Dib, DibV5; }
public sealed record ClipFormat(string Name, byte[] Data);
public sealed record CapturedEntry(EntryKind Kind, string? Body, IReadOnlyList<ClipFormat> Formats,
  string Hash, string? SourceApp, DateTimeOffset CopiedAt);          // Files: Body は '\n' 区切りのパス
public enum ImageState { None = 0, Pending = 1, Done = 2, Failed = 3 }
public sealed record EntrySummary(long Id, EntryKind Kind, string Preview, string? Name, string? SourceApp,
  bool Pinned, DateTimeOffset CreatedAt, DateTimeOffset LastCopiedAt, int Width, int Height,
  long ByteSize, int CharCount, int LineCount, ImageState ImageState, bool HasOcr, bool HasQr,
  string? ThumbPath);
public sealed record StoredFormat(string Name, string FilePath, long Size);
public sealed record EntryDetail(EntrySummary Summary, string? Body, IReadOnlyList<StoredFormat> Formats, string? Ocr, string? Qr);
public sealed record HistoryOptions {  // 既定値は仕様 6.7
  TimeSpan? Retention (= 90 日、null は無期限); bool PasteIsPrimary = true; bool PreferPlainText;
  bool MoveToTop = true; bool Ocr = true; IReadOnlySet<string> ExcludedApps (小文字のファイル名);
  TimeSpan SequenceTimeout = 1 分; bool SequenceNewline = true;
  long MaxFormatBytes = 50 MB; long MaxTotalBytes = 2048 MB; }
public static class Log { void Initialize(string dir); void Info(string); void Error(string where, Exception); }
public static class ContentClassifier { public static EntryKind ClassifyText(string text); }
public readonly record struct Rgba(byte R, byte G, byte B, byte A);
public static class ColorParser { public static bool TryParse(string text, out Rgba color); }
```

- [ ] **Step 1: 失敗するテスト** — `[Theory]` で次を確認する。

| 入力 | 期待 |
|---|---|
| `https://example.com/a?b=1`、前後に空白や改行が付いたもの | Link |
| `http://localhost:3000`、`HTTPS://EXAMPLE.COM` | Link |
| `https://a.com https://b.com`、`see https://a.com`、`ftp://a.com`、`https://`、`javascript:alert(1)` | Text |
| `user@example.com`、`mailto:user@example.com` | Email |
| `user@`、`a b@example.com`、`user@example.com, b@example.com`、`@user` | Text |
| `#fff`、`#3A7BD5`、`#3A7BD580`、`rgb(1, 2, 3)`、`rgba(1,2,3,0.5)`、`rgb(1 2 3 / 50%)`、`hsl(120, 50%, 50%)`、`hsla(120,50%,50%,0.3)` | Color |
| `#ff`、`#12345`、`#ggg`、`rgb(256,0,0)`、`rgb(1,2)`、`hsl(120,50,50)` | Text |
| 空白だけ、改行だけ、`abc` | Text |

`ColorParser`: `#f00` → (255,0,0,255)、`#3A7BD580` → (0x3A,0x7B,0xD5,0x80)、`rgba(1,2,3,0.5)` → A=128、`hsl(120,100%,50%)` → (0,255,0,255)。4 KB を超える文字列は正規表現を通さず Text とする (100 万文字で 10 ms 未満)。

- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — 正規表現は `[GeneratedRegex]`。`Directory.Build.props` は共通設定、`Directory.Packages.props` は Global Constraints の版。`nuget.config` は見本と同じ nuget.org のみ。`spikes/` がソリューションと MSBuild の props 継承に影響しないことを確認する。
- [ ] **Step 4: テスト実行 → PASS**
- [ ] **Step 5: コミット** `feat(core): add content model and classifier`

---

### Task 2: 取り込みの純粋ロジック

**Files:** Create: `Capture/ContentHasher.cs`、`Capture/CaptureFilter.cs`、`Capture/CaptureBuilder.cs`、`Search/SearchNormalizer.cs`、`Worker/OcrText.cs`。Test: 各 `*Tests.cs`

**Interfaces — Produces:**

```csharp
public sealed record ClipboardSnapshot(uint Sequence, string? SourceExePath, bool IsOwnWrite,
  bool ExcludeMonitor, bool ViewerIgnore, int? CanIncludeInHistory,
  string? Text, IReadOnlyList<string>? Files, IReadOnlyList<ClipFormat> Formats);
public static class CaptureFilter {
  public static bool ShouldIgnore(ClipboardSnapshot s, IReadOnlySet<string> excludedApps);
  public static IReadOnlySet<string> ParseExcludedApps(string? multiline); }
public static class ContentHasher {
  public static string ForText(string); public static string ForFiles(IEnumerable<string>); public static string ForBytes(ReadOnlySpan<byte>); }
public static class CaptureBuilder { public static CapturedEntry? Build(ClipboardSnapshot s, HistoryOptions o, DateTimeOffset now); }
public static class SearchNormalizer { public static string Normalize(string); }
public static class OcrText { public static string Clean(string); }
```

- [ ] **Step 1: 失敗するテスト**
  - `ShouldIgnore`: `IsOwnWrite`、`ExcludeMonitor`、`ViewerIgnore`、`CanIncludeInHistory == 0` はそれぞれ単独で true。`CanIncludeInHistory` が 1 または null なら false。除外 `keepass.exe` に対し `C:\x\KeePass.exe` は true、`C:\keepass.exe\other.exe` は false、コピー元 null は false。
  - `ParseExcludedApps("KeePass.exe\r\n\n  code.exe \nC:\\x\\Foo.EXE")` → `{keepass.exe, code.exe, foo.exe}`。null → 空。
  - `ForText`: `"a\r\nb"`、`"a\rb"`、`"a\nb"` は同値。`"a"` と `" a"` は別。閉じていないサロゲート (`"\ud800"`) で例外を出さず、`"\ud801"` と別値 (UTF-16 のバイト列でハッシュする)。
  - `ForFiles`: 大文字小文字だけの違いは同値、順序違いは別。
  - `Build`: Files と Text の両方があれば Files。Text と Png があれば Text 系で、Png も `Formats` に残る。Text が空文字で Png があれば Image。Text 1 文字の改行だけでも記録する。何もなければ null。画像のみで Png と DibV5 と Dib があれば Png を主内容とし、残りの画像形式は捨てる (優先順 Png、DibV5、Dib)。HTML / RTF は残す。
  - 上限 (`MaxFormatBytes = 10`): 11 バイトの Html は落ちて Text は残る。Text の UTF-8 が 11 バイトなら null。11 バイトの Png のみなら null。ちょうど 10 バイトは残る。
  - `SourceApp` はファイル名のみ (`Code.exe`)。
  - `Normalize("ＡＢＣ　データ―－‐–—")` → `"abc デ-タ-----"`。`ー U+30FC`、`― U+2015`、`－ U+FF0D`、`‐ U+2010`、`‑ U+2011`、`– U+2013`、`— U+2014`、`− U+2212` を `-` にする。ASCII は小文字、U+FF01〜FF5E は半角、U+3000 は空白。ほかの文字は変えない。
  - `OcrText.Clean("日 本 語 の test 文 字 列")` → `"日本語の test 文字列"`。`"hello world"` は不変。改行は残す。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — `OcrText` の和文文字は U+3000〜30FF、U+3400〜9FFF、U+F900〜FAFF、U+FF00〜FFEF。両側が和文文字のときだけ空白を除く。
- [ ] **Step 4: テスト実行 → PASS**
- [ ] **Step 5: コミット** `feat(core): add capture rules and normalizers`

---

### Task 3: 保存 (スキーマ、重複、ファイル)

**Files:** Create: `Store/StorePaths.cs`、`Store/Database.cs`、`Store/BlobStore.cs`、`Store/HistoryStore.cs`、`Store/JobQueue.cs`。Test: `HistoryStoreWriteTests.cs`、`BlobStoreTests.cs`、`TempStore.cs` (一時フォルダーの補助)、`FakeClock.cs` (`TimeProvider` を継承)

**Interfaces — Produces:**

```csharp
public sealed record StorePaths(string Root) { Db (history.db), Files (files\), Thumbs (thumbs\), Temp (tmp\); void EnsureCreated(); }
public sealed class BlobStore { // 一時名に書いてから改名。既にあれば書かない
  public BlobStore(StorePaths paths); public string Save(ReadOnlySpan<byte> data, string extension); // 戻り値は "<sha256>.<ext>"
  public string FullPath(string fileName); }
public sealed partial class HistoryStore : IDisposable {
  public HistoryStore(StorePaths paths, TimeProvider clock);
  public BlobStore Blobs { get; }
  public JobQueue Jobs { get; }
  public (long Id, bool IsNew) Upsert(CapturedEntry e);
  public EntryDetail? Get(long id);
  public string? GetMeta(string key); public void SetMeta(string key, string? value);
  public event Action? Changed; }            // 変更後に 1 回。例外は呼び出し側へ漏らさない
public sealed record Job(long EntryId, int Attempts);
public sealed class JobQueue { void Enqueue(long entryId); Job? Claim(); void Complete(long entryId); void Fail(long entryId);
  int ResetRunning(); bool HasPending(); }
```

スキーマ (`user_version = 1`):

```sql
CREATE TABLE entries(id INTEGER PRIMARY KEY, kind INTEGER NOT NULL, hash TEXT NOT NULL, preview TEXT NOT NULL,
  name TEXT, source TEXT, pinned INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL, last_copied_at INTEGER NOT NULL,
  width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0, byte_size INTEGER NOT NULL DEFAULT 0,
  char_count INTEGER NOT NULL DEFAULT 0, line_count INTEGER NOT NULL DEFAULT 0,
  image_state INTEGER NOT NULL DEFAULT 0, thumb TEXT, search TEXT NOT NULL, ocr TEXT, qr TEXT, body TEXT,
  UNIQUE(kind, hash));
CREATE INDEX ix_entries_recent ON entries(last_copied_at DESC);
CREATE TABLE formats(entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE, name TEXT NOT NULL,
  file TEXT NOT NULL, size INTEGER NOT NULL, PRIMARY KEY(entry_id, name)) WITHOUT ROWID;
CREATE TABLE jobs(entry_id INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE,
  attempts INTEGER NOT NULL DEFAULT 0, state INTEGER NOT NULL DEFAULT 0);  -- 0 pending, 1 running, 2 failed
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;
```

接続は 1 本を `Lock` で直列化する。`Pooling=False`、`journal_mode=WAL`、`foreign_keys=ON`、`busy_timeout=5000`、`cache_size=-512`。日時は Unix ミリ秒。プレビューは先頭から空白と改行を 1 つの空白に畳んだ 200 文字 (サロゲートの途中で切らない)。画像のプレビューは `画像`、Files は先頭のファイル名と `ほか N 件`。`search` は `Normalize(本文先頭 64 Ki 文字 + 名前 + ファイル名 + OCR + QR)`。

- [ ] **Step 1: 失敗するテスト**
  - 同じ `(Kind, Hash)` の 2 回目の Upsert: 件数 1、`IsNew` は false、`LastCopiedAt` が新しくなり `CreatedAt`、名前、ピン留めは残る。形式は新しいもので置き換わる (1 回目 Html + Rtf、2 回目 Html のみ → Rtf が消える)。
  - 画像の再コピーで、処理済み (`Done`) の OCR とサムネイルを保ち、ジョブを積み直さない。
  - 画像形式を含む Upsert は `ImageState = Pending` とジョブ 1 件。含まないものは `None` とジョブ 0 件。
  - `BlobStore.Save` を同じ内容で 2 回 → 同じ名前、ファイル 1 個、`tmp\` は空。
  - DB を開き直しても内容が残る。`user_version` が 2 以上の DB は `InvalidDataException`。
  - `Claim` → `Fail` を 3 回で `state = 2` となり、4 回目の `Claim` は null、`ImageState = Failed`。`ResetRunning` で running が pending に戻る。
  - 100 KB の本文でも `Get` の `Body` は全文、`Preview` は 200 文字以内。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装**
- [ ] **Step 4: テスト実行 → PASS**
- [ ] **Step 5: コミット** `feat(store): add history database and blobs`

---

### Task 4: 検索、編集、削除、保持期間、掃除

**Files:** Create: `Store/HistoryStore.Query.cs`、`Store/HistoryStore.Maintenance.cs`、`Search/LikePattern.cs`。Test: `HistoryStoreQueryTests.cs`、`HistoryStoreMaintenanceTests.cs`、`SearchPerformanceTests.cs`

**Interfaces — Produces** (`HistoryStore` のメンバー):

```csharp
public sealed record HistoryQuery(string Search, EntryKind? Kind, int Offset, int Limit);
IReadOnlyList<EntrySummary> Query(HistoryQuery q);   // ピン留めが先、各群は last_copied_at 降順、同値は id 降順
IReadOnlyList<long> IdsNewestFirst(int limit);       // ピン留めの優先なし
bool Exists(long id);
void Touch(long id); void SetPinned(long id, bool pinned); void Rename(long id, string? name);
long EditText(long id, string text);                 // 残った項目の id
void Delete(long id); int DeleteSince(DateTimeOffset since); int DeleteAll();   // 後 2 つはピン留めを残す
int Expire(DateTimeOffset cutoff); int EnforceTotalLimit(long maxBytes); int SweepFiles(TimeSpan minAge);
void CompleteImage(long id, int width, int height, string? pngFile, string? thumb, string? ocr, string? qr);
long TotalBytes();
public static class LikePattern { public static IReadOnlyList<string> Build(string search); } // Normalize → 空白分割 → エスケープ → %term%
```

- [ ] **Step 1: 失敗するテスト**
  - 検索: `%` を含む項目 `100%` と `1000` に対し `0%` は前者だけ。`a_b` は `a_b` だけで `axb` に当たらない。`\` は文字どおり。`git main` は両方を含むものだけ (順不同)。`ＡＢＣ` で `abc` に当たる。`デ-タ` で `データ` に当たる。空白だけの検索語は全件。
  - 名前、ファイル名、OCR、QR で当たる。`Rename` の後は新しい名前で当たり、古い名前では当たらない。
  - 種別フィルタ、`Offset` / `Limit` (51 件を 50 と 1 に分ける)、並び順。
  - `EditText`: 本文、種別 (`abc` → `https://a.com` で Link)、ハッシュ、文字数が変わり、形式と古い `search` が消える。既存の項目と同じ内容にすると 1 件に統合され、編集中の id が残る。名前は編集中のもの、なければ相手のもの、ピン留めはどちらかが true なら true。画像と Files には `InvalidOperationException`。
  - `DeleteSince(now - 5 分)`: `LastCopiedAt` がちょうど 5 分前は消え、5 分 1 ms 前は残る。ピン留めは残る。`Expire(cutoff)`: cutoff より古いものだけ消え、ちょうどとピン留めは残る。
  - `EnforceTotalLimit`: 合計 300、上限 250 で、最も古いピン留めなしが 1 件だけ消える。ピン留めだけで超えるときは 0 件。
  - `SweepFiles(1 時間)`: 参照のある 2 ファイルは残り、参照のない 2 時間前の 1 つは消え、直前に書いた参照なしは残る。`tmp\` も同じ規則。2 項目が同じ blob を指すとき、片方を削除しても残る。
  - 存在しない id への `Touch`、`Delete`、`Rename` は例外を出さない。
  - 性能 (仕様 2.6): 5,000 件、平均 2 KB、画像 200 件で、`Query` の初回表示と検索 (`日本語` と当たらない語) の中央値が 200 ms 未満 (Release で計測、`[Trait("Category","Performance")]`)。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — `Query` は `body` と `ocr` を読まない。1 操作は 1 トランザクション。
- [ ] **Step 4: テスト実行 → PASS** (性能は `-c Release` でも実行し実測値を報告)
- [ ] **Step 5: コミット** `feat(store): add search and maintenance`

---

### Task 5: ワーカー (PNG 変換、縮小、OCR、QR)

**Files:** Create: `Worker/WorkerRunner.cs`、`Worker/IImageProcessor.cs`、`Worker/WinRtImageProcessor.cs`、`Worker/DibCodec.cs`。Test: `WorkerRunnerTests.cs`、`DibCodecTests.cs`、`WinRtImageProcessorTests.cs`

**Interfaces — Produces:**

```csharp
public sealed record ImageResult(int Width, int Height, string? Ocr, string? Qr);
public interface IImageProcessor { // 入力は PNG または DIB の生ファイル。PNG と縮小 PNG (長辺 128 px、拡大しない) を書く
  ImageResult Process(string inputPath, bool inputIsDib, string pngOutPath, string thumbOutPath, bool ocr, bool qr); 
  void WriteDib(string pngPath, string dibOutPath); }         // BITMAPV5HEADER + 32 bpp BGRA、下から上
public sealed class WorkerRunner { public WorkerRunner(HistoryStore store, StorePaths paths, IImageProcessor processor, Func<bool> ocrEnabled);
  public int RunAll(); }                                        // 処理件数
public static class DibCodec { public static byte[] ToBmp(ReadOnlySpan<byte> dib); public static byte[] BuildV5(int w, int h, ReadOnlySpan<byte> bgraTopDown); }
```

- [ ] **Step 1: 失敗するテスト**
  - `WorkerRunner` (偽の processor): 2 件を処理して `ImageState = Done`、寸法と OCR を保存し、`jobs` が空になる。元が DIB の項目は形式が `PNG` に置き換わり、DIB の blob は参照がなくなる。1 件目が例外でも 2 件目は処理する。3 回失敗で `Failed`、項目は残る。Kind が Image 以外 (Office の画像など) は OCR と QR を求めない。処理中に項目が削除されても例外を出さない。OCR は `OcrText.Clean` を通して保存する。
  - `DibCodec.ToBmp`: 24 bpp、32 bpp `BI_BITFIELDS` (マスク 12 バイト)、8 bpp パレット (`biClrUsed = 0` は 256 色)、V5 ヘッダーで `bfOffBits` が正しい。40 バイト未満と `biSize` が不正な入力は `InvalidDataException`。
  - `WinRtImageProcessor`: テスト内で生成した 300×200 の PNG → 縮小 128×85、寸法 300×200。QR (ZXing で生成した `https://example.com/日本`) を読む。`WriteDib` → `ToBmp` → 復号で元のピクセル (透過を含む) と一致。壊れた PNG は例外。OCR エンジンがない環境では Ocr は null。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — OCR は `OcrEngine.TryCreateFromUserProfileLanguages`。`OcrEngine.MaxImageDimension` を超える画像は縮めてから渡す。書き込みは `tmp\` に書いてから改名。見本は `spikes/aot/Program.cs`。
- [ ] **Step 4: テスト実行 → PASS**
- [ ] **Step 5: コミット** `feat(worker): add image processing`

---

### Task 6: 貼り付けと連続貼り付けの状態

**Files:** Create: `Paste/*.cs`、`Sequence/SequentialPaste.cs`。Test: `PasteServiceTests.cs`、`PayloadFactoryTests.cs`、`SequentialPasteTests.cs`

**Interfaces — Produces:**

```csharp
public sealed record ClipboardPayload { string? Text; byte[]? Html; byte[]? Rtf; IReadOnlyList<string>? Files; string? PngPath; string? DibPath; }
public interface IClipboardWriter { bool Write(ClipboardPayload payload); }
public interface IWindowSystem { nint Foreground { get; } nint LastTarget { get; } uint PidOf(nint hwnd); bool IsCommandPalette(nint hwnd);
  bool IsElevatedOrUnknown(nint hwnd); bool AnyModifierDown(); bool SendCtrlV(); }
public interface IDelay { long NowMs { get; } void Sleep(int ms); }
public enum PasteOutcome { Sent, Busy, NoTarget, ClipboardFailed, FocusTimeout, Elevated, ModifiersHeld, SendFailed, SourceMissing }
public sealed class PasteService { public PasteService(IWindowSystem, IClipboardWriter, IDelay, Action<Action> runInBackground);
  // 同期部: 対象の決定と書き込み。非同期部: 待機と送信。done はちょうど 1 回呼ばれる
  public PasteOutcome? Begin(ClipboardPayload payload, bool fromHotkey, Action<PasteOutcome> done); }  // 即時失敗はその値、開始できたら null
public interface IDibProvider { string? TryCreate(string pngPath); }
public static class PayloadFactory {
  public static ClipboardPayload? Full(EntryDetail e, IDibProvider dib);       // ファイルが消えていれば欠けた形式を除き、何も残らなければ null
  public static ClipboardPayload? Plain(EntryDetail e);                          // Text 系と Files (改行区切りのパス)。画像は null
  public static ClipboardPayload? Single(EntryDetail e, string formatName, IDibProvider dib);
  public static IReadOnlyList<string> AvailableFormats(EntryDetail e); }         // "Text", "HTML", "RTF", "画像", "ファイル", "パス"
public sealed class SequentialPaste { public SequentialPaste(TimeProvider clock, Func<TimeSpan> timeout);
  public long? Peek(Func<IReadOnlyList<long>> loadIds, Func<long, bool> exists); public void Advance(); public void Reset(); }
```

- [ ] **Step 1: 失敗するテスト**
  - `PasteService` 正常: 書き込み → 前面が対象に戻る → 修飾キーなし → 50 ms → Ctrl+V 1 回 → 150 ms → `Sent`。同じプロセスの別ウィンドウが前面でも送る。
  - 送らない場合 (いずれも `SendCtrlV` 0 回): `LastTarget` が 0 (`NoTarget`、書き込みは行う)、800 ms 経っても対象が前面でない (`FocusTimeout`)、待機中に第三のウィンドウが前面 (800 ms まで待ってから `FocusTimeout`)、`IsElevatedOrUnknown` (`Elevated`)、2,000 ms 経っても修飾キーが下りている (`ModifiersHeld`)、書き込み失敗 (`ClipboardFailed`)、`SendCtrlV` が false (`SendFailed`)。修飾キーを待つ間に前面が変わったら `FocusTimeout` (送る直前に前面を再確認する)。
  - `fromHotkey`: 対象は呼び出し時の前面。前面待ちをしない。前面が CmdPal なら `NoTarget` とし書き込みもしない。
  - 実行中の 2 回目の `Begin` は `Busy`、書き込み 0 回、1 回目は影響を受けない。終わった後は開始できる。途中で例外が出ても次が開始できる。
  - `PayloadFactory`: Text + Html + Rtf の Full は 3 つ。Plain は Text のみ。Files の Plain はパスの改行区切り。処理待ちの DIB 画像は `DibPath` のみ。blob が消えた画像の Full は null、Html の blob だけ消えた Text は Text のみ。
  - `SequentialPaste`: 列 [3,2,1] で `Peek`/`Advance` を繰り返すと 3、2、1、null。`Advance` なしの `Peek` は同じ値。2 が消えていれば 3 の次は 1。始めた後に 4 が増えても列は変わらない。最後の実行からちょうど 1 分は継続、1 分 1 ms で先頭から。`Reset` で先頭から。空の履歴は null。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — テストは `runInBackground` に同期実行を、`IDelay` に仮想時刻を渡す。
- [ ] **Step 4: テスト実行 → PASS**
- [ ] **Step 5: コミット** `feat(paste): add paste and sequence logic`

---

### Task 7: Win32 層

**Files:** Create: `Interop/*.cs`、`Capture/ClipboardMonitor.cs`。Test: `DropFilesTests.cs`、`HtmlFormatTests.cs`

**Interfaces — Produces:**

```csharp
public static class DropFiles { public static byte[] Encode(IReadOnlyList<string> paths); public static IReadOnlyList<string> Decode(ReadOnlySpan<byte> data); }
public static class HtmlFormat { public static string? ExtractFragment(ReadOnlySpan<byte> cfHtml); }    // 詳細表示用。壊れていれば null
public sealed class ClipboardMonitor : IDisposable {   // 専用 STA スレッド、メッセージ専用ウィンドウ、WinEvent フック
  public ClipboardMonitor(Action onClipboardChanged, Action<nint> onForegroundChanged); public void Start(); public nint WindowHandle { get; }
  public void Post(Action action); }                   // STA スレッドで実行
public sealed class ClipboardReader { public ClipboardReader(Func<nint> owner, Func<string?> foregroundExe); public ClipboardSnapshot? Read(long maxFormatBytes); }
public sealed class ClipboardWriter : IClipboardWriter { public ClipboardWriter(Func<nint> owner); }
public sealed class WindowSystem : IWindowSystem { public void OnForegroundChanged(nint hwnd); public string? ExeOf(nint hwnd); }
```

- [ ] **Step 1: 失敗するテスト** — `DropFiles` の往復 (日本語、空白、2 件)、ANSI 版 (`fWide = 0`) の復号、途中で切れたデータは空。`HtmlFormat`: 正しい CF_HTML から断片を UTF-8 で得る、オフセットが範囲外なら null。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — 見本は `spikes/ext/SpikeExt/{Native,ForegroundTracker,ClipboardInspector,Paste}.cs`。要点:
  - 自分の目印形式は `Waigoma.ClipboardHistory.Own` (1 バイト)。書き込みのたびに付け、読み取りでは `IsOwnWrite` に反映する。
  - `OpenClipboard` は 20 ms 間隔で最大 10 回。`WM_CLIPBOARDUPDATE` の連続はシーケンス番号が変わらなければ無視する。
  - 除外の目印を先に調べ、該当したら本文も形式も読まずに返す。画像は Png、DibV5、Dib の順に 1 つだけ読む。`GlobalSize` が上限を超える形式は読まない。`CF_DIBV5` と `CF_DIB` は合成されるため、`EnumClipboardFormats` の順で先に現れる方を使う。
  - コピー元は `GetClipboardOwner`、なければ前面。`ApplicationFrameHost.exe` は子ウィンドウのうち別プロセスのものを採る。
  - 書き込み: Text は `CF_UNICODETEXT`、Html、Rtf、`CF_HDROP` と `Preferred DropEffect` = 1、`PNG`、`CF_DIBV5` (ファイルから HGLOBAL へ直接読み込む)。すべての HGLOBAL は書けなかったときに解放する。
  - 前面の追跡は、CmdPal と、枠のない `Shell_TrayWnd`、`XamlExplorerHostIslandWindow` (Alt+Tab)、`SearchHost` を覚えない。
  - 同じプロセス内のコールバックで例外を外へ出さない。
- [ ] **Step 4: テスト実行 → PASS**
- [ ] **Step 5: コミット** `feat(core): add win32 clipboard layer`

---

### Task 8: HistoryService (まとめ役)

**Files:** Create: `HistoryService.cs`、`Capture/IBackfillSource.cs`、`Capture/WindowsHistorySource.cs`。Test: `HistoryServiceTests.cs`

**Interfaces — Produces:**

```csharp
public sealed record BackfillItem(string? Text, IReadOnlyList<string>? Files, DateTimeOffset CopiedAt);
public interface IBackfillSource { IReadOnlyList<BackfillItem> ReadNewerThan(DateTimeOffset since); }
public sealed class HistoryService : IDisposable {
  public HistoryService(HistoryStore store, Func<HistoryOptions> options, Func<ClipboardSnapshot?> read, IBackfillSource backfill,
    Action startWorker, SequentialPaste sequence, TimeProvider clock);
  public void Startup();            // ResetRunning、Expire、Sweep、補完、未処理があればワーカー起動
  public void OnClipboardChanged(); // STA スレッドから
  public void Tick();               // 1 分ごと: heartbeat。1 時間ごと: Expire + EnforceTotalLimit + SweepFiles
  public bool Paused { get; set; } } // meta "paused"
```

`meta` の鍵: `paused`、`heartbeat` (Unix ms)、`last_sequence`。

- [ ] **Step 1: 失敗するテスト** (偽の read / backfill / startWorker)
  - 新しい内容 → 1 件、連続貼り付けが Reset される。自分の書き込み → 0 件、Reset されない。一時停止中 → 0 件。除外 → 0 件。`read` が null や例外 → 0 件で落ちない。
  - 画像 → `startWorker` 1 回。テキスト → 0 回。
  - 全体上限を超えた記録の後、古いものが消える。
  - `Startup` の補完: `read` の `Sequence` が `last_sequence` と同じなら拾わない。違えば拾う。標準の履歴は `heartbeat` より新しいものだけを、古い順に、その `CopiedAt` で記録する。`heartbeat` がない初回は標準の履歴を読まない。除外アプリが 1 つ以上、または一時停止中なら標準の履歴を読まない。既にある内容は増やさず、`LastCopiedAt` を古くしない。
  - 削除 → 再起動 (同じ `last_sequence`、`heartbeat` は削除より後) で復活しない。
  - `Tick`: 60 回で保守が 1 回。`Retention = null` なら `Expire` しない。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — `WindowsHistorySource` は STA スレッドで `Clipboard.GetHistoryItemsAsync`、最大 5 秒待ち、失敗は空。`StandardDataFormats.Text` と `StorageItems` だけを読む。
- [ ] **Step 4: テスト実行 → PASS**
- [ ] **Step 5: コミット** `feat(core): add history service`

---

### Task 9: 拡張の骨組み、設定、ビルド

**Files:** Create: `src/ClipboardHistory.Extension/` の `*.csproj`、`Package.appxmanifest`、`app.manifest`、`Assets/`、`Public/`、`Program.cs`、`ClipboardHistoryExtension.cs`、`HistoryCommandsProvider.cs`、`AppHost.cs`、`SettingsManager.cs`、`scripts/build.ps1`

**Interfaces — Produces:**

```csharp
internal sealed class AppHost : IDisposable { // 唯一の組み立て場所
  HistoryStore Store; HistoryService Service; PasteService Paste; SequentialPaste Sequence; Func<HistoryOptions> Options;
  IDibProvider Dib; SettingsManager Settings; PasteNotice Notice; void StartWorker(); event Action? WorkerFinished; }
internal sealed class PasteNotice { string? Current; void Set(PasteOutcome o); void Clear(); } // 文言は下の表
internal sealed class SettingsManager : JsonSettingsManager { HistoryOptions Current { get; } string DataRoot { get; } void AddExcludedApp(string exe); event Action? Changed; }
```

固定値: CLSID `7C1B6F3E-5A0D-4E0B-9B52-2F6A4C8D9E17`、Provider Id `com.waigoma.clipboardhistory`、ページ Id `com.waigoma.clipboardhistory.page`、連続貼り付け `.sequence.paste`、リセット `.sequence.reset`、一時停止 `.pause` (接頭辞は Provider Id)。パッケージ名 `Waigoma.CmdPal.ClipboardHistory`、発行者 `CN=Waigoma`、表示名 `クリップボード履歴`、版 `0.1.0.0`、exe `ClipboardHistory.Extension.exe`。

起動の形: `-RegisterProcessAsComServer` (本体)、`--worker <root> <ocr:0|1>` (WorkerRunner を動かして終了。名前付き mutex `Local\Waigoma.ClipboardHistory.Worker` を取れなければ即終了)、`--dib <png> <out>` (DIB を書いて終了)。本体の記録は mutex `Local\Waigoma.ClipboardHistory.Recorder` を取れたときだけ行う。

`PasteNotice` の文言: Elevated「管理者権限のアプリには貼り付けられません。クリップボードにコピーしました。」、FocusTimeout / NoTarget / SendFailed「貼り付け先を確認できませんでした。クリップボードにコピーしました。」、ModifiersHeld「キーが押されたままのため貼り付けませんでした。」、ClipboardFailed「クリップボードを使用できませんでした。」、SourceMissing「元のデータが見つかりません。」。Sent と Busy は出さない。

- [ ] **Step 1: csproj と manifest** — 見本は `spikes/ext/SpikeExt/SpikeExt.csproj` と `Package.appxmanifest`。差分: `PublishAot` を Release で常に有効、`Microsoft.DotNet.ILCompiler` 10.0.15 を参照、arm64 を外す、`InvariantGlobalization`、`ConcurrentGarbageCollection=false`、`System.GC.ConserveMemory=9`、manifest の `MinVersion` は 10.0.22000.0。
- [ ] **Step 2: `scripts/build.ps1`** — PATH に VS Installer を足し、`dotnet build` を Release / x64 で実行し、AOT の exe をレイアウトへ置いて、レイアウトのパスを出力する。失敗時は非 0 で終わる。
- [ ] **Step 3: Program、Extension、Provider、AppHost、SettingsManager** — 設定項目、選択肢、既定値は仕様 6.7 のとおり。保存先が空なら `ApplicationData.Current.LocalFolder`。設定ファイルは常に LocalFolder。保存先を使えないときは LocalFolder に戻してログに残す。`StartWorker` は `Environment.ProcessPath` を起動し、終了を待って `Store.Changed` と `WorkerFinished` を起こす (実行中の再要求は終了後に 1 回だけ)。最後の活動から 60 秒後に `GC.Collect(2, Aggressive, true, true)` と `SqliteConnection` の `shrink_memory` を 1 回行う。
- [ ] **Step 4: ビルド** — Run: `powershell -File scripts/build.ps1`。Expected: 終了コード 0、警告 0 (NU1903 なし)、レイアウトの exe が 12 MB 以下。
- [ ] **Step 5: モードの動作** — Run: 空のフォルダーに対し `<exe> --worker <dir> 0` と `<exe> --dib <png> <out>`。Expected: ともに終了コード 0。存在しない PNG は終了コード 1。
- [ ] **Step 6: コミット** `feat(extension): add extension host and settings`

---

### Task 10: 一覧ページ

**Files:** Create: `Pages/HistoryPage.cs`、`Pages/HistoryFilters.cs`、`Pages/EntryItemFactory.cs`、`Pages/EntryDetailsBuilder.cs`、Core に `Presentation/DateSections.cs`、`Presentation/MarkdownText.cs`。Test: `DateSectionsTests.cs`、`MarkdownTextTests.cs`

**Interfaces — Produces:**

```csharp
public static class DateSections { public static string Of(EntrySummary e, DateTimeOffset now); } // ピン留め / 今日 / 昨日 / 今週 / それ以前 (ローカル時刻、週は月曜始まり)
public static class MarkdownText { public static string Fence(string body, int maxChars); public static string Escape(string inline); }
internal sealed class HistoryPage : DynamicListPage { public HistoryPage(AppHost host, EntryItemFactory factory); public void Refresh(); }
internal sealed class EntryItemFactory { public ListItem Create(EntrySummary e); }  // Task 11 がコマンドを差し込む
```

- [ ] **Step 1: 失敗するテスト** — `DateSections`: 月曜 00:00 ちょうどの今日、日曜 23:59 の昨日、火曜から見た日曜は「それ以前」、木曜から見た火曜は「今週」、未来の日時は「今日」、ピン留めは日付によらず「ピン留め」。`Fence`: 本文に ``` があればそれより長い柵、上限で切れたら `…` を付け、サロゲートの途中で切らず、閉じていないサロゲートは U+FFFD。`Escape`: `[`、`]`、`(`、`*`、`_`、`` ` ``、`<`、`#`、`|`、改行を無害化。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — 見本は `SpikePage.cs`、`SpikeItems.cs`、`SpikeFilters.cs`。
  - 50 件ずつ。検索語かフィルタが変わったとき、または前回の `GetItems` から 30 秒以上経ったときは 50 件に戻す。`Store.Changed` は 100 ms まとめてから `RaiseItemsChanged`。60 秒使われなければ保持を手放す。
  - `Details` は getter を override し、選ばれたときに初めて `Store.Get` する。本文は 4,000 文字まで。画像は見本と同じ `file:///` の markdown (最大高 400)。項目は「種別、コピー元、日時、文字数 / 行数 または 寸法と大きさ、形式」。HTML を解釈して表示しない。
  - アイコン: 画像は縮小画像のパス (なければ記号)、種別ごとの Segoe Fluent の記号。色は `Tag` に見本の背景色を付ける。Files で存在しないものは `Tag`「見つかりません」(調べるのは表示分の先頭 1 件だけ、ネットワークパスは調べない)。
  - `PasteNotice` があれば先頭に項目として出し、選ぶと消える。履歴が空、検索結果が空のときは `EmptyContent`。
- [ ] **Step 4: テスト実行 → PASS、`scripts/build.ps1` → 警告 0**
- [ ] **Step 5: コミット** `feat(extension): add history list page`

---

### Task 11: 項目の操作

**Files:** Create: `Commands/PasteCommand.cs`、`Commands/CopyCommand.cs`、`Commands/EntryCommands.cs`、`Pages/EditPage.cs`、`Pages/RenamePage.cs`、`Pages/FormatsPage.cs`、`Pages/BulkDeletePage.cs`。Modify: `Pages/EntryItemFactory.cs`。Core に `Presentation/EntryActions.cs`、`Presentation/OpenTarget.cs`。Test: `EntryActionsTests.cs`、`OpenTargetTests.cs`

**Interfaces — Produces:**

```csharp
public enum EntryAction { Paste, Copy, PastePlain, PasteFormat, Edit, Rename, TogglePin, CopyOcr, CopyQr, Open, OpenLocation, ExcludeApp, Delete, BulkDelete, DeleteAll }
public static class EntryActions { public static IReadOnlyList<EntryAction> For(EntrySummary e, bool pasteIsPrimary, int formatCount); } // 先頭が Enter、2 番目が Ctrl+Enter
public static class OpenTarget { public static string? For(EntryDetail e); public static string? LocationFor(EntryDetail e); }
```

- [ ] **Step 1: 失敗するテスト**
  - `EntryActions`: 種別ごとに仕様 6.3 の「対象」列と一致し、順序は表の順。`pasteIsPrimary = false` で先頭が Copy、2 番目が Paste。形式が 1 つなら PasteFormat なし。コピー元 null なら ExcludeApp なし。`HasQr` が false なら CopyQr なし。
  - `OpenTarget`: Link は http / https のみ (`file://`、`javascript:` は null)、Email は `mailto:` を付ける、画像は PNG のパス、Files は先頭のパス、Text と Color は null。存在しないパスは null。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装**
  - キーは仕様 6.3。`Ctrl+.` は `(VirtualKey)0xBE`。
  - 貼り付け: `Begin` が null なら `MoveToTop` のとき `Touch` し `CommandResult.Hide()`。値が返ったら `ShowToast` で `PasteNotice` と同じ文言。`done` は Sent 以外を `Notice.Set`。`PreferPlainText` は主操作の貼り付けとコピーの両方に効く。
  - コピー: 書き込み後 `ShowToast("コピーしました")`。
  - 編集: 見本は `SpikeForm.cs`。本文は `Utf8JsonWriter` で埋め込む。64 KB を超える本文は編集画面を出さず「長すぎるため編集できません」を表示する (途中で切れた内容で上書きしないため)。保存 / 保存して貼り付け / キャンセル。改行は元の改行 (CRLF か LF) に揃えて保存する。
  - 名前の変更は空で解除。期間で削除は 5 / 15 / 30 分、1 / 24 時間を並べ、選ぶと `Confirm`。すべて削除も `Confirm`。文言に件数ではなく「ピン留めは残ります」と書く。
  - 文字のコピー: `Done` ならその場で、`Pending` か `Failed` ならジョブを積んでワーカーを動かし、`WorkerFinished` の後にコピーして `ToastStatusMessage` で知らせる。OCR が OFF でもこの操作では行う (worker に ocr=1)。文字がなければ「文字は見つかりませんでした」。
  - 開くは `Process.Start` の `UseShellExecute = true`。場所は `explorer.exe /select,"<path>"`。
  - 削除は確認なしで即時、画面は開いたまま。
- [ ] **Step 4: テスト実行 → PASS、`scripts/build.ps1` → 警告 0**
- [ ] **Step 5: コミット** `feat(extension): add entry actions`

---

### Task 12: トップレベルコマンド

**Files:** Create: `Commands/TopLevelCommands.cs`。Modify: `HistoryCommandsProvider.cs`、`AppHost.cs`。Core に `Sequence/SequentialPasteRunner.cs`。Test: `SequentialPasteRunnerTests.cs`

**Interfaces — Produces:** `public sealed class SequentialPasteRunner { public PasteOutcome? Run(); }` (`null` は開始または対象なし)

- [ ] **Step 1: 失敗するテスト** — `Sent` のときだけ `Advance`。`FocusTimeout` では同じ項目が次も出る。`Busy` は進めない。`SequenceNewline` は Text 系の Text 末尾に `\r\n` を付け (Html と Rtf は出さない)、画像と Files には付けない。`Touch` を呼ばない。データが消えた項目 (`SourceMissing`) は飛ばして次を貼る。末尾の後は何もしない。
- [ ] **Step 2: テスト実行 → FAIL**
- [ ] **Step 3: 実装** — 「クリップボード履歴」「連続貼り付け」「連続貼り付けをリセット」「記録を一時停止 / 再開」。一時停止の表題は状態で変え、`RaiseItemsChanged` で更新する。連続貼り付けは `fromHotkey: true`、戻り値は `KeepOpen`。CmdPal の一覧から選ばれたとき (前面が CmdPal) は `NoTarget` となり、何も起きないのでトーストで「ホットキーを割り当てて使ってください」と出す。
- [ ] **Step 4: テスト実行 → PASS、`scripts/build.ps1` → 警告 0**
- [ ] **Step 5: コミット** `feat(extension): add top level commands`

---

### Task 13: 署名、導入、README

**Files:** Create: `scripts/new-certificate.ps1`、`scripts/package.ps1`、`scripts/install.ps1`、`scripts/uninstall.ps1`、`README.md`

- [ ] **Step 1: `new-certificate.ps1`** — `CN=Waigoma` のコード署名証明書を `Cert:\CurrentUser\My` に作る (あれば再利用、5 年、秘密鍵はエクスポート不可)。公開部分だけを `artifacts/Waigoma.cer` に出す。信頼の登録はしない。
- [ ] **Step 2: `package.ps1`** — ビルドして MSIX を `artifacts/` に作り、`signtool` (NuGet の BuildTools 内) で SHA256 署名する。タイムスタンプ サーバーは使わない (通信しないため)。
- [ ] **Step 3: `install.ps1` / `uninstall.ps1`** — `Add-AppxPackage` / `Remove-AppxPackage`。信頼されていない証明書では、README の手順を示して終了コード 1。アンインストールは、既定の保存先の履歴も消えることを表示し、`-Force` がなければ止まる。
- [ ] **Step 4: README** — 目的、導入 (証明書の登録は管理者の PowerShell で依頼者が行う 1 行を明記)、Alt+V と連続貼り付けの割り当て、CmdPal の自動起動、設定、保存場所とプライバシー、できないこと (仕様 3 章)、更新と削除、開発 (ビルド、テスト)、開発者モードは OFF に戻せること。
- [ ] **Step 5: 検証** — Run: `powershell -File scripts/package.ps1`。Expected: 署名済み MSIX が 1 つ、`Get-AuthenticodeSignature` の発行者が `CN=Waigoma`、`git status` に `artifacts/` と `.pfx` が出ない。
- [ ] **Step 6: コミット** `chore(release): add packaging scripts and docs`

---

### Task 14: 実機検証 (main agent)

- [ ] 試作を外し (`Remove-AppxPackage Waigoma.CmdPal.Spike`)、開発者モードでレイアウトを登録する。
- [ ] 仕様 6 章の操作を一通り確かめる。特に、VS Code、Windows Terminal、ブラウザ、管理者権限のアプリへの貼り付け、Ctrl+X などのキー、画像の表示、フィルタ、編集、確認画面、除外の目印 (パスワード管理ソフト)。
- [ ] 5,000 件 (画像 200 件) を入れた保存先で、操作後 5 分の専有メモリ (30 MB 以下) と一覧の応答 (200 ms 以内) を測る。画像 10 枚のコピー後も測る。
- [ ] whole-branch review (sonnet) の指摘を直し、署名版を導入する (証明書の登録は依頼者)。
- [ ] `spikes/` を削除する。
