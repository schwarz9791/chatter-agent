# chatter-agent-app

[English](./README.md) | **日本語**

ChatterAgent（Tauri）。メニューバー常駐でサーバーを起動・停止し、設定パネルを持ちます。

全体像（仕組み・現在の状態・導入）は[ルートの README](../README-ja.md)を見てください。

## ビルド

**コマンドはリポジトリのルートから実行します。**

### 前提

- **Node 24.11 以上** —— [ルートの README](../README-ja.md) の「ビルド」の前提と同じです
- **Bun**（`mise.toml` で固定しています）—— ChatterAgent をビルドするとき（core を1本の実行ファイルにして同梱します）
- **Rust**（`chatter-agent-app/mise.toml` で固定しています）—— ChatterAgent をビルドするとき

### ChatterAgent（macOS）

```bash
(cd core && npm install)
cd chatter-agent-app
npm install
npm run build      # core を Bun で compile → build/ChatterAgent.app
```

サーバーの起動・停止と設定パネルを受け持つメニューバー常駐アプリです。サーバーとプレーヤーはアプリに同梱されます。使い方は「実行」の「サーバー」にあります。

## 実行

**コマンドはリポジトリのルートから実行します。**

### サーバー

macOS では、メニューバー常駐アプリ **ChatterAgent** を開くとサーバーが起動します（Releases のもの、または「ビルド」で作った
`chatter-agent-app/build/ChatterAgent.app`）。同梱のサーバーをログインシェルと同じ環境で起こし、起動・停止・再起動も任せられます。
メニューの「設定…」から、サーバーの設定とマスコットの設定（モデル・大きさ・音量・モーションなど）を変えられます。マスコットを右クリックするか、Finder などから ChatterAgent を開き直しても、設定窓が前面に出ます。

ターミナルから手で起こすこともできます。サーバーは同時に1つしか動かないので、先に ChatterAgent のメニュー「終了」を選びます
（ChatterAgent は「外で動いている」と表示するだけで、手を出しません）。

```bash
/Applications/ChatterAgent.app/Contents/MacOS/chatter-agent server
```

メニューの「マスコットを表示 / 隠す」と「ミュート」で、繋ぐクライアントも切り替わります。表示中は macOS アプリ、
隠している間は CLI プレーヤーで声だけを鳴らし、隠してミュートしているときは何も繋ぎません（2つを同時に繋ぐと
二重に鳴るので、ChatterAgent がどちらか一方だけを動かします）。macOS アプリは `/Applications` →
`~/Applications` → `chatter-mascot/Build` の順に `ChatterMascot.app` を探します。ショートカット
（既定は ⌃⌥M がミュート、⌃⌥H が表示切替。登録するのは ChatterAgent だけです）でも操作できます。詳細は [`docs/agent.md`](../docs/agent.md)。

## 開発

**コマンドはリポジトリのルートから実行します。**

```bash
cd chatter-agent-app/src-tauri
cargo fmt --check && cargo clippy --all-targets -- -D warnings && cargo test
cd .. && npm run build:web && npm test   # 設定窓（tsc + vite build、node --test）
npm run build            # → ChatterAgent.app
```
