# Using Kokoro-FastAPI (`ttsEngine: "openai"`)

chatter-agent can synthesize speech with [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M)
(Apache-2.0) through [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI), which exposes an
OpenAI-compatible API. The client is `core/src/tts/openaiClient.ts`; the server launches the engine
via `resolveKokoroSpawn` in `core/src/server/engineProcess.ts`
([#106](https://github.com/schwarz9791/chatter-agent/issues/106)).

## When to use it

**For Japanese, AivisSpeech (the default) is recommended.** Kokoro is meant for other languages.

`ttsEngine` can be switched at runtime with `PATCH /v1/config {"ttsEngine": "openai"}` (loopback
only); the server re-runs its engine launch decision and stops only an engine it launched itself.
The settings-panel UI for it comes with ChatterAgent
([#148](https://github.com/schwarz9791/chatter-agent/issues/148)). Editing `config.json` directly
changes where speech is synthesized from the next sentence, but the launch decision is only
re-run on restart.

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
  "kokoroDir": "/absolute/path/to/Kokoro-FastAPI"
}
```

| Key | Meaning |
|---|---|
| `ttsEngine` | `"openai"`. Use `kokoroBaseUrl` / `kokoroVoiceId` and talk to an OpenAI-compatible API |
| `kokoroBaseUrl` | The origin of Kokoro-FastAPI. **Do not include `/v1`** (the client appends the path; a trailing `/v1` is normalized away if you do). Defaults to `http://127.0.0.1:8880` — omit it unless Kokoro-FastAPI runs elsewhere. Not writable through the control API |
| `kokoroVoiceId` | The voice ID (e.g. `af_heart`). Defaults to `af_heart` — omit it, or pick another from `GET /v1/speakers` (chatter-agent's control API) or the voice list in the settings panel. Composite specs such as `af_bella+af_sky` or weighted ones like `af_bella(2)+af_sky(1)` are accepted |
| `kokoroDir` | Absolute path to the clone above (`~/...` and relative paths are also accepted and get expanded/resolved). The server launches `uv run --no-sync uvicorn …` from here. Left empty, the server does not launch Kokoro |

`ttsBaseUrl` / `ttsSpeakerId` / `ttsSpawnCommand` / `ttsSpawnArgs` are for AivisSpeech only and are not
used by this engine (Kokoro is launched only from `kokoroDir`).

**Migrating from an earlier setup** that put Kokoro values in `ttsBaseUrl` / `ttsSpeakerId`: move them to
`kokoroBaseUrl` / `kokoroVoiceId`. A non-numeric `ttsSpeakerId` such as `af_heart` is now rejected
and falls back to the AivisSpeech default with a warning.

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
  even though `kokoroBaseUrl` points at loopback
- ★ **Docker Desktop on Mac cannot use the GPU (MPS).** The container runs on the CPU and is
  slower than the native `uv venv` setup
- In this setup `chatter-agent-server` does not start the container (`kokoroDir` only applies to
  launching a local clone with `uv`). Start and stop the container yourself; in `config.json`,
  setting `ttsEngine: "openai"` is enough (leave `kokoroDir` empty; `kokoroBaseUrl` / `kokoroVoiceId`
  only need to be set if you're not using the defaults above)
