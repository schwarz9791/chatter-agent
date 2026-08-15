/**
 * 応答待ちフックイベント → 読み上げメッセージの変換
 *
 * Claude Code のフック（PreToolUse / Notification）が stdin で受け取った JSON を
 * そのまま受け取り、読み上げるテキストに組み立てる。
 *
 * ログ監視ではなくフックを使う理由: jsonl のアシスタント行はツール結果と一緒に
 * flush されるため、AskUserQuestion の tool_use 行がファイルに現れるのは
 * ユーザーが回答した後になる。フックはツール実行の直前に発火するので間に合う。
 */

// 改変: 上流は adapters/harnessAdapter から型を取っていたが、adapters/ は移植しない
// （対象が Claude Code のみでログ形式の抽象化が不要）ため、契約側から取り込む。
import type { SpeakMessage } from "../core/types";

/** ExitPlanMode の読み上げ文の定型部分（計画の見出しが取れない場合はこれだけを話す） */
const PLAN_APPROVAL_TEXT = "計画がまとまりました。確認をお願いします。";

/** 許可プロンプトの読み上げ文（通知本文が取れない場合のフォールバック） */
const PERMISSION_PROMPT_TEXT = "許可を求めています。確認をお願いします。";

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * AskUserQuestion の入力から、質問ごとの読み上げ文を組み立てる
 * 例: 「どちらにしますか？ 選択肢は、そのまま進める、やり直す。」
 * 選択肢の description は長くなりすぎるため読み上げない
 */
function buildQuestionTexts(input: unknown): string[] {
  if (!isRecord(input) || !Array.isArray(input.questions)) return [];

  const texts: string[] = [];
  for (const question of input.questions) {
    if (!isRecord(question) || typeof question.question !== "string") continue;

    const questionText = question.question.trim();
    if (!questionText) continue;

    const labels = Array.isArray(question.options)
      ? question.options
          .filter(isRecord)
          .map((option) => (typeof option.label === "string" ? option.label.trim() : ""))
          .filter((label) => label.length > 0)
      : [];

    texts.push(labels.length > 0 ? `${questionText} 選択肢は、${labels.join("、")}。` : questionText);
  }
  return texts;
}

/**
 * ExitPlanMode の入力から承認依頼の読み上げ文を組み立てる
 * plan は計画の全文マークダウン（数千字）なので、先頭の見出しだけを使う
 */
function buildPlanApprovalText(input: unknown): string {
  const plan = isRecord(input) && typeof input.plan === "string" ? input.plan : "";
  const heading = plan.match(/^#{1,6}\s+(.+)$/m)?.[1].trim();
  return heading ? `「${heading}」の${PLAN_APPROVAL_TEXT}` : PLAN_APPROVAL_TEXT;
}

/** フックの payload に含まれるセッションIDを取り出す（取れなければ null） */
export function getEventSessionId(payload: unknown): string | null {
  return getStringField(payload, "session_id");
}

/**
 * フックの payload に含まれるプロンプトIDを取り出す（取れなければ null）
 * PreToolUse と、その直後に発火する Notification は同じ prompt_id を持つ
 */
export function getEventPromptId(payload: unknown): string | null {
  return getStringField(payload, "prompt_id");
}

/** フックの payload に含まれるイベント名を取り出す（取れなければ null） */
export function getEventHookName(payload: unknown): string | null {
  return getStringField(payload, "hook_event_name");
}

function getStringField(payload: unknown, key: string): string | null {
  if (!isRecord(payload)) return null;
  const value = payload[key];
  return typeof value === "string" && value ? value : null;
}

/**
 * フックの payload を読み上げメッセージに変換する
 * 対象外のイベントや壊れた payload の場合は空配列を返す
 */
export function formatPromptEvent(payload: unknown): SpeakMessage[] {
  if (!isRecord(payload)) return [];

  const texts = buildTexts(payload);
  return texts
    .map((text) => text.trim())
    .filter((text) => text.length > 0)
    .map((text) => ({ type: "speak" as const, text, kind: "prompt" as const }));
}

function buildTexts(payload: Record<string, unknown>): string[] {
  switch (payload.hook_event_name) {
    case "PreToolUse":
      // 応答待ちで止まるツールのみが対象（フック側の matcher でも絞っている）
      if (payload.tool_name === "AskUserQuestion") return buildQuestionTexts(payload.tool_input);
      if (payload.tool_name === "ExitPlanMode") return [buildPlanApprovalText(payload.tool_input)];
      return [];

    case "Notification":
      // matcher で permission_prompt に絞っているため、届いた時点で許可プロンプト。
      // event 固有フィールドは公式ドキュメントに記載がないため message の有無に依存しない
      return [typeof payload.message === "string" && payload.message.trim() ? payload.message : PERMISSION_PROMPT_TEXT];

    default:
      return [];
  }
}
