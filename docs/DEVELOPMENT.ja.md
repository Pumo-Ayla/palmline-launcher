# Series Relay Launcher 開発者ガイド

[English](DEVELOPMENT.md) · [简体中文](DEVELOPMENT.zh-CN.md) · [繁體中文](DEVELOPMENT.zh-TW.md) · 日本語 · [한국어](DEVELOPMENT.ko.md) · [利用ガイド](GUIDE.ja.md)

## v1.0.4 の処理境界

WPF はインスタンス ID、表示、入力設定とユーザーの確定操作を管理します。転送ヘルパーはメタデータ検査・取得・展開、Python ワーカーは実行環境の準備とログの正規化を担当します。インシデントを追うときは、表示名に加えて TitleId、エンジン ID、コミット、実行ディレクトリを記録してください。[WPF](../Source/GTA6Launcher/DesktopLauncher.cs) · [ワーカー](../Source/XCL-VIctory-Lab/desktop/session.py)

## インポートと永続化

1. `Address` は、選択したディレクトリと直下の `GameData` のマニフェスト、または同じ階層にあるアーカイブ先頭巻を確認します。マグネットの場合はファイル一覧で判定します。
2. `Contents` で EXE の有無を確認し、必要なら `External` で別の EXE を選びます。`Archive` で方式と出力先を記録します。選択が変わった時点で既存の検査結果を失効させます。
3. 展開済みならマニフェストに宣言された相対パス、AMD64 PE、対象ビルドの四つの RPF ヘッダーを調べます。保留中の入手・展開は `InstallPlan` と必要な torrent メタデータとして保存します。
4. ライブラリからインストールを開始すると、独立した EXE、次に本体を処理し、成功した段階の出力を記録します。最終検査後もインスタンス ID を引き継ぎます。[ソースの検査](../Source/GTA6Launcher/ImportSource.cs) · [インストール計画](../Source/GTA6Launcher/DeferredInstall.cs)

## 実績 ID と進捗

`achievement_record` は XUser の `local achievement update` から JSON を読み、1～256 文字の文字列 ID と 0～100 の整数百分率を受理します。真偽値、範囲外、壊れた JSON は進捗になりません。タイムゾーン付き時刻を保持し、時刻がなければ UTC を使用します。`forward_runtime_logs` は正規化した行を `LOG` で WPF に送り、同じ行を `runs/<run>/achievements.log` に追記します。

```text
2026-01-01T12:00:00Z [CGBJ achievement] local-progress {"achievementId":"1","percentComplete":42}
2026-01-01T12:01:00Z [CGBJ achievement] unlocked achievementId=1
```

上記は合成例です。`query` と `progress-update-request` は診断用です。`AchievementJournal.ReadHistory` は `desktop/play-history/<instance>/Desktop-*.time` から対象実行を解決し、ID ごとの最大値と最初の達成時刻を再構成します。42% の後に 12% が届いても表示は 42% です。明示的な解放は 100% として扱い、同じ 100% の再送で通知は増えません。

ライブ表示は既知 ID のみを受理します。既存の 55 行の TSV は、ID、ポイント、5 言語の名前と同順の説明の 12 列です。原始ログ、正規化ログ、インスタンスと実行の対応、辞書の順に確認します。[解析](../Source/GTA6Launcher/AchievementJournal.cs) · [辞書](../Source/GTA6Launcher/Assets/GtaViAchievements.tsv) · [通知と順序](../Source/GTA6Launcher/AchievementToast.cs)

## キー割り当てと HUD の再描画

カスタム割り当てのエディターには 18 項目があり、`PAD=` は先頭 16 個の仮想キーコードを運びます。Share と Guide はこのフィールドの外です。内蔵プリセットでは F6 で徒歩・運転を切り替え、同じトリガーでも動作の文脈から表示キーを決定します。`PROMPTS=` は ASCII の `INPUT_AIM=RMB` などを改行区切りにし、Base64 化した値です。

CGBJ、TitleId `7952987A`、キーボードモード、設定済みスロット、表示オプションがそろった時に準備します。`replace_labels` は対応する文字列トークンを書き換え、後続項目の位置を維持します。`replace_button_glyphs` は Scaleform シンボルを操作に対応付け、外枠・内側・キートップのベクター形状を構成します。`create_copy` は読み取り専用で元の `data.rpf` を開き、セッション用コピーに適用します。HUD アイコンがゼロなら準備失敗、文字列がゼロならアイコンのみ成功の場合があります。`READY` 後に割り当てを変えたテストでは、古い表示のままの起動を検出します。[操作表の生成](../Source/GTA6Launcher/GameKeyPrompts.cs) · [リソース処理](../Source/XCL-VIctory-Lab/desktop/key_prompts.py)

## プリロード、保存、更新

`PRELOADING` は準備開始、`COUNT` はシェーダー数、`PROGRESS` は管線の処理数、`READY` は起動待ちです。`DEVICE_READY` と最初のフレームを示す `PLAYING` を分けて記録します。インストール・準備開始・準備完了・起動の通知は実績キューとは別に管理し、同じモニターの表示領域を調整します。[通知](../Source/GTA6Launcher/LauncherNotice.cs)

VIctory-NVIDIA は `preload.env` と対応する F1 の値を統合してネイティブプリロードを実行します。`prewarm.log` のシェーダー数とパイプラインライブラリ処理数を別々に比較します。コンポーネント修復では同梱インデックスのパス・長さ・SHA-256 で復元内容を確認します。セーブは `GameSaves/<instance>`、実績は実行履歴、インスタンス定義は `desktop/game-instances.xml` です。[コンポーネント](../Source/GTA6Launcher/ComponentLibrary.cs) · [セーブ](../Source/GTA6Launcher/LocalSaveLibrary.cs)

リポジトリ直下で同一版 ZIP から依存ファイルを復元し、ビルド・回帰テストを行います。

```powershell
py -3 prepare_inputs.py Updates/Launcher-1.0.4.zip --portable
dotnet build Source/GTA6Launcher/GTA6Launcher.csproj -c Release
py -3 -m unittest discover -s Source/GTA6Launcher/Tools/tests -p test_cgbj_log.py
py -3 Source/GTA6Launcher/Tools/tests/test_import_archive.py
```

リリース時は `history.json` に分類を追加し、完全版とエンジン ZIP、各チェックサム、更新ヘルパーと設定の 6 添付を検証します。1.0.1 → 1.0.4 は重要 1 件・通常 2 件、1.0.3 → 1.0.4 は通常 1 件です。更新器は管理対象のファイルをステージングして置換前にトランザクションを記録します。中断時はバックアップを使って回復します。[ビルド・公開手順](DEVELOPMENT.md#ship-a-full-update) · [トランザクション](../Source/GTA6Launcher/Updater/PackageInstall.cs)
