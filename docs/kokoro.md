# Kokoro-FastAPI を使う（`ttsEngine: "openai"`）

[Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M)（Apache-2.0）を
[Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) 経由で OpenAI 互換 API として叩く。
`core/src/tts/openaiClient.ts` が実装、起動は `core/src/server/engineProcess.ts` の
`resolveKokoroSpawn`（[#106](https://github.com/schwarz9791/chatter-agent/issues/106)）。

## 位置づけ

**日本語は AivisSpeech を推奨する。** Kokoro はそれ以外の言語向け —— 要約・感情判定・文分割が
日本語前提なので、英語の発言を要約させると日本語になって返る
（[#147](https://github.com/schwarz9791/chatter-agent/issues/147)）。

設定パネルから `ttsEngine` を切り替える UI はまだ無い
（[#148](https://github.com/schwarz9791/chatter-agent/issues/148)）。`ttsEngine` は制御 API から
書けない区分（再起動まで反映されない。→ [`protocol.md`](./protocol.md)「書けないキーは3種類ある」）
なので、切り替えは `config.json` を直接編集するか環境変数で行い、サーバーを再起動する。

## 導入（macOS / Apple Silicon）

前提: [mise](https://mise.jdx.dev/)、[uv](https://docs.astral.sh/uv/)、Homebrew、git。

```bash
# 1. espeak-ng（英語の未知語の読みに使う。無いと起動の途中で落ちる）
brew install espeak-ng

# 2. Kokoro-FastAPI を取ってくる
git clone https://github.com/remsky/Kokoro-FastAPI.git
cd Kokoro-FastAPI

# 3. Python を 3.12 に固定する（3.13 だと依存が入らない）
mise use python@3.12

# 4. その Python で venv を作る
uv venv --python "$(mise which python)"

# 5. 一度だけ手で起動して、依存とモデルを取得する
HOST=127.0.0.1 PORT=8880 \
ESPEAK_DATA_PATH="$(brew --prefix espeak-ng)/share/espeak-ng-data" \
  ./start-gpu_mac.sh
```

起動できたら（`GET http://127.0.0.1:8880/v1/audio/voices` が声の一覧を返せば OK）、
このプロセスは Ctrl-C で止めてよい。**以後は `chatter-agent-server` がこの clone を直接起こす**
（下の「chatter-agent との接続」）ので、`start-gpu_mac.sh` を再び使うことは無い。

★ **日本語の声も使う場合だけ**、UniDic 辞書を追加で取得する（英語だけなら不要。ディスクを
それなりに使うので、要らないなら省く）。

```bash
uv run --no-sync python -m unidic download
```

日本語の声は、この辞書が無いと**本文の言語によらず**エラーになる（英語の声は辞書に関係なく動く）。
踏んだ詳細は [`knowledge/core.md`](./knowledge/core.md)「Kokoro を起こす」。

## chatter-agent との接続

`~/.config/chatter-agent/config.json` に足す。

```json
{
  "ttsEngine": "openai",
  "ttsBaseUrl": "http://127.0.0.1:8880",
  "ttsSpeakerId": "af_heart",
  "kokoroDir": "/absolute/path/to/Kokoro-FastAPI"
}
```

| キー | 意味 |
|---|---|
| `ttsEngine` | `"openai"`。`ttsBaseUrl` を OpenAI 互換 API として解釈する |
| `ttsBaseUrl` | Kokoro-FastAPI の origin。**`/v1` を含めない**（パスはクライアントが足す） |
| `ttsSpeakerId` | 声の ID（例: `af_heart`）。`GET /v1/speakers`（chatter-agent の制御 API）か設定パネルの話者一覧で選べる |
| `kokoroDir` | 上で clone したディレクトリの絶対パス。`ttsSpawnCommand` が空のとき、ここから `uv run --no-sync uvicorn …` で server が起こす。空のままなら起こさない |

書き換えたら `chatter-agent-server` を再起動する。エンジンが起きていなければ、疎通確認の失敗を
きっかけに server 側が起こす（`ttsSpawn: true`。既定）。起動には時間がかかるので、その間の
`GET /audio/…` は `503` になり、クライアントが取り直す。

## Docker で動かす代替

Python 環境を用意したくない場合は、Kokoro-FastAPI の README にある Docker イメージでも動く。
イメージ名やタグは変わりうるので、[Kokoro-FastAPI の README](https://github.com/remsky/Kokoro-FastAPI)
を見て決め打ちしないこと。

```bash
docker run --rm -p 127.0.0.1:8880:8880 <Kokoro-FastAPI の README にあるイメージ名>
```

- ★★ **`-p 127.0.0.1:8880:8880` のように、ホスト側のアドレスを必ず明示すること。**
  `-p 8880:8880` だけだと全インターフェースで公開され、`ttsBaseUrl` がループバックのつもりで
  LAN に音声合成 API を晒すことになる
- ★ **Mac の Docker Desktop からは GPU（MPS）を使えない。** コンテナは CPU 実行になり、
  ネイティブの `uv venv` 実行より遅くなる
- この経路では `chatter-agent-server` はコンテナを起こさない（`kokoroDir` はローカルの
  clone を `uv` で起こす経路にしか効かない）。コンテナの起動・停止は自分で行い、
  `config.json` 側は `ttsEngine` / `ttsBaseUrl` / `ttsSpeakerId` だけ設定すればよい
  （`kokoroDir` は空のままでよい）
