# Using Kokoro-FastAPI (`ttsEngine: "openai"`)

chatter-agent can synthesize speech with [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M)
(Apache-2.0) through [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI), which exposes an
OpenAI-compatible API. The client is `core/src/tts/openaiClient.ts`; the server launches the engine
via `resolveKokoroSpawn` in `core/src/server/engineProcess.ts`
([#106](https://github.com/schwarz9791/chatter-agent/issues/106)).

## When to use it

**For Japanese, AivisSpeech (the default) is recommended.** Kokoro is meant for other languages.

There is no settings-panel UI for switching `ttsEngine` yet
([#148](https://github.com/schwarz9791/chatter-agent/issues/148)). `ttsEngine` cannot be written
through the control API (it only takes effect after a restart; see "書けないキーは3種類ある" in
[`protocol.md`](./protocol.md)), so switch it by editing `config.json` or via the environment
variable, then restart the server.

## Setup (macOS / Apple Silicon)

Prerequisites: [mise](https://mise.jdx.dev/), [uv](https://docs.astral.sh/uv/), Homebrew, git.

```bash
# 1. espeak-ng (used to read unknown English words; without it the engine crashes during startup)
brew install espeak-ng

# 2. Get Kokoro-FastAPI
git clone https://github.com/remsky/Kokoro-FastAPI.git
cd Kokoro-FastAPI

# 3. Pin Python to 3.12 (dependencies do not install on 3.13)
mise use python@3.12

# 4. Create a venv with that Python
uv venv --python "$(mise which python)"

# 5. Start it once by hand to install dependencies and download the model
HOST=127.0.0.1 PORT=8880 \
ESPEAK_DATA_PATH="$(brew --prefix espeak-ng)/share/espeak-ng-data" \
  ./start-gpu_mac.sh
```

Once it is up (`GET http://127.0.0.1:8880/v1/audio/voices` returns the voice list), stop it with
Ctrl-C. **From then on `chatter-agent-server` launches this clone directly** (see "Connecting to
chatter-agent" below), so you will not need `start-gpu_mac.sh` again.

★ **Only if you also want the Japanese voices**, download the UniDic dictionary (not needed for
English; it takes a fair amount of disk space, so skip it if you don't need it).

```bash
uv run --no-sync python -m unidic download
```

Without this dictionary the Japanese voices fail **regardless of the input language** (the English
voices work either way). Details are in "Kokoro を起こす" in
[`knowledge/core.md`](./knowledge/core.md).

## Connecting to chatter-agent

Add the following to `~/.config/chatter-agent/config.json`:

```json
{
  "ttsEngine": "openai",
  "ttsBaseUrl": "http://127.0.0.1:8880",
  "ttsSpeakerId": "af_heart",
  "kokoroDir": "/absolute/path/to/Kokoro-FastAPI"
}
```

| Key | Meaning |
|---|---|
| `ttsEngine` | `"openai"`. Treat `ttsBaseUrl` as an OpenAI-compatible API |
| `ttsBaseUrl` | The origin of Kokoro-FastAPI. **Do not include `/v1`** (the client appends the path) |
| `ttsSpeakerId` | The voice ID (e.g. `af_heart`). Pick one from `GET /v1/speakers` (chatter-agent's control API) or the voice list in the settings panel |
| `kokoroDir` | Absolute path to the clone above. When `ttsSpawnCommand` is empty, the server launches `uv run --no-sync uvicorn …` from here. Left empty, the server does not launch Kokoro |

Restart `chatter-agent-server` after editing. If the engine is not running, the server launches it
when the startup reachability check fails (`ttsSpawn: true`, the default). Startup takes a while;
until then `GET /audio/…` returns `503` and clients retry.

## Alternative: running it in Docker

If you'd rather not set up a Python environment, the Docker images listed in the Kokoro-FastAPI
README work as well. Image names and tags may change, so take them from the
[Kokoro-FastAPI README](https://github.com/remsky/Kokoro-FastAPI) instead of hard-coding them.

```bash
docker run --rm -p 127.0.0.1:8880:8880 <image name from the Kokoro-FastAPI README>
```

- ★★ **Always bind the host side explicitly, as in `-p 127.0.0.1:8880:8880`.** With just
  `-p 8880:8880` the port is published on every interface, exposing the speech API to your LAN
  even though `ttsBaseUrl` points at loopback
- ★ **Docker Desktop on Mac cannot use the GPU (MPS).** The container runs on the CPU and is
  slower than the native `uv venv` setup
- In this setup `chatter-agent-server` does not start the container (`kokoroDir` only applies to
  launching a local clone with `uv`). Start and stop the container yourself; in `config.json`,
  setting `ttsEngine` / `ttsBaseUrl` / `ttsSpeakerId` is enough (leave `kokoroDir` empty)
