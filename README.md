# DJI Osmo Action Viewer (Windows)

Windows 版 DJI Osmo Action Viewer。macOS 版 (`../dji-osmo-action-viewer`) と
同じ機能・同じメタデータ形式を持ち、動画フォルダを共有すれば両 OS 間で
マーカー・タイトル・ロケーションが相互に同期されます。

## メタデータ互換

メタデータは動画フォルダ直下の
`.osmo-action-viewer-metadata.sqlite` に保存されます。Mac 版も同じファイルを
参照するため、クラウド同期された動画フォルダをそのまま両 OS で開くだけで、
追加の同期処理なしにマーカー等が引き継がれます。

**データ保護**:

- メタデータは動画フォルダ側にあり、アプリの再インストールやアップグレードで
  失われません。
- 旧 `%APPDATA%/OsmoActionViewer/metadata.sqlite` と
  `.osmo-action-viewer-metadata.json` は起動時に 1 回だけ新 DB にマージされ、
  **削除されません**（ダウングレード時の保険）。

## ビルド

```
dotnet build
dotnet run --project OsmoActionViewer
```

### MSIX パッケージ

```
export MSIX_SIGN_PFX_PATH=/absolute/path/to/your-codesign-certificate.pfx
export MSIX_SIGN_PFX_PASSWORD='your-password'
./scripts/build_msix.sh 0.2.1.0
```

生成物:

- `dist/msix/DJI-Osmo-Action-Viewer-<version>.msix`

本番用のコード署名証明書 (`.pfx`) で署名します。`AppxManifest.xml` の
`Publisher` と、証明書 Subject は一致している必要があります。

任意:

```
export MSIX_TIMESTAMP_URL=http://timestamp.digicert.com
```

### FFmpeg の同梱

`OsmoActionViewer/ffmpeg/` に `ffmpeg.exe` と `ffprobe.exe` を配置して
ください（LGPL ビルドを推奨）。csproj により出力ディレクトリへコピーされます。

## 機能

- 動画一覧（DJI ファイル名パターンの自動グルーピング、サブフォルダ単位のセクション）
- 再生・シーク（←/→ 10秒、Space 再生/停止）
- マーカー追加/削除、現在時刻/任意秒の両対応
- タイトル・ロケーション・Google Maps URL 編集
- マルチ選択削除（ごみ箱送り）
- ハイライト書き出し（マーカー±0→+N 秒、ffmpeg + drawtext でタイトル焼き込み）
- 区間クリップ書き出し
