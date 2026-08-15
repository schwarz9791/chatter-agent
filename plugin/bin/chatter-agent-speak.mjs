#!/usr/bin/env node
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
/** 発話ログの現世代。全体の契約（設計書 §5） */
function getSpeechLogPath(e = currentPathEnv()) {
	return path.join(getRuntimeDir(e), "speech.jsonl");
}
/**
* ローテート後の世代パス。`speech.jsonl` → `speech.1.jsonl`。
* 世代番号は 1 始まり（0 は現世代 = `basePath` そのもの）。
*/
function getSpeechLogGenerationPath(basePath, generation) {
	if (generation <= 0) return basePath;
	const dir = path.dirname(basePath);
	const ext = path.extname(basePath);
	const stem = path.basename(basePath, ext);
	return path.join(dir, `${stem}.${generation}${ext}`);
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
		speechLogGenerations: 3,
		spoolMaxAgeHours: 6
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
const parseNonEmptyString = (raw) => typeof raw === "string" && raw.trim() ? raw : void 0;
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
	speechLogGenerations: {
		env: "CHATTER_AGENT_SPEECH_LOG_GENERATIONS",
		parse: parsePositiveInt
	},
	spoolMaxAgeHours: {
		env: "CHATTER_AGENT_SPOOL_MAX_AGE_HOURS",
		parse: parsePositiveInt
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
			warnOnce("file:shape", `[Config] ${filePath} のトップレベルがオブジェクトではありません。既定値を使います`);
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
//#region src/core/speechLog.ts
/**
* `speech.jsonl` への追記・ローテート・`seq` 採番。
*
* これが全体の契約（設計書 §5）を書き出す唯一の場所。WebSocket はこの行をそのまま配信する。
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
					if (typeof seq === "number" && Number.isInteger(seq)) return seq;
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
			if (typeof next === "number" && Number.isInteger(next) && next >= 1) return next;
		}
	} catch {}
	return 1;
}
function writeStateNextSeq(statePath, nextSeq) {
	const tmp = `${statePath}.tmp`;
	fs.writeFileSync(tmp, `${JSON.stringify({ nextSeq })}\n`);
	fs.renameSync(tmp, statePath);
}
function createSpeechLog(deps) {
	const { logPath, statePath, maxBytes, generations } = deps;
	const now = deps.now ?? (() => /* @__PURE__ */ new Date());
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
		if (lastSeq === 0) lastSeq = readLastSeq(getSpeechLogGenerationPath(logPath, 1));
		return Math.max(readStateNextSeq(statePath), lastSeq + 1);
	}
	let nextSeq = reconcile();
	/** 世代を1つずつ繰り下げ、最古を捨てる */
	function rotate() {
		const oldest = getSpeechLogGenerationPath(logPath, generations);
		fs.rmSync(oldest, { force: true });
		for (let g = generations - 1; g >= 1; g--) {
			const from = getSpeechLogGenerationPath(logPath, g);
			if (fs.existsSync(from)) fs.renameSync(from, getSpeechLogGenerationPath(logPath, g + 1));
		}
		if (fs.existsSync(logPath)) fs.renameSync(logPath, getSpeechLogGenerationPath(logPath, 1));
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
//#region src/cli/lock.ts
/**
* 単一ワーカーのロック。
*
* CLI は hook から**毎 delta 起動される**が、spool を処理してよいのは
* ロックを取れた1プロセスだけ（CLAUDE.md「絶対に守ること」4）。`seq` の採番も
* このロック下で行う。取れなかったプロセスは何もせず即終了する（先行ワーカーが拾う）。
*
* `src/cli/` は npm 依存を持てない（docs/core.md）ので、ロックライブラリは使わず
* **`mkdir` の原子性**で実装する。同名ディレクトリの作成は、成功するのが必ず1プロセスだけ。
*/
const DEFAULT_STALE_MS = 6e4;
function pidFilePath(lockDir) {
	return path.join(lockDir, "pid");
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
* 「古い」だけでは足りない（要約などで長く走っているワーカーを殺してしまう）ので、
* **プロセスが死んでいること**と**十分に古いこと**の両方を要求する。
* pid が読めない（作成途中 / 壊れている）ロックは、古い場合にだけ奪う。
*/
function isStale(lockDir, staleMs, now) {
	let age;
	try {
		age = now - fs.statSync(lockDir).mtimeMs;
	} catch {
		return false;
	}
	if (age < staleMs) return false;
	try {
		const pid = Number.parseInt(fs.readFileSync(pidFilePath(lockDir), "utf-8").trim(), 10);
		if (!Number.isInteger(pid) || pid <= 0) return true;
		return !isProcessAlive(pid);
	} catch {
		return true;
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
	fs.mkdirSync(path.dirname(lockDir), { recursive: true });
	const tryCreate = () => {
		try {
			fs.mkdirSync(lockDir);
		} catch (err) {
			if (err.code === "EEXIST") return false;
			throw err;
		}
		try {
			fs.writeFileSync(pidFilePath(lockDir), `${pid}\n`);
		} catch {}
		return true;
	};
	if (tryCreate()) return makeLock(lockDir);
	if (!isStale(lockDir, staleMs, now())) return null;
	fs.rmSync(lockDir, {
		recursive: true,
		force: true
	});
	return tryCreate() ? makeLock(lockDir) : null;
}
function makeLock(lockDir) {
	let released = false;
	return { release() {
		if (released) return;
		released = true;
		fs.rmSync(lockDir, {
			recursive: true,
			force: true
		});
	} };
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
//#region src/text/pendingFence.ts
/**
* 未閉じコードフェンスの保留。
*
* chatter-agent 固有の要件で、上流 cc-mascot には存在しない。
*
* `cleanTextForSpeech` のコードブロック除去は ```` /```[\s\S]*?```/g ```` で、
* **閉じフェンスが揃って初めて**ブロックを消す。ストリーミング中の raw には
* 開いたままの ``` が普通に現れるので、そのまま流すとコードが読み上げられる。
*
* 開いたままのフェンスより後ろを切り落としてから整形すれば、
*
* - 未閉じの間はコードが漏れない
* - フェンスが閉じた瞬間にブロック全体が消えても、**既に出した文は変化しない**
*
* の両方が同時に成立する。後者は「出力済みの文数」で進捗を持つ設計の前提条件になっている。
*/
const FENCE = "```";
/**
* 開いたままのコードフェンスがあれば、その開始位置より後ろを切り落とす。
* フェンスが揃っていれば元の文字列をそのまま返す。
*
* フェンスの数え方は `cleanTextForSpeech` の正規表現に合わせ、
* 左から順に非重複で ``` を拾い、奇数個目を開き・偶数個目を閉じとして扱う。
* 行頭かどうかは見ない（正規表現も見ていないため）。
*/
function truncateAtUnclosedFence(text) {
	let searchFrom = 0;
	let openedAt = -1;
	let isOpen = false;
	for (;;) {
		const found = text.indexOf(FENCE, searchFrom);
		if (found === -1) break;
		if (isOpen) isOpen = false;
		else {
			isOpen = true;
			openedAt = found;
		}
		searchFrom = found + 3;
	}
	return isOpen ? text.slice(0, openedAt) : text;
}

//#endregion
//#region src/cli/messageAssembler.ts
/**
* delta の集合から「確定した文」だけを切り出す。chatter-agent の中核。
*
* ★ `final:true` を待ってはいけない（CLAUDE.md「絶対に守ること」1 / 設計書 §2-4）。
*   最終チャンクは実測で 34〜80 秒遅れて届く。メッセージが閉じるとき＝次のツール呼び出しが
*   始まるときに flush され、その手前の thinking を待つため。
*
* そこで delta が届くたびに全体を組み直し、**最後の文を除いた**未出力分だけを流す。
* 最後の文はまだ伸びうるので保留する。
*
* この関数が純粋であることが重要で、CLI は毎 delta 起動して終了するため、
* 状態は「出力済みの文数」だけをディスクに持ち、テキストは毎回ゼロから組み直す。
*/
/**
* 全文を組み直し、確定した文のうち未出力のものを返す。
*
* 未閉じの ``` 以降を先に切り落とすのが要で、これにより
* 「開いたままのコードが読み上げられない」と「既に出した文が後から変化しない」が
* 同時に成立する。後者が `emitted`（文数）で進捗を持てる根拠になっている。
*/
function assembleSentences(input) {
	const raw = input.deltas.join("");
	const safe = truncateAtUnclosedFence(raw);
	const cleaned = cleanTextForSpeech(safe);
	const all = splitIntoSentences(cleaned).filter((sentence) => sentence.length > 0);
	const limit = input.flushPending ? all.length : Math.max(0, all.length - 1);
	const emitted = Math.max(input.emitted, limit);
	return {
		sentences: input.emitted < limit ? all.slice(input.emitted, limit) : [],
		emitted
	};
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
* ★ **spool の `.jsonl` は絶対に書き換えない。** hook が並行して追記している。
*   ワーカーが持つ進捗は `<message_id>.progress.json` のサイドカーに置く。
*/
const MESSAGE_SUFFIX = ".jsonl";
const PROMPT_PREFIX = "prompt-";
const PROMPT_SUFFIX = ".json";
const PROGRESS_SUFFIX = ".progress.json";
function progressPathFor(filePath) {
	return filePath.slice(0, -6) + PROGRESS_SUFFIX;
}
/**
* 到着順のキー。
*
* ★ mtime を使わないこと。`<message_id>.jsonl` は delta ごとに追記されるので mtime が動き続け、
*   先に始まったメッセージが後から追記されて順番が入れ替わる。birthtime が本来の「到着順」。
*   birthtime を持たない環境では mtime に落とす。
*
* ★ ミリ秒（`birthtimeMs`）では粗すぎる。同じミリ秒に作られたファイルが同値になり、
*   下のタイブレーク（パス順）に落ちて到着順が壊れる。`bigint: true` の統計情報が持つ
*   ナノ秒を使う。ナノ秒は number の安全整数を超えるので bigint のまま扱う。
*/
function arrivalOrder(stat) {
	return stat.birthtimeNs > 0n ? stat.birthtimeNs : stat.mtimeNs;
}
function classify(fileName, filePath, order) {
	if (fileName.endsWith(PROGRESS_SUFFIX)) return null;
	if (fileName.startsWith(PROMPT_PREFIX) && fileName.endsWith(PROMPT_SUFFIX)) return {
		kind: "prompt",
		filePath,
		order
	};
	if (fileName.endsWith(MESSAGE_SUFFIX)) return {
		kind: "message",
		messageId: fileName.slice(0, -6),
		filePath,
		progressPath: progressPathFor(filePath),
		order
	};
	return null;
}
/** spool を到着順に走査する。ディレクトリが無いのは正常（まだ hook が動いていない） */
function scanSpool(spoolDir) {
	let fileNames;
	try {
		fileNames = fs.readdirSync(spoolDir);
	} catch {
		return [];
	}
	const entries = [];
	for (const fileName of fileNames) {
		const filePath = path.join(spoolDir, fileName);
		let stat;
		try {
			stat = fs.statSync(filePath, { bigint: true });
		} catch {
			continue;
		}
		if (!stat.isFile()) continue;
		const entry = classify(fileName, filePath, arrivalOrder(stat));
		if (entry) entries.push(entry);
	}
	return entries.sort((a, b) => {
		if (a.order !== b.order) return a.order < b.order ? -1 : 1;
		return a.filePath.localeCompare(b.filePath);
	});
}
function isRecord(value) {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}
function stringOrNull(value) {
	return typeof value === "string" && value ? value : null;
}
/** 壊れた行・途中で切れた行は黙って捨てる（hook が書き込み中の可能性がある） */
function parseLines(filePath) {
	let text;
	try {
		text = fs.readFileSync(filePath, "utf-8");
	} catch {
		return [];
	}
	const out = [];
	for (const line of text.split("\n")) {
		if (!line.trim()) continue;
		try {
			const parsed = JSON.parse(line);
			if (isRecord(parsed)) out.push(parsed);
		} catch {}
	}
	return out;
}
/**
* `<message_id>.jsonl` を読み、index 順に結合できる delta 列にする。
*
* index に欠番があったら**そこで打ち切る**。歯抜けのまま繋ぐと文が壊れて読み上げられるので、
* 欠けた分が届くまで（あるいは孤児掃除に回収されるまで）黙って待つ方を選ぶ。
*/
function readMessage(filePath) {
	const byIndex = /* @__PURE__ */ new Map();
	let sessionId = null;
	let turnId = null;
	let messageId = null;
	for (const payload of parseLines(filePath)) {
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
function writeProgress(progressPath, emitted) {
	fs.writeFileSync(progressPath, `${JSON.stringify({ emitted })}\n`);
}
/** 処理し終えた spool を消す。メッセージはサイドカーごと消す */
function removeEntry(entry) {
	fs.rmSync(entry.filePath, { force: true });
	if (entry.kind === "message") fs.rmSync(entry.progressPath, { force: true });
}
/**
* CLI が起動しないまま終わった孤児を掃除する。
* 進行中のメッセージは delta のたびに mtime が更新されるので、ここでは mtime で「無活動時間」を見る。
*/
function cleanOrphans(spoolDir, maxAgeMs, now = Date.now()) {
	let fileNames;
	try {
		fileNames = fs.readdirSync(spoolDir);
	} catch {
		return 0;
	}
	let removed = 0;
	for (const fileName of fileNames) {
		const filePath = path.join(spoolDir, fileName);
		try {
			if (now - fs.statSync(filePath).mtimeMs <= maxAgeMs) continue;
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
	const tmp = `${statePath}.tmp`;
	fs.writeFileSync(tmp, `${JSON.stringify(state)}\n`);
	fs.renameSync(tmp, statePath);
}

//#endregion
//#region src/cli/worker.ts
/**
* spool のドレイン。ロックを取れた1プロセスだけがここに入る。
*
* 順序の保証（CLAUDE.md「絶対に守ること」4）:
* - spool は**到着順**に処理する
* - ドレインが空振りするまで繰り返す。これが「解放前にもう一度 spool を見る」に当たり、
*   走査直後に到着した分の取りこぼしを防ぐ
*/
/**
* PreToolUse と、それに付随する Notification を同一プロンプトとみなす時間窓。
* AskUserQuestion / ExitPlanMode では PreToolUse の直後に permission_prompt の
* Notification も発火するため、後者を捨てるのに使う（上流 cc-mascot と同じ値）。
*/
const PROMPT_PAIR_WINDOW_MS = 1e4;
/** 同一テキストの連投を抑制する時間窓（許可プロンプトの重複発火対策） */
const DUPLICATE_WINDOW_MS = 3e3;
/** 空振りするまで回すが、万一 spool が育ち続けても抜けられるようにする */
const MAX_PASSES = 8;
function drainSpool(deps) {
	const now = deps.now ?? Date.now;
	const orphansRemoved = cleanOrphans(deps.spoolDir, deps.spoolMaxAgeMs, now());
	const state = readWorkerState(deps.workerStatePath);
	let stateDirty = false;
	let written = 0;
	let passes = 0;
	for (; passes < MAX_PASSES; passes++) {
		const entries = scanSpool(deps.spoolDir);
		if (entries.length === 0) break;
		let changed = false;
		for (let i = 0; i < entries.length; i++) {
			const hasNewer = i < entries.length - 1;
			const outcome = processEntry(entries[i], hasNewer, deps, state, now);
			written += outcome.written;
			if (outcome.changed) changed = true;
			if (outcome.stateDirty) stateDirty = true;
		}
		if (!changed) break;
	}
	if (stateDirty) writeWorkerState(deps.workerStatePath, state);
	return {
		written,
		passes,
		orphansRemoved
	};
}
const NOTHING = {
	written: 0,
	changed: false,
	stateDirty: false
};
function processEntry(entry, hasNewer, deps, state, now) {
	return entry.kind === "message" ? processMessage(entry, hasNewer, deps) : processPrompt(entry, deps, state, now);
}
function processMessage(entry, hasNewer, deps) {
	const content = readMessage(entry.filePath);
	const emitted = readProgress(entry.progressPath);
	const result = assembleSentences({
		deltas: content.deltas,
		emitted,
		flushPending: content.final || hasNewer
	});
	let written = 0;
	if (result.sentences.length > 0) {
		const messageId = content.messageId ?? entry.messageId;
		deps.speechLog.append(result.sentences.map((text) => ({
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
function processPrompt(entry, deps, state, now) {
	const payload = readPromptPayload(entry.filePath);
	removeEntry(entry);
	if (!deps.speakPrompts || payload === null) return {
		...NOTHING,
		changed: true
	};
	const messages = formatPromptEvent(payload);
	if (messages.length === 0) return {
		...NOTHING,
		changed: true
	};
	let stateDirty = false;
	const hookName = getEventHookName(payload);
	const promptId = getEventPromptId(payload);
	const at = now();
	if (hookName === "Notification" && promptId !== null && promptId === state.pairedPromptId && at - state.pairedPromptAt < PROMPT_PAIR_WINDOW_MS) {
		state.pairedPromptId = null;
		return {
			written: 0,
			changed: true,
			stateDirty: true
		};
	}
	let written = 0;
	const records = [];
	const sessionId = typeof payload.session_id === "string" ? payload.session_id : null;
	for (const message of messages) {
		const cleaned = cleanTextForSpeech(message.text);
		if (!cleaned) continue;
		if (cleaned === state.lastText && at - state.lastTextAt < DUPLICATE_WINDOW_MS) continue;
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
	if (records.length > 0) {
		deps.speechLog.append(records);
		written = records.length;
	}
	if (hookName === "PreToolUse" && promptId !== null) {
		state.pairedPromptId = promptId;
		state.pairedPromptAt = at;
		stateDirty = true;
	}
	return {
		written,
		changed: true,
		stateDirty
	};
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
*   2. ロックを取る。取れなければ即終了（先行ワーカーが拾う）
*   3. spool をドレインする
*   4. ロックを解放する
*
* **何があっても exit 0 で終える。** hook の失敗が Claude Code の表示を止めてはいけない。
*/
function main() {
	if (process.env.CHATTER_AGENT_DISABLE) return;
	const config = createConfigStore();
	const lock = acquireLock(getLockDir());
	if (!lock) return;
	try {
		const speechLog = createSpeechLog({
			logPath: getSpeechLogPath(),
			statePath: getSpeechStatePath(),
			maxBytes: config.get("speechLogMaxBytes"),
			generations: config.get("speechLogGenerations")
		});
		const classifier = new RuleBasedEmotionClassifier();
		drainSpool({
			spoolDir: getSpoolDir(),
			speechLog,
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