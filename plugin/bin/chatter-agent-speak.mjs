#!/usr/bin/env node
/**
 * chatter-agent
 * Copyright 2026 Masaki Matsumura
 * Licensed under the Apache License, Version 2.0.
 *
 * This bundle includes software developed as part of CC Mascot
 * (https://github.com/kazakago/cc-mascot), Copyright 2026 kazakago,
 * licensed under the Apache License, Version 2.0:
 *
 *   electron/filters/textFilter.ts @ 46f7def
 *   electron/services/ruleBasedEmotionClassifier.ts @ 46f7def
 *
 * Modified for chatter-agent. See NOTICE and docs/origin.md.
 */
import * as fs from "fs";
import * as os from "os";
import * as path from "path";

//#region src/core/paths.ts
/**
* ランタイムのパス解決。
*
* ★ 規則を単純に保つこと。plugin の bash hook が**同じ spool パスを自力で組み立てる**ため、
*   `${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/spool` の一行で書ける以上のことをしない。
*   条件分岐や環境変数を増やすと、bash 側と Node 側の実装が静かにズレる。
*/
function currentPathEnv() {
	return {
		platform: process.platform,
		homedir: os.homedir(),
		env: process.env
	};
}
function xdgConfigHome(e) {
	return e.env.XDG_CONFIG_HOME || path.join(e.homedir, ".config");
}
function appData(e) {
	return e.env.APPDATA || path.join(e.homedir, "AppData", "Roaming");
}
/**
* chatter-agent のランタイムルート。設定・spool・発話ログ・ロックをすべてこの下に置く。
* 散らばらせないのは、plugin の bash が辿れる場所を1箇所に絞るため。
*/
function getRuntimeDir(e = currentPathEnv()) {
	const base = e.platform === "win32" ? appData(e) : xdgConfigHome(e);
	return path.join(base, "chatter-agent");
}
/** 設定ファイル。config より先に必要なので環境変数の解決もここで行う */
function getConfigFilePath(e = currentPathEnv()) {
	return e.env.CHATTER_AGENT_CONFIG || path.join(getRuntimeDir(e), "config.json");
}
/** hook が payload を落とす場所。ワーカーが処理し終えたら削除する */
function getSpoolDir(e = currentPathEnv()) {
	return path.join(getRuntimeDir(e), "spool");
}
/**
* 発話の**記録**。1文1行で、消さずに残す。
*
* ★ 配信はここを読まない（→ `getSpeechQueueDir`）。誰も tail しないので、
*   ローテートの正しさが要求されない。
*/
function getSpeechLogPath(e = currentPathEnv()) {
	return path.join(getRuntimeDir(e), "speech.jsonl");
}
/** 記録の退避先。`speech.jsonl` → `speech.1.jsonl`。1世代だけ持つ */
function getSpeechLogBackupPath(basePath) {
	const dir = path.dirname(basePath);
	const ext = path.extname(basePath);
	const stem = path.basename(basePath, ext);
	return path.join(dir, `${stem}.1${ext}`);
}
/**
* 発話の**配信キュー**。1文1ファイルで、ファイル名が `seq`。
*
* CLI が書き、server が読んで配信し、クライアントの ack で消える。
* 記録と分けてあるのは、配信側は消えてよく、記録側は残したいため。
*/
function getSpeechQueueDir(e = currentPathEnv()) {
	return path.join(getRuntimeDir(e), "speech");
}
/**
* 次に採番する seq を持つ。
* seq を行番号から導かないのは、ローテートを跨いで連番を維持するため（設計書 §6）。
*/
function getSpeechStatePath(e = currentPathEnv()) {
	return path.join(getRuntimeDir(e), "speech.state.json");
}
/** ワーカー側の状態（要約セッションの除外、応答待ちの prompt_id 重複抑制） */
function getWorkerStatePath(e = currentPathEnv()) {
	return path.join(getRuntimeDir(e), "speak.state.json");
}
/**
* 単一ワーカーのロック。**ディレクトリ**として作る（mkdir が原子的なため）。
* CLI に npm 依存を持たせられないので、ロックライブラリは使わない。
*/
function getLockDir(e = currentPathEnv()) {
	return path.join(getRuntimeDir(e), "speak.lock");
}

//#endregion
//#region src/core/config.ts
/**
* 設定ストア。環境変数 > config.json > 既定値。
*
* 守る性質:
*
* - `SPECS` の `satisfies` で全キーの網羅を型で担保する
* - `mtime`+`size` のスタンプが変わったときだけ読み直す（参照時に stat するだけ。watch はしない）
* - 壊れた JSON では直前の値を維持する（書き込み途中を読んだ瞬間に挙動が飛ばないように）
* - 不正値・未知キーは警告して既定値で動き続ける（**throw しない**）
*
* 上流にあった要約関連のキー（aiSummary*）は summarizer を移植していないので落としてある。
* summarizer を入れるときに戻す。
*/
function createDefaultConfig() {
	return {
		port: 8570,
		host: "0.0.0.0",
		speakPrompts: true,
		speechLogMaxBytes: 5242880,
		speechQueueMaxEntries: 500,
		spoolMaxAgeHours: 6,
		allowedOrigins: [],
		ttsBaseUrl: "http://127.0.0.1:10101",
		ttsSpeakerId: 888753760,
		synthesisLookahead: 3,
		synthesisTimeoutMs: 3e4,
		playerCommand: "afplay",
		playerArgs: ["{file}"],
		playerServerUrl: "",
		speechMaxAgeMs: 0
	};
}
const TRUTHY = [
	"1",
	"true",
	"yes",
	"on"
];
const FALSY = [
	"0",
	"false",
	"no",
	"off"
];
const parseBoolean = (raw) => {
	if (typeof raw === "boolean") return raw;
	if (typeof raw !== "string") return void 0;
	const v = raw.trim().toLowerCase();
	if (TRUTHY.includes(v)) return true;
	if (FALSY.includes(v)) return false;
};
/**
* `CHATTER_AGENT_DISABLE` — hook と CLI をまとめて黙らせる。無限ループ防止の第1層で、
* 要約プロセスはこれを付けて spawn される（設計書 §4-3）。config のキーではないので
* `SPECS` には載せず、環境変数だけを見る。
*
* ★ 「設定されていれば無効」にしないこと。`CHATTER_AGENT_DISABLE=0` を「無効化の解除」の
*   つもりで書くと、逆に全発話が止まる。しかも診断が出ないので原因に辿り着けない。→ #4
*
* ★ 判定は `plugin/scripts/_lib.sh` の `chatter_disabled` と同じトークン集合に揃える。
*   hook 側だけが黙る / CLI 側だけが黙るという半端な状態を作らないため。
*   未知の値は「無効化しない」に倒す（`parseBoolean` が `undefined` を返すため）。
*/
function isSpeakDisabled(env = process.env) {
	return parseBoolean(env.CHATTER_AGENT_DISABLE) === true;
}
function toInt(raw) {
	const n = typeof raw === "number" ? raw : typeof raw === "string" ? Number(raw.trim()) : NaN;
	return Number.isInteger(n) ? n : void 0;
}
const parsePort = (raw) => {
	const n = toInt(raw);
	return n !== void 0 && n >= 1 && n <= 65535 ? n : void 0;
};
const parsePositiveInt = (raw) => {
	const n = toInt(raw);
	return n !== void 0 && n >= 1 ? n : void 0;
};
const parseNonNegativeInt = (raw) => {
	const n = toInt(raw);
	return n !== void 0 && n >= 0 ? n : void 0;
};
const parseNonEmptyString = (raw) => typeof raw === "string" && raw.trim() ? raw.trim() : void 0;
const parseStringList = (raw) => {
	let items;
	if (typeof raw === "string") items = raw.split(",");
	else if (Array.isArray(raw)) items = raw;
	else return;
	const out = [];
	for (const item of items) {
		if (typeof item !== "string") return void 0;
		const trimmed = item.trim();
		if (trimmed) out.push(trimmed);
	}
	return out;
};
/**
* スキームを絞った URL のパーサを作る。
*
* 素通しにすると、`localhost:10101`（スキーム忘れ）や末尾スラッシュ付きが
* そのまま `${baseUrl}/audio_query` に連結されて、症状が「無音」の設定ミスになる。
* ここで弾けば「不正です。既定値を使います」の警告が出る。
*/
function makeUrlParser(protocols) {
	return (raw) => {
		const text = parseNonEmptyString(raw);
		if (text === void 0) return void 0;
		let url;
		try {
			url = new URL(text);
		} catch {
			return;
		}
		if (!protocols.includes(url.protocol)) return void 0;
		return text.replace(/\/+$/, "");
	};
}
/**
* キーの定義。satisfies で ChatterAgentConfig の全キーを網羅していることを型で担保する
* （satisfies は型のみなので erasableSyntaxOnly に抵触しない）。
* キーを増やすときは ChatterAgentConfig と SPECS の両方を直さないとコンパイルが通らない。
*/
const SPECS = {
	port: {
		env: "CHATTER_AGENT_PORT",
		parse: parsePort
	},
	host: {
		env: "CHATTER_AGENT_HOST",
		parse: parseNonEmptyString
	},
	speakPrompts: {
		env: "CHATTER_AGENT_SPEAK_PROMPTS",
		parse: parseBoolean
	},
	speechLogMaxBytes: {
		env: "CHATTER_AGENT_SPEECH_LOG_MAX_BYTES",
		parse: parsePositiveInt
	},
	speechQueueMaxEntries: {
		env: "CHATTER_AGENT_SPEECH_QUEUE_MAX_ENTRIES",
		parse: parsePositiveInt
	},
	spoolMaxAgeHours: {
		env: "CHATTER_AGENT_SPOOL_MAX_AGE_HOURS",
		parse: parsePositiveInt
	},
	allowedOrigins: {
		env: "CHATTER_AGENT_ALLOWED_ORIGINS",
		parse: parseStringList
	},
	ttsBaseUrl: {
		env: "CHATTER_AGENT_TTS_URL",
		parse: makeUrlParser(["http:", "https:"])
	},
	ttsSpeakerId: {
		env: "CHATTER_AGENT_TTS_SPEAKER_ID",
		parse: parseNonNegativeInt
	},
	synthesisLookahead: {
		env: "CHATTER_AGENT_SYNTHESIS_LOOKAHEAD",
		parse: parseNonNegativeInt
	},
	synthesisTimeoutMs: {
		env: "CHATTER_AGENT_SYNTHESIS_TIMEOUT_MS",
		parse: parsePositiveInt
	},
	playerCommand: {
		env: "CHATTER_AGENT_PLAYER_COMMAND",
		parse: parseNonEmptyString
	},
	playerArgs: {
		env: "CHATTER_AGENT_PLAYER_ARGS",
		parse: parseStringList
	},
	playerServerUrl: {
		env: "CHATTER_AGENT_PLAYER_SERVER_URL",
		parse: makeUrlParser(["ws:", "wss:"])
	},
	speechMaxAgeMs: {
		env: "CHATTER_AGENT_SPEECH_MAX_AGE_MS",
		parse: parseNonNegativeInt
	}
};
const CONFIG_KEYS = Object.keys(SPECS);
function createConfigStore(deps = {}) {
	const filePath = deps.filePath ?? getConfigFilePath(deps.pathEnv ?? currentPathEnv());
	const defaults = deps.defaults ?? createDefaultConfig();
	const warned = /* @__PURE__ */ new Set();
	const warnOnce = (key, message) => {
		if (warned.has(key)) return;
		warned.add(key);
		console.warn(message);
	};
	function collect(source, origin) {
		const out = {};
		for (const key of CONFIG_KEYS) {
			const raw = source[key];
			if (raw === void 0) continue;
			const parsed = SPECS[key].parse(raw);
			if (parsed === void 0) {
				warnOnce(`${origin}:${key}`, `[Config] ${origin} の ${key} が不正です (${JSON.stringify(raw)})。既定値を使います`);
				continue;
			}
			out[key] = parsed;
		}
		return out;
	}
	const envSource = deps.env ?? process.env;
	const envValues = {};
	for (const key of CONFIG_KEYS) {
		const raw = envSource[SPECS[key].env];
		if (raw !== void 0) envValues[key] = raw;
	}
	const overrides = collect(envValues, "環境変数");
	let fileValues = {};
	let merged = {
		...defaults,
		...overrides
	};
	/** `${mtimeMs}:${size}`。ファイルが無いときは null */
	let stamp = null;
	let loaded = false;
	function readFileValues() {
		let text;
		try {
			text = fs.readFileSync(filePath, "utf-8");
		} catch (err) {
			warnOnce("file:read", `[Config] ${filePath} を読めませんでした: ${String(err)}`);
			return;
		}
		let parsed;
		try {
			parsed = JSON.parse(text);
		} catch (err) {
			warnOnce("file:json", `[Config] ${filePath} のJSONが壊れています: ${String(err)}。直前の値を使い続けます`);
			return;
		}
		if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
			warnOnce("file:shape", `[Config] ${filePath} のトップレベルがオブジェクトではありません。直前の値を使い続けます`);
			return;
		}
		const record = parsed;
		for (const key of Object.keys(record)) if (!CONFIG_KEYS.includes(key)) warnOnce(`file:unknown:${key}`, `[Config] ${filePath} の未知のキー "${key}" は無視されます`);
		return collect(record, "config.json");
	}
	function refresh() {
		let next = null;
		try {
			const st = fs.statSync(filePath);
			next = `${st.mtimeMs}:${st.size}`;
		} catch {}
		if (loaded && next === stamp) return;
		loaded = true;
		stamp = next;
		if (next === null) fileValues = {};
		else {
			const parsed = readFileValues();
			if (parsed) fileValues = parsed;
		}
		merged = {
			...defaults,
			...fileValues,
			...overrides
		};
	}
	return {
		filePath,
		get(key) {
			refresh();
			return merged[key];
		},
		snapshot() {
			refresh();
			return { ...merged };
		}
	};
}

//#endregion
//#region src/core/atomicWrite.ts
/**
* tmp を書いてから rename する原子書き込み。
*
* ★ 素の `writeFileSync` は対象を O_TRUNC してから書くので、書き込みの途中でプロセスが
*   落ちると、読み手には空バイト・あるいは書きかけのバイト列が見える窓ができる。
*   同じディレクトリに `.tmp` を書いてから rename すれば、POSIX の rename はファイル
*   システム内で原子的なので、読み手には「書く前」か「書き終わった後」の2状態しか
*   見えない。
*
* 何が壊れるかは呼び出し側ごとに違う（配信キューの entry か、記録の seq state か、
* 応答待ちの重複抑制 state か、spool の進捗サイドカーか）。ここは機構だけを持ち、
* 具体的な帰結は各呼び出し側のコメントに残す。
*/
const TMP_SUFFIX$1 = ".tmp";
function writeFileAtomic(filePath, data) {
	const tmp = `${filePath}${TMP_SUFFIX$1}`;
	fs.writeFileSync(tmp, data);
	fs.renameSync(tmp, filePath);
}

//#endregion
//#region src/core/speechLog.ts
/**
* `speech.jsonl` への追記・ローテート・`seq` 採番。
*
* **記録**であって配信経路ではない。配信は `speechQueue.ts` が持つ。
*
* ★ 誰もこのファイルを tail しないので、**ローテートの正しさが要求されない**。
*   退避は1世代だけ（`speech.1.jsonl` を上書き）で、取りこぼしても困る人がいない。
*   読み手がいた頃は、世代交代の検出と未読部分の回収を正しく保つ必要があった。
*
* 呼び出し側は**ロックを保持していること**。`seq` の採番と state の更新はロック下でしか行わない
* （並列に走らせると発話順が入れ替わる。CLAUDE.md「絶対に守ること」4）。
*/
/** 末尾から seq を拾うために読むバイト数。1行はたかだか数KBなのでこれで足りる */
const TAIL_READ_BYTES = 65536;
/** ファイル末尾の有効な行から seq を拾う。読めなければ 0 */
function readLastSeq(filePath) {
	let fd;
	try {
		fd = fs.openSync(filePath, "r");
	} catch {
		return 0;
	}
	try {
		const size = fs.fstatSync(fd).size;
		if (size === 0) return 0;
		const length = Math.min(size, TAIL_READ_BYTES);
		const buffer = Buffer.allocUnsafe(length);
		fs.readSync(fd, buffer, 0, length, size - length);
		const lines = buffer.toString("utf-8").split("\n");
		for (let i = lines.length - 1; i >= 0; i--) {
			const line = lines[i]?.trim();
			if (!line) continue;
			try {
				const parsed = JSON.parse(line);
				if (typeof parsed === "object" && parsed !== null) {
					const seq = parsed.seq;
					if (typeof seq === "number" && Number.isSafeInteger(seq)) return seq;
				}
			} catch {}
		}
		return 0;
	} finally {
		fs.closeSync(fd);
	}
}
function readStateNextSeq(statePath) {
	try {
		const parsed = JSON.parse(fs.readFileSync(statePath, "utf-8"));
		if (typeof parsed === "object" && parsed !== null) {
			const next = parsed.nextSeq;
			if (typeof next === "number" && Number.isSafeInteger(next) && next >= 1) return next;
		}
	} catch {}
	return 1;
}
function writeStateNextSeq(statePath, nextSeq) {
	writeFileAtomic(statePath, `${JSON.stringify({ nextSeq })}\n`);
}
function createSpeechLog(deps) {
	const { logPath, statePath, maxBytes } = deps;
	const now = deps.now ?? (() => /* @__PURE__ */ new Date());
	const backupPath = getSpeechLogBackupPath(logPath);
	fs.mkdirSync(path.dirname(logPath), { recursive: true });
	/**
	* state と実ファイルの整合を取る。
	*
	* クラッシュで両者がずれると、seq の重複（クライアントの欠落検出が壊れる）か
	* 欠番（欠落の誤検知）になる。どちらに転んでも直せるよう、大きい方を採る。
	* ローテート直後は現世代が空なので、その場合だけ1世代前も見る。
	*/
	function reconcile() {
		let lastSeq = readLastSeq(logPath);
		if (lastSeq === 0) lastSeq = readLastSeq(backupPath);
		return Math.max(readStateNextSeq(statePath), lastSeq + 1);
	}
	let nextSeq = reconcile();
	/** 退避は1世代だけ。前の退避は上書きされる */
	function rotate() {
		if (fs.existsSync(logPath)) fs.renameSync(logPath, backupPath);
	}
	function currentSize() {
		try {
			return fs.statSync(logPath).size;
		} catch {
			return 0;
		}
	}
	/**
	* 末尾が改行で終わっているか。
	*
	* 追記が途中で切れた（電源断など）ファイルに素直に追記すると、壊れた断片と次の
	* レコードが**1行に融合して**、正常なはずの行まで読めなくなる。改行を1つ挟んで
	* 断片を断片のまま隔離する。
	*/
	function endsWithNewline(size) {
		if (size === 0) return true;
		let fd;
		try {
			fd = fs.openSync(logPath, "r");
		} catch {
			return true;
		}
		try {
			const buffer = Buffer.allocUnsafe(1);
			fs.readSync(fd, buffer, 0, 1, size - 1);
			return buffer[0] === 10;
		} finally {
			fs.closeSync(fd);
		}
	}
	return {
		peekNextSeq: () => nextSeq,
		append(entries) {
			if (entries.length === 0) return [];
			const ts = now().toISOString();
			const records = entries.map((entry) => ({
				seq: nextSeq++,
				ts,
				source: entry.source,
				sessionId: entry.sessionId,
				turnId: entry.turnId,
				messageId: entry.messageId,
				kind: entry.kind,
				text: entry.text,
				emotion: entry.emotion
			}));
			const body = `${records.map((record) => JSON.stringify(record)).join("\n")}\n`;
			let size = currentSize();
			if (size > 0 && size + Buffer.byteLength(body) > maxBytes) {
				rotate();
				size = 0;
			}
			const payload = endsWithNewline(size) ? body : `\n${body}`;
			fs.appendFileSync(logPath, payload);
			writeStateNextSeq(statePath, nextSeq);
			return records;
		}
	};
}

//#endregion
//#region src/core/speechQueue.ts
/**
* 発話の配信キュー。1文1ファイルで、**ファイル名が `seq`**。
*
* ```
* {root}/speech/000000000123.json    ← 中身は speech.jsonl の1行と同一
* {root}/speech/000000000124.json
* ```
*
* CLI がロック下で書き、server が読んで配信し、クライアントの ack で消える。
*
* ★ なぜ `speech.jsonl` の tail をやめたか。
*   1つのファイルに記録と配信を兼ねさせると、**読み手だけが突出して複雑になる**。
*   ローテートを跨ぐ差分読み取りは、世代交代の検出（inode か、サイズの逆行か）、
*   退避された世代の未読部分の回収、消費バイト数の算術を同時に正しく保つ必要があり、
*   実際に取りこぼしと二重配信を両方踏んだ。
*
*   キューにすると、順序はファイル名で決まり、消費は削除で表せる。
*   **誰も tail しなくなるので、記録側のローテートの正しさも要求されなくなる。**
*
* `spool/` と同じ形。あちらは hook が書いて CLI が消し、こちらは CLI が書いて server が消す。
*
* ★ `list()` と `read()` に分けてあるのは、配信済みの entry を毎回全件 readFileSync
*   させないため。server は 50ms ごとにポーリングするが、配信済みかどうかの絞り込みは
*   呼び出し側で後から走るので、ファイル名の列挙だけで済む問い合わせと、実際に配信する
*   1件だけの読み取りを分けないと、マスコットが繋がっていない間（ack が来ず trim が
*   上限に張り付く）も毎秒何十回と全件を読み直すことになる。
*/
/** ファイル名の桁数。`ls` で並べたときに順序が見えるよう固定幅にする */
const SEQ_DIGITS = 12;
const SUFFIX = ".json";
const TMP_SUFFIX = ".tmp";
function fileNameFor(seq) {
	return `${String(seq).padStart(SEQ_DIGITS, "0")}${SUFFIX}`;
}
/**
* ファイル名から `seq` を読む。
*
* ★ 中身の JSON はパースしない。順序を決めるだけなら名前で足りるし、
*   1文ごとにパースするコストを server のポーリングに乗せたくない。
*/
function seqFromFileName(fileName) {
	if (!fileName.endsWith(SUFFIX)) return null;
	const stem = fileName.slice(0, -5);
	if (!/^\d+$/.test(stem)) return null;
	const seq = Number(stem);
	return Number.isSafeInteger(seq) ? seq : null;
}
function createSpeechQueue(queueDir) {
	fs.mkdirSync(queueDir, { recursive: true });
	/** ディレクトリを走査して `seq` 昇順に並べる。中身は読まない */
	function listSeqs() {
		let fileNames;
		try {
			fileNames = fs.readdirSync(queueDir);
		} catch {
			return [];
		}
		const found = [];
		for (const fileName of fileNames) {
			const seq = seqFromFileName(fileName);
			if (seq !== null) found.push({
				seq,
				fileName
			});
		}
		return found.sort((a, b) => a.seq - b.seq);
	}
	function remove(fileName) {
		try {
			fs.rmSync(path.join(queueDir, fileName), { force: true });
			return true;
		} catch {
			return false;
		}
	}
	return {
		enqueue(records) {
			let written = 0;
			for (const record of records) {
				const target = path.join(queueDir, fileNameFor(record.seq));
				try {
					writeFileAtomic(target, `${JSON.stringify(record)}\n`);
					written++;
				} catch (err) {
					console.error(`[SpeechQueue] seq=${record.seq} の書き込みに失敗しました:`, err);
				}
			}
			return written;
		},
		list() {
			return listSeqs().map(({ seq }) => seq);
		},
		read(seq) {
			const filePath = path.join(queueDir, fileNameFor(seq));
			let line;
			try {
				line = fs.readFileSync(filePath, "utf-8").trim();
			} catch {
				return null;
			}
			if (!line) return null;
			let parsed;
			try {
				parsed = JSON.parse(line);
			} catch {
				return null;
			}
			if (typeof parsed !== "object" || parsed === null) return null;
			if (parsed.seq !== seq) return null;
			return line;
		},
		ackUpTo(upTo) {
			if (!Number.isSafeInteger(upTo) || upTo < 0) return 0;
			let removed = 0;
			for (const { seq, fileName } of listSeqs()) {
				if (seq > upTo) break;
				if (remove(fileName)) removed++;
			}
			return removed;
		},
		dropOlderThan(maxAgeMs, now = Date.now()) {
			let removed = 0;
			for (const { fileName } of listSeqs()) {
				let mtimeMs;
				try {
					mtimeMs = fs.statSync(path.join(queueDir, fileName)).mtimeMs;
				} catch {
					continue;
				}
				if (now - mtimeMs <= maxAgeMs) continue;
				if (remove(fileName)) removed++;
			}
			return removed;
		},
		trim(maxEntries) {
			if (maxEntries < 0) return 0;
			const all = listSeqs();
			const excess = all.length - maxEntries;
			if (excess <= 0) return 0;
			let removed = 0;
			for (const { fileName } of all.slice(0, excess)) if (remove(fileName)) removed++;
			return removed;
		},
		sweepTmp() {
			let fileNames;
			try {
				fileNames = fs.readdirSync(queueDir);
			} catch {
				return 0;
			}
			let removed = 0;
			for (const fileName of fileNames) {
				if (!fileName.endsWith(`${SUFFIX}${TMP_SUFFIX}`)) continue;
				if (remove(fileName)) removed++;
			}
			return removed;
		}
	};
}

//#endregion
//#region src/emotion/ruleBasedEmotionClassifier.ts
var RuleBasedEmotionClassifier = class {
	/**
	* 感情キーワード辞書
	*/
	emotionKeywords = {
		happy: [
			"うれしい",
			"嬉しい",
			"うれ",
			"喜",
			"喜び",
			"よかった",
			"よかっ",
			"良かっ",
			"良い",
			"やった",
			"やっ",
			"できた",
			"すごい",
			"すご",
			"凄",
			"素晴らしい",
			"素敵",
			"ありがと",
			"ありが",
			"感謝",
			"サンクス",
			"楽しい",
			"楽し",
			"愉快",
			"面白い",
			"面白",
			"成功",
			"完璧",
			"完了",
			"クリア",
			"最高",
			"ベスト",
			"グッド",
			"ナイス",
			"いいね",
			"助かっ",
			"助かる",
			"わーい",
			"やっほー",
			"やったー",
			"いえーい",
			"達成",
			"ゲット",
			"獲得",
			"実現",
			"解決",
			"修正できた",
			"直った",
			"満足",
			"幸せ",
			"ハッピー",
			"ラッキー",
			"運が良",
			"期待以上",
			"想像以上"
		],
		angry: [
			"むかつく",
			"むかつ",
			"ムカつ",
			"腹立",
			"怒",
			"イライラ",
			"いらいら",
			"キレ",
			"最悪",
			"ひどい",
			"酷",
			"クソ",
			"くそ",
			"うざい",
			"ウザ",
			"うっとうし",
			"許せない",
			"許せ",
			"我慢できない",
			"ダメ",
			"駄目",
			"ダメだ",
			"だめ",
			"エラー",
			"バグ",
			"失敗",
			"動かない",
			"壊れ",
			"問題",
			"トラブル",
			"不具合",
			"障害",
			"困る",
			"困っ",
			"困った",
			"信じられない",
			"呆れ",
			"ふざけ",
			"冗談じゃ",
			"勘弁",
			"マジで",
			"本気で腹"
		],
		sad: [
			"悲しい",
			"悲し",
			"哀",
			"残念",
			"ざんねん",
			"惜しい",
			"つらい",
			"辛い",
			"つら",
			"苦しい",
			"ごめん",
			"すまな",
			"すみま",
			"申し訳",
			"謝",
			"無理",
			"不可能",
			"困った",
			"困難",
			"諦め",
			"あきら",
			"断念",
			"失敗し",
			"しくじ",
			"ミス",
			"駄目だった",
			"間に合わ",
			"遅れ",
			"自信ない",
			"不安",
			"心配",
			"怖",
			"しょんぼり",
			"がっかり",
			"落ち込",
			"泣",
			"涙"
		],
		surprised: [
			"え！",
			"えっ",
			"え？",
			"えー",
			"まさか",
			"マジ",
			"まじ",
			"本当",
			"びっくり",
			"ビックリ",
			"驚",
			"ビビ",
			"意外",
			"予想外",
			"想定外",
			"なんと",
			"何と",
			"おお",
			"おぉ",
			"すごっ",
			"やば",
			"ヤバ",
			"信じられない",
			"嘘",
			"うそ",
			"ウソ",
			"本当に",
			"ほんと",
			"本気",
			"あり得ない",
			"ありえな",
			"初めて",
			"見たことない",
			"はぁ！？",
			"へぇ",
			"ほぉ",
			"ふぉ",
			"おったまげ",
			"たまげ"
		],
		relaxed: [
			"落ち着",
			"落着",
			"冷静",
			"安心",
			"あんしん",
			"ホッと",
			"大丈夫",
			"だいじょうぶ",
			"だいじょぶ",
			"OK",
			"ok",
			"オッケー",
			"おk",
			"了解",
			"りょうかい",
			"承知",
			"問題ない",
			"問題なし",
			"ノープロブレム",
			"ゆっくり",
			"のんびり",
			"じっくり",
			"様子見"
		]
	};
	/**
	* 文末パターン（正規表現）
	* 女性言葉・中性的・丁寧・男性的な言葉すべてに対応
	*/
	sentenceEndPatterns = {
		happy: [
			/[！!]{2,}/,
			/わ[ね〜～！!♪]+$/,
			/わよ[！!♪]+$/,
			/です[！!♪]+$/,
			/ます[！!♪]+$/,
			/ました[！!♪]+$/,
			/ね[！!♪]+$/,
			/よ[！!♪]+$/,
			/ぜ[！!]+$/,
			/ぞ[！!]+$/,
			/だ[！!]+$/,
			/った[！!]+$/,
			/[♪♫]+/,
			/[✨🎉🎊😊😄🎊👍]+/u
		],
		angry: [
			/[！!？?]{2,}/,
			/わよ[！!]{2,}$/,
			/のよ[！!]+$/,
			/です[！!]{2,}$/,
			/ません[！!]+$/,
			/だ[！!]{2,}$/,
			/だろ[！!？?]+$/,
			/のか[！!？?]+$/,
			/[💢😠😡]+/u
		],
		sad: [
			/わ…+$/,
			/のね…+$/,
			/です…+$/,
			/ます…+$/,
			/ません…+$/,
			/だ…+$/,
			/な…+$/,
			/[。.]{2,}$/,
			/…+$/,
			/[😢😭💔]+/u
		],
		surprised: [
			/[！!？?]$/,
			/え[っ〜～！!？?]+/,
			/まさか[！!？?]/,
			/の[！!？?]$/,
			/ですか[！!？?]$/,
			/ますか[！!？?]$/,
			/のか[！!？?]$/,
			/だと[！!？?]$/,
			/マジ[！!？?]/,
			/ほんと[！!？?]/,
			/本当[！!？?]/,
			/[😮😲🤯]+/u
		],
		relaxed: [
			/わ[ね〜～]+$/,
			/ですわ[〜～]+$/,
			/です[〜～]+$/,
			/ます[〜～]+$/,
			/ました[〜～]+$/,
			/ね[〜～]+$/,
			/OK[。.〜～]+$/,
			/了解[。.〜～]+$/
		]
	};
	/**
	* テキストから感情を分類する
	* @param text 分類対象のテキスト
	* @returns 分類された感情
	*/
	classify(text) {
		if (!text || text.trim().length < 2) return "neutral";
		const normalizedText = text.trim();
		const isLongText = normalizedText.length > 100;
		const scores = {
			neutral: 0,
			happy: 0,
			angry: 0,
			sad: 0,
			relaxed: 0,
			surprised: 0
		};
		const keywordWeight = isLongText ? 3 : 2;
		for (const [emotion, keywords] of Object.entries(this.emotionKeywords)) for (const keyword of keywords) if (normalizedText.includes(keyword)) scores[emotion] += keywordWeight;
		const patternWeight = isLongText ? 4 : 2;
		for (const [emotion, patterns] of Object.entries(this.sentenceEndPatterns)) for (const pattern of patterns) if (pattern.test(normalizedText)) scores[emotion] += patternWeight;
		const firstPart = normalizedText.substring(0, 50);
		for (const [emotion, keywords] of Object.entries(this.emotionKeywords)) for (const keyword of keywords) if (firstPart.includes(keyword)) scores[emotion] += 2;
		this.applyHeuristics(normalizedText, scores);
		if (scores.angry > 0 || scores.sad > 0) {
			if (this.sentenceEndPatterns.happy.some((p) => p.test(normalizedText)) && (scores.angry > 0 || scores.sad > 0)) scores.happy = Math.floor(scores.happy * .5);
		}
		if (isLongText) {
			if (scores.happy + scores.angry + scores.sad + scores.surprised + scores.relaxed >= 10) scores.neutral = Math.max(0, scores.neutral - 3);
		}
		if (scores.relaxed > 0 && scores.relaxed < 6) {
			scores.neutral += scores.relaxed;
			scores.relaxed = 0;
		}
		if (scores.neutral >= 4 && scores.sad > 0 && scores.sad < 4) {
			scores.neutral += scores.sad;
			scores.sad = 0;
		}
		let maxEmotion = "neutral";
		let maxScore = 0;
		for (const [emotion, score] of Object.entries(scores)) if (score > maxScore) {
			maxScore = score;
			maxEmotion = emotion;
		}
		if (process.env.NODE_ENV === "development" && maxEmotion !== "neutral") {
			console.log(`[EmotionClassifier] Text: "${normalizedText.substring(0, 50)}${normalizedText.length > 50 ? "..." : ""}"`);
			console.log(`[EmotionClassifier] Scores:`, scores);
			console.log(`[EmotionClassifier] Result: ${maxEmotion}`);
		}
		return maxEmotion;
	}
	/**
	* ヒューリスティックルールを適用
	*/
	applyHeuristics(text, scores) {
		const hasEmotion = scores.happy + scores.angry + scores.sad + scores.surprised + scores.relaxed > 0;
		if (/[？?]$/.test(text)) scores.surprised += 1;
		if (text.length < 10) {
			if (/^(OK|了解|わかった)/.test(text)) scores.relaxed += 2;
		}
		if (/```|`[^`]+`/.test(text)) {
			scores.neutral += hasEmotion ? 2 : 4;
			scores.relaxed = Math.max(0, scores.relaxed - 2);
		}
		if (/(import|export|function|const|let|var|class|interface|type)/.test(text)) {
			scores.neutral += hasEmotion ? 2 : 4;
			scores.relaxed = Math.max(0, scores.relaxed - 2);
		}
		if (/[/\\][a-zA-Z0-9_\-./\\]+/.test(text)) scores.neutral += hasEmotion ? 0 : 1;
		if (/(コード|関数|メソッド|変数|クラス|インターフェース|型|配列|オブジェクト|プロパティ)/.test(text)) {
			scores.neutral += hasEmotion ? 1 : 3;
			scores.relaxed = Math.max(0, scores.relaxed - 1);
		}
		if (!hasEmotion && /(次に|まず|それから|その後|最後に|ここで|この|その)/.test(text)) scores.neutral += 1;
		if (text.length > 100) {
			if ((text.match(/[。.]/g) || []).length >= 3) scores.neutral += hasEmotion ? 1 : 2;
		}
		if (/(エラー|バグ|問題|失敗)/.test(text) && /(修正|解決|できた|成功|完了)/.test(text)) {
			scores.happy += 4;
			scores.angry = Math.max(0, scores.angry - 2);
		}
	}
};

//#endregion
//#region src/cli/publish.ts
/** 記録と配信の両方に書く。記録できた時点で「出した」が確定する */
function createPublisher(deps) {
	const { speechLog, speechQueue, maxEntries } = deps;
	return (entries) => {
		const records = speechLog.append(entries);
		try {
			const written = speechQueue.enqueue(records);
			if (written !== records.length) console.error(`[chatter-agent-speak] 配信キューへの書き込みが ${records.length} 件中 ${written} 件しか成功しませんでした`);
		} catch (err) {
			console.error("[chatter-agent-speak] 配信キューへの書き込みに失敗しました:", err);
		}
		try {
			const dropped = speechQueue.trim(maxEntries());
			if (dropped > 0) console.error(`[chatter-agent-speak] 配信キューの上限を超えたため ${dropped} 件を破棄しました`);
		} catch (err) {
			console.error("[chatter-agent-speak] 配信キューの上限チェックに失敗しました:", err);
		}
		return records;
	};
}

//#endregion
//#region src/prompt/promptEventFormatter.ts
/** ExitPlanMode の読み上げ文の定型部分（計画の見出しが取れない場合はこれだけを話す） */
const PLAN_APPROVAL_TEXT = "計画がまとまりました。確認をお願いします。";
/** 許可プロンプトの読み上げ文（通知本文が取れない場合のフォールバック） */
const PERMISSION_PROMPT_TEXT = "許可を求めています。確認をお願いします。";
function isRecord$1(value) {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}
/**
* AskUserQuestion の入力から、質問ごとの読み上げ文を組み立てる
* 例: 「どちらにしますか？ 選択肢は、そのまま進める、やり直す。」
* 選択肢の description は長くなりすぎるため読み上げない
*/
function buildQuestionTexts(input) {
	if (!isRecord$1(input) || !Array.isArray(input.questions)) return [];
	const texts = [];
	for (const question of input.questions) {
		if (!isRecord$1(question) || typeof question.question !== "string") continue;
		const questionText = question.question.trim();
		if (!questionText) continue;
		const labels = Array.isArray(question.options) ? question.options.filter(isRecord$1).map((option) => typeof option.label === "string" ? option.label.trim() : "").filter((label) => label.length > 0) : [];
		texts.push(labels.length > 0 ? `${questionText} 選択肢は、${labels.join("、")}。` : questionText);
	}
	return texts;
}
/**
* ExitPlanMode の入力から承認依頼の読み上げ文を組み立てる
* plan は計画の全文マークダウン（数千字）なので、先頭の見出しだけを使う
*/
function buildPlanApprovalText(input) {
	const heading = (isRecord$1(input) && typeof input.plan === "string" ? input.plan : "").match(/^#{1,6}\s+(.+)$/m)?.[1].trim();
	return heading ? `「${heading}」の${PLAN_APPROVAL_TEXT}` : PLAN_APPROVAL_TEXT;
}
/** フックの payload に含まれるセッションIDを取り出す（取れなければ null） */
function getEventSessionId(payload) {
	return getStringField(payload, "session_id");
}
/**
* フックの payload に含まれるプロンプトIDを取り出す（取れなければ null）
* PreToolUse と、その直後に発火する Notification は同じ prompt_id を持つ
*/
function getEventPromptId(payload) {
	return getStringField(payload, "prompt_id");
}
/** フックの payload に含まれるイベント名を取り出す（取れなければ null） */
function getEventHookName(payload) {
	return getStringField(payload, "hook_event_name");
}
function getStringField(payload, key) {
	if (!isRecord$1(payload)) return null;
	const value = payload[key];
	return typeof value === "string" && value ? value : null;
}
/**
* フックの payload を読み上げメッセージに変換する
* 対象外のイベントや壊れた payload の場合は空配列を返す
*/
function formatPromptEvent(payload) {
	if (!isRecord$1(payload)) return [];
	return buildTexts(payload).map((text) => text.trim()).filter((text) => text.length > 0).map((text) => ({
		type: "speak",
		text,
		kind: "prompt"
	}));
}
function buildTexts(payload) {
	switch (payload.hook_event_name) {
		case "PreToolUse":
			if (payload.tool_name === "AskUserQuestion") return buildQuestionTexts(payload.tool_input);
			if (payload.tool_name === "ExitPlanMode") return [buildPlanApprovalText(payload.tool_input)];
			return [];
		case "Notification": return [typeof payload.message === "string" && payload.message.trim() ? payload.message : PERMISSION_PROMPT_TEXT];
		default: return [];
	}
}

//#endregion
//#region src/text/textFilter.ts
/**
* Originally from kazakago/cc-mascot (Apache-2.0, Copyright 2026 kazakago)
*   electron/filters/textFilter.ts @ 46f7def
*/
/**
* Text filtering utilities for speech synthesis
* Removes markdown syntax and other elements that shouldn't be spoken
*/
/**
* Clean text for speech synthesis by removing markdown syntax and
* replacing special characters with readable alternatives
*/
function cleanTextForSpeech(text) {
	let cleaned = text;
	cleaned = cleaned.replace(/```[\s\S]*?```/g, "");
	cleaned = cleaned.replace(/<[^>]+>/g, "");
	cleaned = cleaned.replace(/^#{1,6}\s+/gm, "");
	cleaned = cleaned.replace(/^[-*]{3,}$/gm, "");
	cleaned = cleaned.replace(/^\|.*\|$/gm, "");
	cleaned = cleaned.replace(/^>\s*/gm, "");
	cleaned = cleaned.replace(/^[-*]\s+/gm, "");
	cleaned = cleaned.replace(/https?:\/\/[^\s]+/g, "");
	cleaned = cleaned.replace(/\b[0-9a-f]{7,40}\b/g, "");
	cleaned = cleaned.replace(/`([^`]+)`/g, "$1");
	return cleaned;
}
/**
* Split text into individual sentences for sequential speech synthesis.
* Splits on Japanese period (。), exclamation (！/!), question (？/?), and newlines.
* Returns trimmed sentences (including empty strings as spacing information).
*/
function splitIntoSentences(text) {
	return text.split(/(?<=[。！？!?])|[\n\r]+/).map((s) => s.trim());
}

//#endregion
//#region src/core/lock.ts
/**
* 単一ワーカーのロック。
*
* CLI は hook から**毎 delta 起動される**が、spool を処理してよいのは
* ロックを取れた1プロセスだけ（CLAUDE.md「絶対に守ること」4）。`seq` の採番も
* このロック下で行う。取れなかったプロセスは何もせず即終了する（先行ワーカーが拾う）。
*
* `src/cli/` と `src/core/` はどちらも npm 依存を持てない（docs/core.md）ので、
* ロックライブラリは使わず**`mkdir` の原子性**で実装する。同名ディレクトリの
* 作成は、成功するのが必ず1プロセスだけ。
*/
const DEFAULT_STALE_MS = 6e4;
function ownerFilePath(lockDir) {
	return path.join(lockDir, "owner.json");
}
function readOwner(lockDir) {
	try {
		const parsed = JSON.parse(fs.readFileSync(ownerFilePath(lockDir), "utf-8"));
		if (typeof parsed === "object" && parsed !== null) {
			const { pid, token } = parsed;
			if (typeof pid === "number" && Number.isInteger(pid) && typeof token === "string") return {
				pid,
				token
			};
		}
	} catch {}
	return null;
}
/** そのプロセスが生きているか。権限が無くて確認できない場合は「生きている」に倒す */
function isProcessAlive(pid) {
	try {
		process.kill(pid, 0);
		return true;
	} catch (err) {
		return err.code === "EPERM";
	}
}
/**
* 放置されたロックか判定する。
*
* 所有者が読めるなら pid の生死**だけ**で決める。古さも条件に加えると、
* ドレインが staleMs を超えて長引いた・ラップトップがサスペンドから復帰しただけの
* **生きた保持者**から奪ってしまう（実測: mtime だけで60秒経過扱いにすると
* 生きたロックが奪われる）。
*
* ★ この結果、pid が再利用されると恒久的なロックになる穴が残る（死んだプロセスの
*   pid を別の生きたプロセスが引き継ぐと、owner は永遠に「生きている」に見える）。
*   意図的な判断: 「生きた保持者を60秒で奪う」方が実害が大きい。踏んだら
*   `speak.lock` を手で消せば済む。
*
* 所有者が読めない場合（mkdir 直後で owner.json を書く前 / 壊れている）だけ、
* 経過時間で判定する。読めないまま staleMs 続いたなら owner.json を書けずに死んだとみなす。
*/
function isStale(lockDir, staleMs, now) {
	const owner = readOwner(lockDir);
	if (owner !== null) return !isProcessAlive(owner.pid);
	try {
		return now - fs.statSync(lockDir).mtimeMs >= staleMs;
	} catch {
		return false;
	}
}
/**
* ロックを取る。取れなければ `null`（呼び出し側は即終了すること）。
* 放置ロックを見つけた場合だけ、1回だけ奪って取り直す。
*/
function acquireLock(lockDir, options = {}) {
	const staleMs = options.staleMs ?? DEFAULT_STALE_MS;
	const now = options.now ?? Date.now;
	const pid = options.pid ?? process.pid;
	const token = options.token ?? `${pid}-${process.hrtime.bigint()}`;
	fs.mkdirSync(path.dirname(lockDir), { recursive: true });
	const tryCreate = () => {
		try {
			fs.mkdirSync(lockDir);
		} catch (err) {
			if (err.code === "EEXIST") return null;
			throw err;
		}
		try {
			fs.writeFileSync(ownerFilePath(lockDir), `${JSON.stringify({
				pid,
				token
			})}\n`, { flag: "wx" });
		} catch {
			return null;
		}
		const owner = readOwner(lockDir);
		if (owner === null || owner.token !== token) return null;
		return makeLock(lockDir, token);
	};
	const first = tryCreate();
	if (first) return first;
	if (!isStale(lockDir, staleMs, now())) return null;
	const evicted = `${lockDir}.evicted-${token}`;
	try {
		fs.renameSync(lockDir, evicted);
	} catch {
		return null;
	}
	if (!isStale(evicted, staleMs, now())) {
		try {
			fs.renameSync(evicted, lockDir);
		} catch {
			fs.rmSync(evicted, {
				recursive: true,
				force: true
			});
		}
		return null;
	}
	fs.rmSync(evicted, {
		recursive: true,
		force: true
	});
	return tryCreate();
}
function makeLock(lockDir, token) {
	let released = false;
	return { release() {
		if (released) return;
		released = true;
		const owner = readOwner(lockDir);
		if (owner === null || owner.token !== token) return;
		fs.rmSync(lockDir, {
			recursive: true,
			force: true
		});
	} };
}

//#endregion
//#region src/text/unstableTail.ts
/**
* まだ確定していない末尾の切り落とし。
*
* chatter-agent 固有の要件で、上流 cc-mascot には存在しない。
*
* CLI は毎 delta 起動して終了するため、進捗は「出力済みの文数」でしか持てない。
* これが成り立つ前提は **既に出した範囲が後から変化しないこと** で、`cleanTextForSpeech` を
* 伸び続ける raw に繰り返し適用するかぎり、その前提は自動では成立しない。
*
* 10段の正規表現のうち、**開始位置が既出範囲にあり、閉じ側が後から届く**ものが危険:
*
* | 構文 | 何が起きるか |
* |---|---|
* | ```` ``` ```` | 閉じフェンスが来るまでコードが読み上げられる |
* | `<…>` | `>` が届いた瞬間、`<` 以降の**既に発話した文ごと**削除される |
* | `` `…` `` | 閉じバッククォートが届くと既出テキストから記号が消える |
* | 表の行 | 行が閉じるまで生の `\| A \| B` が読み上げられ、閉じると消える |
* | URL | 空白が来るまで削除範囲が伸び続ける |
* | 16進列 | 7文字目が届いた瞬間に消え、41文字目で戻る |
*
* これらの開始位置より後ろを切り落としてから整形すれば、既出範囲は変化しなくなる。
*
* ★ 引き換えに**発話が遅れる**。未閉じの `<` がある間、それ以降は保留される。
*   `final:true` で保留は解ける（もう伸びないので不安定ではなくなる）ため、
*   遅延の上限は「メッセージが閉じるまで」＝保留中の最終文と同じ。
*/
const FENCE = "```";
/**
* まだ確定していない末尾があれば、その開始位置より後ろを切り落とす。
* すべて確定していれば元の文字列をそのまま返す。
*/
function truncateAtUnstableTail(text, options = {}) {
	const scan = text.replace(/```[\s\S]*?```/g, (block) => block.replace(/[^\n]/g, " "));
	const always = [unclosedFenceAt(text), incompleteTableRowAt(scan)];
	const whileStreaming = options.final ? [] : [
		unclosedTagAt(scan),
		unclosedInlineCodeAt(scan),
		trailingUrlAt(scan),
		trailingHexRunAt(scan)
	];
	let cut = text.length;
	for (const at of [...always, ...whileStreaming]) if (at !== null && at < cut) cut = at;
	return cut === text.length ? text : text.slice(0, cut);
}
/**
* 開いたままのコードフェンスの開始位置。
*
* 数え方は `cleanTextForSpeech` の正規表現に合わせ、左から順に非重複で ``` を拾い、
* 奇数個目を開き・偶数個目を閉じとして扱う。行頭かどうかは見ない（正規表現も見ていない）。
*/
function unclosedFenceAt(text) {
	return unclosedDelimiterAt(text, FENCE);
}
/** 開いたままのインラインバッククォートの開始位置 */
function unclosedInlineCodeAt(scan) {
	return unclosedDelimiterAt(scan, "`");
}
function unclosedDelimiterAt(text, delimiter) {
	let searchFrom = 0;
	let openedAt = -1;
	let isOpen = false;
	for (;;) {
		const found = text.indexOf(delimiter, searchFrom);
		if (found === -1) break;
		if (isOpen) isOpen = false;
		else {
			isOpen = true;
			openedAt = found;
		}
		searchFrom = found + delimiter.length;
	}
	return isOpen ? openedAt : null;
}
/**
* 閉じていない `<` の位置。
*
* `/<[^>]+>/g` は左から非重複で拾うので、**最後の `>` より後ろにある最初の `<`** が
* 閉じ待ちになる。それより前の `<` はすでにどれかの `>` と対になっている。
*/
function unclosedTagAt(scan) {
	const found = scan.indexOf("<", scan.lastIndexOf(">") + 1);
	return found === -1 ? null : found;
}
/**
* 書きかけの表の行の位置。
*
* 除去の正規表現は `/^\|.*\|$/gm` で、行が `|` で閉じて初めて消える。閉じるまでの間は
* 生の `| A | B` が1文として読み上げられ、閉じた瞬間に消えるので、既出範囲が縮む。
*/
function incompleteTableRowAt(scan) {
	const lineStart = scan.lastIndexOf("\n") + 1;
	const line = scan.slice(lineStart);
	if (!line.startsWith("|")) return null;
	return /^\|.*\|$/.test(line) ? null : lineStart;
}
/** 末尾の URL。後続の空白が来るまで削除範囲が伸び続ける */
function trailingUrlAt(scan) {
	return scan.match(/https?:\/\/\S*$/)?.index ?? null;
}
/** 末尾の16進列。7文字目が届いた瞬間に消え、41文字目で戻る */
function trailingHexRunAt(scan) {
	return scan.match(/\b[0-9a-f]+$/)?.index ?? null;
}

//#endregion
//#region src/cli/messageAssembler.ts
/**
* delta の集合から「確定した文」だけを切り出す。chatter-agent の中核。
*
* ★ `final:true` を待ってはいけない（CLAUDE.md「絶対に守ること」1 / 設計書 §2-4）。
*   最終チャンクはメッセージが閉じるとき＝次のブロックが始まるときに flush されるので、
*   その手前でモデルが生成した分だけ遅れる。`AskUserQuestion` の直前だと数十秒に達する。
*   **秒数は仕様ではない**（モデル・thinking の量・ツール入力の大きさで動く）。
*
* そこで delta が届くたびに全体を組み直し、**最後の文を除いた**未出力分だけを流す。
* 最後の文はまだ伸びうるので保留する。
*
* この関数が純粋であることが重要で、CLI は毎 delta 起動して終了するため、
* 状態は「出力済みの文数」だけをディスクに持ち、テキストは毎回ゼロから組み直す。
*/
/** 文として閉じているか。句点・感嘆符・疑問符か、行が変わっていれば閉じている */
function endsAtBoundary(text) {
	return text.length === 0 || /[。！？!?\n\r]\s*$/.test(text);
}
/**
* 全文を組み直し、確定した文のうち未出力のものを返す。
*
* 未確定の末尾（未閉じの ``` や `<` など）を先に切り落とすのが要で、これにより
* 「開いたままのコードや表が読み上げられない」と「既に出した文が後から変化しない」が
* 同時に成立する。後者が `emitted`（文数）で進捗を持てる根拠になっている。
*/
function assembleSentences(input) {
	const raw = input.deltas.join("");
	const safe = truncateAtUnstableTail(raw, { final: input.final });
	const cleaned = cleanTextForSpeech(safe);
	const all = splitIntoSentences(cleaned).filter((sentence) => sentence.length > 0);
	const limit = resolveLimit(all.length, input, safe);
	const clamped = Math.min(input.emitted, all.length);
	const from = Math.min(clamped, limit);
	return {
		sentences: all.slice(from, limit),
		emitted: Math.max(clamped, limit)
	};
}
function resolveLimit(total, input, safe) {
	if (input.final) return total;
	if (input.flushPending && endsAtBoundary(safe)) return total;
	return Math.max(0, total - 1);
}

//#endregion
//#region src/cli/spool.ts
/**
* spool の走査と読み取り。
*
* ★ **hook の payload を解釈するのはこのファイルだけ。** `MessageDisplay` の入力スキーマは
*   公式ドキュメントに記載が無く実測に基づくもの（設計書 §2-3・§10）なので、想定が外れたときに
*   差し替える場所を1箇所に閉じてある。
*
* ★ **spool のファイルは絶対に書き換えない。** hook は delta ごとに `<message_id>.<index>.json`
*   を tmp + rename で置く（追記はしない — bash から任意長の追記を原子的にする移植可能な方法が
*   無いため。→ docs/plugin.md）。1メッセージは複数の delta ファイルに分かれるので、
*   「1メッセージ = 複数ファイル」を1エントリにまとめるのがこのファイルの仕事。
*   ワーカーが持つ進捗は `<message_id>.progress.json` のサイドカーに置く。
*/
/** `<message_id>.<index>.json`。message_id はサニタイズ済みで `.` を含まない（plugin 側の責務） */
const MESSAGE_DELTA_RE = /^(.+)\.(\d+)\.json$/;
const PROMPT_PREFIX = "prompt-";
const PROMPT_SUFFIX = ".json";
const PROGRESS_SUFFIX = ".progress.json";
function progressPathFor(messageId, spoolDir) {
	return path.join(spoolDir, `${messageId}${PROGRESS_SUFFIX}`);
}
/**
* 到着順のキー。
*
* ★ mtime を使わないこと。1 delta 1 ファイルにしても、進捗サイドカーはワーカーが書き換えるし、
*   何よりメッセージの「到着順」を代表する値としては個々のファイルの mtime ではなく
*   birthtime を使う必要がある（後述 `arrivalOrderOfMessage`）。birthtime を持たない環境では
*   mtime に落とす。
*
* ★ ミリ秒（`birthtimeMs`）では粗すぎる。同じミリ秒に作られたファイルが同値になり、
*   下のタイブレーク（パス順）に落ちて到着順が壊れる。`bigint: true` の統計情報が持つ
*   ナノ秒を使う。ナノ秒は number の安全整数を超えるので bigint のまま扱う。
*/
function arrivalOrder(stat) {
	return stat.birthtimeNs > 0n ? stat.birthtimeNs : stat.mtimeNs;
}
/** 判定順は「進捗サイドカーを最初に弾く → prompt- → message」を維持する */
function classify(fileName, filePath, order) {
	if (fileName.endsWith(PROGRESS_SUFFIX)) return null;
	if (fileName.startsWith(PROMPT_PREFIX) && fileName.endsWith(PROMPT_SUFFIX)) return {
		kind: "prompt",
		filePath,
		order
	};
	const match = MESSAGE_DELTA_RE.exec(fileName);
	if (match) return {
		kind: "messageDelta",
		messageId: match[1],
		index: Number(match[2]),
		filePath,
		order
	};
	return null;
}
/**
* メッセージの到着順。index 0 のファイルの到着順を採る。
*
* ★ 「最新のファイルの到着順」ではなく「index 0 の到着順」であること。final:true は
*   大きく遅れて届くため、遅れて増えた delta ファイルの方が新しくなるのが普通に起きる。
*   それに引きずられて到着順が入れ替わらないよう、常に先頭（index 0）を基準にする。
*
* index 0 のファイルが（何らかの理由で）無ければ、手元にある中で最小の到着順を使う。
*/
function arrivalOrderOfMessage(deltas) {
	const zero = deltas.find((d) => d.index === 0);
	if (zero) return zero.order;
	return deltas.reduce((min, d) => d.order < min ? d.order : min, deltas[0].order);
}
/** タイブレーク（同じナノ秒のときに実行のたびに順序が揺れないようにする）用の代表パス */
function tieBreakPath(entry) {
	return entry.kind === "prompt" ? entry.filePath : entry.filePaths[0];
}
/** spool を到着順に走査する。ディレクトリが無いのは正常（まだ hook が動いていない） */
function scanSpool(spoolDir) {
	let fileNames;
	try {
		fileNames = fs.readdirSync(spoolDir);
	} catch {
		return [];
	}
	const prompts = [];
	const messages = /* @__PURE__ */ new Map();
	for (const fileName of fileNames) {
		const filePath = path.join(spoolDir, fileName);
		let stat;
		try {
			stat = fs.statSync(filePath, { bigint: true });
		} catch {
			continue;
		}
		if (!stat.isFile()) continue;
		const classified = classify(fileName, filePath, arrivalOrder(stat));
		if (!classified) continue;
		if (classified.kind === "prompt") {
			prompts.push(classified);
			continue;
		}
		const list = messages.get(classified.messageId) ?? [];
		list.push({
			index: classified.index,
			filePath: classified.filePath,
			order: classified.order
		});
		messages.set(classified.messageId, list);
	}
	const entries = [...prompts];
	for (const [messageId, deltas] of messages) {
		deltas.sort((a, b) => a.index - b.index);
		entries.push({
			kind: "message",
			messageId,
			filePaths: deltas.map((d) => d.filePath),
			progressPath: progressPathFor(messageId, spoolDir),
			order: arrivalOrderOfMessage(deltas)
		});
	}
	return entries.sort((a, b) => {
		if (a.order !== b.order) return a.order < b.order ? -1 : 1;
		return tieBreakPath(a).localeCompare(tieBreakPath(b));
	});
}
function isRecord(value) {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}
function stringOrNull(value) {
	return typeof value === "string" && value ? value : null;
}
/** 1ファイル1 JSON を読む。壊れた・読めないファイルは黙って捨てる（hook が書き込み中の可能性がある） */
function readDeltaFile(filePath) {
	let text;
	try {
		text = fs.readFileSync(filePath, "utf-8");
	} catch {
		return null;
	}
	try {
		const parsed = JSON.parse(text);
		return isRecord(parsed) ? parsed : null;
	} catch {
		return null;
	}
}
/**
* delta ファイル群を読み、index 順に結合できる delta 列にする。
*
* index に欠番があったら**そこで打ち切る**。歯抜けのまま繋ぐと文が壊れて読み上げられるので、
* 欠けた分が届くまで（あるいは孤児掃除に回収されるまで）黙って待つ方を選ぶ。
*
* ★ どの index かは**ファイル名ではなく payload の `index` フィールド**で判定する。
*   ファイル名はグルーピング（scanSpool）にしか使わない。同じ index の payload が複数
*   渡されたら後に読んだ方を勝たせる（呼び出し順は index 昇順で揃っているので、通常は
*   ファイル名どおりの index と一致するが、そうでない場合への備え）。
*/
function readMessage(filePaths) {
	const byIndex = /* @__PURE__ */ new Map();
	let sessionId = null;
	let turnId = null;
	let messageId = null;
	for (const filePath of filePaths) {
		const payload = readDeltaFile(filePath);
		if (!payload) continue;
		const index = payload.index;
		if (typeof index !== "number" || !Number.isInteger(index) || index < 0) continue;
		byIndex.set(index, payload);
		sessionId ??= stringOrNull(payload.session_id);
		turnId ??= stringOrNull(payload.turn_id);
		messageId ??= stringOrNull(payload.message_id);
	}
	const deltas = [];
	let final = false;
	for (let i = 0; byIndex.has(i); i++) {
		const payload = byIndex.get(i);
		deltas.push(typeof payload.delta === "string" ? payload.delta : "");
		if (payload.final === true) final = true;
	}
	return {
		deltas,
		final,
		sessionId,
		turnId,
		messageId
	};
}
/** `prompt-<…>.json` は1イベントで完結するので、payload をそのまま返す */
function readPromptPayload(filePath) {
	try {
		return JSON.parse(fs.readFileSync(filePath, "utf-8"));
	} catch {
		return null;
	}
}
/** 出力済みの文数。サイドカーが無ければ 0 */
function readProgress(progressPath) {
	try {
		const parsed = JSON.parse(fs.readFileSync(progressPath, "utf-8"));
		if (isRecord(parsed)) {
			const emitted = parsed.emitted;
			if (typeof emitted === "number" && Number.isInteger(emitted) && emitted >= 0) return emitted;
		}
	} catch {}
	return 0;
}
/**
* 出力済みの文数を記録する。
*
* ★ atomicWrite を使うこと（素の `writeFileSync` は書きかけを読まれる窓ができる）。
*   ここで書きかけが漏れると 0 バイトのサイドカーが残り、`readProgress` はそれを 0 と
*   読むので、**メッセージが丸ごと最初から読み直される**。WebSocket の契約は `seq` でしか
*   重複排除しないため、クライアントは言い直しと新規発話を区別できない。
*/
function writeProgress(progressPath, emitted) {
	writeFileAtomic(progressPath, `${JSON.stringify({ emitted })}\n`);
}
/** 処理し終えた spool を消す。メッセージは全 delta ファイル + サイドカーを消す */
function removeEntry(entry) {
	if (entry.kind === "prompt") {
		fs.rmSync(entry.filePath, { force: true });
		return;
	}
	for (const filePath of entry.filePaths) fs.rmSync(filePath, { force: true });
	fs.rmSync(entry.progressPath, { force: true });
}
/** メッセージ関連ファイル（delta + 進捗サイドカー）を message_id でグルーピングするための鍵 */
function messageGroupKey(fileName) {
	if (fileName.startsWith(PROMPT_PREFIX)) return null;
	if (fileName.endsWith(PROGRESS_SUFFIX)) return fileName.slice(0, -14);
	const match = MESSAGE_DELTA_RE.exec(fileName);
	return match ? match[1] : null;
}
/**
* CLI が起動しないまま終わった孤児を掃除する。
*
* ★ **メッセージ単位でまとめて判定すること。** 1 delta 1 ファイルにすると、各ファイルの
*   mtime は書かれた瞬間で止まる。ファイル単位で「無活動時間」を見ると、進行中メッセージの
*   古い index のファイルだけが閾値を超えて消え、`index` に欠番ができて**そのメッセージが
*   永久に発話されなくなる**。そのメッセージに属するファイル群の**最新 mtime**を見て、
*   全体が無活動なら delta ファイルとサイドカーをまとめて消す。
*
* `prompt-*.json` と、rename 前の孤立した `.tmp`（このグルーピングに掛からないもの）は
* 従来どおりファイル単位で判定する（1イベントで完結するので、まとめる意味が無い）。
*/
function cleanOrphans(spoolDir, maxAgeMs, now = Date.now()) {
	let fileNames;
	try {
		fileNames = fs.readdirSync(spoolDir);
	} catch {
		return 0;
	}
	const standalone = [];
	const messageGroups = /* @__PURE__ */ new Map();
	for (const fileName of fileNames) {
		const key = messageGroupKey(fileName);
		if (key === null) {
			standalone.push(fileName);
			continue;
		}
		const list = messageGroups.get(key) ?? [];
		list.push(fileName);
		messageGroups.set(key, list);
	}
	let removed = 0;
	for (const fileName of standalone) {
		const filePath = path.join(spoolDir, fileName);
		try {
			if (now - fs.statSync(filePath).mtimeMs <= maxAgeMs) continue;
			fs.rmSync(filePath, { force: true });
			removed++;
		} catch {}
	}
	for (const groupFileNames of messageGroups.values()) {
		const filePaths = groupFileNames.map((fileName) => path.join(spoolDir, fileName));
		let latestMtimeMs = -Infinity;
		for (const filePath of filePaths) try {
			const mtimeMs = fs.statSync(filePath).mtimeMs;
			if (mtimeMs > latestMtimeMs) latestMtimeMs = mtimeMs;
		} catch {}
		if (latestMtimeMs === -Infinity) continue;
		if (now - latestMtimeMs <= maxAgeMs) continue;
		for (const filePath of filePaths) try {
			fs.rmSync(filePath, { force: true });
			removed++;
		} catch {}
	}
	return removed;
}

//#endregion
//#region src/cli/workerState.ts
/**
* ワーカーが**プロセスを跨いで**持ち回る状態。
*
* 上流 cc-mascot の `promptEventMonitor` は常駐プロセスなので、応答待ち通知の重複抑制を
* クロージャの変数で持てた。chatter-agent の CLI は**毎 delta 起動して終了する**ので、
* 同じ抑制を成立させるにはディスクに置くしかない。
*
* 書き込むのはロック保持者だけなので競合しない。
*/
function emptyWorkerState() {
	return {
		pairedPromptId: null,
		pairedPromptAt: 0,
		lastText: "",
		lastTextAt: 0
	};
}
function readWorkerState(statePath) {
	try {
		const parsed = JSON.parse(fs.readFileSync(statePath, "utf-8"));
		if (typeof parsed === "object" && parsed !== null) {
			const record = parsed;
			return {
				pairedPromptId: typeof record.pairedPromptId === "string" ? record.pairedPromptId : null,
				pairedPromptAt: typeof record.pairedPromptAt === "number" ? record.pairedPromptAt : 0,
				lastText: typeof record.lastText === "string" ? record.lastText : "",
				lastTextAt: typeof record.lastTextAt === "number" ? record.lastTextAt : 0
			};
		}
	} catch {}
	return emptyWorkerState();
}
function writeWorkerState(statePath, state) {
	writeFileAtomic(statePath, `${JSON.stringify(state)}\n`);
}

//#endregion
//#region src/cli/worker.ts
/**
* spool のドレイン。ロックを取れた1プロセスだけがここに入る。
*
* 順序の保証（CLAUDE.md「絶対に守ること」4）:
* - spool は**到着順**に処理する
* - 空振り（進展なし）が**2回連続**するまで繰り返す。1回目の空振りの後にもう一周させることが
*   「解放完了後にもう一度 spool を見る」に当たり、直前の走査が終わった直後に到着した分の
*   取りこぼしを防ぐ
*/
/**
* ロック取得に使ってよい合計の待ち時間予算。
*
* ★ 長く待ってよい理由: CLI は hook からデタッチ起動されているので、ここで待っても hook 自体は
*   ブロックしない。長く待っても実害は「node プロセスが数個並ぶ」だけ。
* ★ 長く待つ必要がある理由: `final:true` の delta と `permission_prompt` の Notification は
*   **そのターン最後の hook イベント**。ここでロックを取り損ねると、次に誰かが hook を
*   発火させるまで発話が沈黙する。`AskUserQuestion` の場合、それは**ユーザーが既に回答した後**になる。
*   旧予算（4回試行 × 120ms ≒ 360ms、Node の起動込みで実測 408〜420ms）は、先行 worker が
*   ロックを 500ms 以上保持しただけで超えていた。実測されたドレイン所要時間に対して
*   十分な余裕を持たせ、3秒を予算にする。
*/
const LOCK_MAX_WAIT_MS = 3e3;
/** 再試行の間隔 */
const LOCK_RETRY_DELAY_MS = 120;
/** 同期で待つ。CLI は hook からデタッチ起動されているので、待っても hook はブロックしない */
function sleepSync(ms) {
	Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}
/**
* ロックを取る。取れなければ `LOCK_MAX_WAIT_MS` を使い切るまで再試行する。
*
* ★ 一度で諦めてはいけない。先行ワーカーが最後の走査を終えてから解放するまでの窓に
*   届いた spool は、そのワーカーにも拾われず、こちらが即終了すると誰にも拾われない。
*/
function acquireLockWithRetry(lockDir, options = {}) {
	const maxWaitMs = options.maxWaitMs ?? 3e3;
	const retryDelayMs = options.retryDelayMs ?? LOCK_RETRY_DELAY_MS;
	const sleep = options.sleep ?? sleepSync;
	const now = options.now ?? Date.now;
	const deadline = now() + maxWaitMs;
	for (;;) {
		const lock = acquireLock(lockDir);
		if (lock) return lock;
		if (now() >= deadline) return null;
		sleep(retryDelayMs);
	}
}
/**
* PreToolUse と、それに付随する Notification を同一プロンプトとみなす時間窓。
* AskUserQuestion / ExitPlanMode では PreToolUse の直後に permission_prompt の
* Notification も発火するため、後者を捨てるのに使う（上流 cc-mascot と同じ値）。
*/
const PROMPT_PAIR_WINDOW_MS = 1e4;
/** 同一テキストの連投を抑制する時間窓（許可プロンプトの重複発火対策） */
const DUPLICATE_WINDOW_MS = 3e3;
/** 2回連続で空振りするまで回すが、万一 spool が育ち続けても抜けられるようにする */
const MAX_PASSES = 8;
function sessionIdOf(loaded) {
	return "content" in loaded ? loaded.content.sessionId : getEventSessionId(loaded.payload);
}
function drainSpool(deps) {
	const now = deps.now ?? Date.now;
	const orphansRemoved = cleanOrphans(deps.spoolDir, deps.spoolMaxAgeMs, now());
	const state = readWorkerState(deps.workerStatePath);
	let stateDirty = false;
	let written = 0;
	let passes = 0;
	let unchangedStreak = 0;
	for (; passes < MAX_PASSES; passes++) {
		const entries = scanSpool(deps.spoolDir);
		if (entries.length === 0) break;
		const loaded = entries.map((entry) => entry.kind === "message" ? {
			entry,
			content: readMessage(entry.filePaths)
		} : {
			entry,
			payload: readPromptPayload(entry.filePath)
		});
		let changed = false;
		for (let i = 0; i < loaded.length; i++) {
			const item = loaded[i];
			const outcome = "content" in item ? processMessage(item, hasNewerInSameSession(loaded, i), deps) : processPrompt(item, deps, state, now);
			written += outcome.written;
			if (outcome.changed) changed = true;
			if (outcome.stateDirty) stateDirty = true;
		}
		if (changed) {
			unchangedStreak = 0;
			continue;
		}
		unchangedStreak++;
		if (unchangedStreak >= 2) break;
	}
	if (stateDirty) writeWorkerState(deps.workerStatePath, state);
	return {
		written,
		passes,
		orphansRemoved
	};
}
/**
* このメッセージがもう伸びないと判断してよいか。
*
* ★ 「後続エントリが1つでもあるか」で見てはいけない。`getSpoolDir()` にセッション成分が無く、
*   `MessageDisplay` は matcher 非対応で**全セッションで発火する**ため、Claude Code を2枚開くと
*   別セッションのメッセージで保留が解け、書きかけの断片が読み上げられて順序も壊れる。
*
* session_id が取れないものは判断材料にしない。保留したまま `final` を待つ方が安全。
*/
function hasNewerInSameSession(loaded, index) {
	const sessionId = sessionIdOf(loaded[index]);
	if (sessionId === null) return false;
	return loaded.slice(index + 1).some((other) => sessionIdOf(other) === sessionId);
}
const NOTHING = {
	written: 0,
	changed: false,
	stateDirty: false
};
function processMessage(item, hasNewer, deps) {
	const { entry, content } = item;
	const emitted = readProgress(entry.progressPath);
	const result = assembleSentences({
		deltas: content.deltas,
		emitted,
		final: content.final,
		flushPending: content.final || hasNewer
	});
	let written = 0;
	if (result.sentences.length > 0) {
		const messageId = content.messageId ?? entry.messageId;
		deps.publish(result.sentences.map((text) => ({
			source: "claude-code",
			sessionId: content.sessionId,
			turnId: content.turnId,
			messageId,
			kind: "assistant",
			text,
			emotion: deps.classify(text)
		})));
		written = result.sentences.length;
	}
	if (result.emitted !== emitted) writeProgress(entry.progressPath, result.emitted);
	if (content.final) removeEntry(entry);
	return {
		written,
		changed: written > 0 || content.final,
		stateDirty: false
	};
}
function processPrompt(item, deps, state, now) {
	const { entry, payload } = item;
	if (payload === null) return NOTHING;
	if (!deps.speakPrompts) {
		removeEntry(entry);
		return {
			...NOTHING,
			changed: true
		};
	}
	const messages = formatPromptEvent(payload);
	if (messages.length === 0) {
		removeEntry(entry);
		return {
			...NOTHING,
			changed: true
		};
	}
	let stateDirty = false;
	const hookName = getEventHookName(payload);
	const promptId = getEventPromptId(payload);
	const at = now();
	if (hookName === "Notification" && promptId !== null && promptId === state.pairedPromptId && withinWindow(at, state.pairedPromptAt, PROMPT_PAIR_WINDOW_MS)) {
		state.pairedPromptId = null;
		removeEntry(entry);
		return {
			written: 0,
			changed: true,
			stateDirty: true
		};
	}
	const records = [];
	const sessionId = getEventSessionId(payload);
	for (const message of messages) {
		const cleaned = cleanTextForSpeech(message.text);
		if (!cleaned) continue;
		if (cleaned === state.lastText && withinWindow(at, state.lastTextAt, DUPLICATE_WINDOW_MS)) continue;
		state.lastText = cleaned;
		state.lastTextAt = at;
		stateDirty = true;
		for (const sentence of splitIntoSentences(cleaned)) {
			if (!sentence) continue;
			records.push({
				source: "claude-code",
				sessionId,
				turnId: null,
				messageId: null,
				kind: "prompt",
				text: sentence,
				emotion: deps.classify(sentence)
			});
		}
	}
	if (records.length > 0) deps.publish(records);
	removeEntry(entry);
	if (hookName === "PreToolUse" && promptId !== null) {
		state.pairedPromptId = promptId;
		state.pairedPromptAt = at;
		stateDirty = true;
	}
	return {
		written: records.length,
		changed: true,
		stateDirty
	};
}
/**
* 抑制の時間窓に入っているか。
*
* ★ 経過が負なら窓の外として扱う。両タイムスタンプは `speak.state.json` に永続化されるので、
*   サスペンド/レジュームや NTP で時計が巻き戻ると、本来ペアでない Notification を
*   「ペア済み」と誤判定して**通知が二度と出なくなる**。
*/
function withinWindow(now, since, windowMs) {
	const elapsed = now - since;
	return elapsed >= 0 && elapsed < windowMs;
}

//#endregion
//#region src/cli/index.ts
/**
* `chatter-agent-speak` — hook から**毎 delta 起動される** CLI。
*
* バンドルして `plugin/bin/chatter-agent-speak.mjs` に出す（docs/core.md）。
* npm 依存を持たないこと。ここから到達する範囲は Node 標準だけで閉じている。
*
* やることは短い:
*   1. 無効化されていたら即終了
*   2. ロックを取る。取れなければ `LOCK_MAX_WAIT_MS`（worker.ts）を使い切るまで待って試す
*   3. spool をドレインする
*   4. ロックを解放する
*
* **何があっても exit 0 で終える。** hook の失敗が Claude Code の表示を止めてはいけない。
*/
function main() {
	if (isSpeakDisabled()) return;
	const config = createConfigStore();
	const lock = acquireLockWithRetry(getLockDir());
	if (!lock) return;
	try {
		const speechLog = createSpeechLog({
			logPath: getSpeechLogPath(),
			statePath: getSpeechStatePath(),
			maxBytes: config.get("speechLogMaxBytes")
		});
		const speechQueue = createSpeechQueue(getSpeechQueueDir());
		speechQueue.sweepTmp();
		const classifier = new RuleBasedEmotionClassifier();
		drainSpool({
			spoolDir: getSpoolDir(),
			publish: createPublisher({
				speechLog,
				speechQueue,
				maxEntries: () => config.get("speechQueueMaxEntries")
			}),
			workerStatePath: getWorkerStatePath(),
			speakPrompts: config.get("speakPrompts"),
			spoolMaxAgeMs: config.get("spoolMaxAgeHours") * 60 * 60 * 1e3,
			classify: (text) => classifier.classify(text)
		});
	} finally {
		lock.release();
	}
}
try {
	main();
} catch (err) {
	console.error("[chatter-agent-speak]", err);
}
process.exit(0);

//#endregion
export {  };