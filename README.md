# Showcase Image Creator

コレクションのカタログ／ショーケース用画像を作る WPF アプリです。商品写真の仕上げをまとめて行います。処理内容は CLI 版
`finish_image.py` と同じで、背景除去 → 透明余白のトリミング → 可逆出力
を、複数ファイルに対して一括で行います。

## 使い方

- ドロップエリアへ画像ファイルをドロップするか、エリアをクリックして
  ファイルを選択します（複数選択可。フォルダをドロップすると、中の
  画像ファイルを読み込みます）
- 背景除去モデル、出力形式、出力先、接尾辞を設定して「実行」
- 出力先は「入力と同じフォルダ」か「指定フォルダ」から選べます。
  同名ファイルがある場合はスキップします（「同名ファイルを上書き」
  チェックで上書きに変更可）

## 入力形式

PNG / JPEG / WebP / BMP / GIF / TIFF / ICO / AVIF / HEIC / JPEG XL など
（デコードは SkiaSharp に依存します。環境によって読めない形式は、
そのファイルだけエラーとして表示されます）

## 出力形式（すべて可逆）

- PNG（可逆）
- WebP（可逆）
- JPEG XL（可逆）― 別途 jxl.dll が必要です（下記）

## 背景除去モデル

rembg（danielgatis/rembg）と同じ ONNX モデルを使い、前処理
（リサイズ → 画像の最大値で割る → チャンネルごとの正規化）も rembg と
同一にしてあります（rembg 本体の出力との一致を検証済み）。

- isnet-general-use（高品質・重め）
- u2net（標準）
- u2netp（高速・軽量）

初回実行時に、選択したモデルを
`%LOCALAPPDATA%\ShowcaseImageFinisher\models` へ自動ダウンロードします
（MD5 検証付き）。

## JPEG XL を使う場合

libjxl 公式リリース（0.11 以降）の Windows 用バイナリから
`jxl.dll` と `jxl_threads.dll` を取り出し、このアプリの exe と
同じフォルダに置いてください。無い場合は JPEG XL だけが
エラーになり、PNG / WebP はそのまま使えます。

## ビルド

Visual Studio 2022 と .NET 8 SDK が必要です。
`ShowcaseImageFinisher.sln` を開いてビルドしてください。
実行には .NET 8 Desktop Runtime が必要です
（フレームワーク依存の構成です）。

使用ライブラリ: ONNX Runtime、SkiaSharp、libjxl（任意）
