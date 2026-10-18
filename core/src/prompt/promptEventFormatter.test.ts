import { describe, it, expect } from "vitest";
import { formatPromptEvent, getEventHookName, getEventPromptId, getEventSessionId } from "./promptEventFormatter";

function preToolUse(toolName: string, toolInput: unknown) {
  return {
    hook_event_name: "PreToolUse",
    session_id: "session-abc",
    tool_name: toolName,
    tool_input: toolInput,
  };
}

describe("formatPromptEvent", () => {
  describe("PreToolUse - AskUserQuestion", () => {
    it("質問文と選択肢のlabelを読み上げる", () => {
      const result = formatPromptEvent(
        preToolUse("AskUserQuestion", {
          questions: [
            {
              question: "どちらにしますか？",
              header: "方針",
              options: [
                { label: "そのまま進める", description: "この説明は読み上げない" },
                { label: "やり直す", description: "これも読み上げない" },
              ],
            },
          ],
        }),
        "ja",
      );

      expect(result).toEqual([
        { type: "speak", text: "どちらにしますか？ 選択肢は、そのまま進める、やり直す。", kind: "prompt" },
      ]);
    });

    it("複数の質問はそれぞれ別のメッセージになる", () => {
      const result = formatPromptEvent(
        preToolUse("AskUserQuestion", {
          questions: [
            { question: "1つ目は？", options: [{ label: "A" }, { label: "B" }] },
            { question: "2つ目は？", options: [{ label: "C" }] },
          ],
        }),
        "ja",
      );

      expect(result.map((m) => m.text)).toEqual(["1つ目は？ 選択肢は、A、B。", "2つ目は？ 選択肢は、C。"]);
    });

    it("選択肢がなければ質問文のみ読み上げる", () => {
      const result = formatPromptEvent(
        preToolUse("AskUserQuestion", { questions: [{ question: "どうしますか？", options: [] }] }),
        "ja",
      );

      expect(result).toHaveLength(1);
      expect(result[0].text).toBe("どうしますか？");
    });

    it("入力が壊れていても例外にならない", () => {
      expect(formatPromptEvent(preToolUse("AskUserQuestion", { questions: [] }), "ja")).toEqual([]);
      expect(formatPromptEvent(preToolUse("AskUserQuestion", { questions: [{ header: "質問文なし" }] }), "ja")).toEqual(
        [],
      );
      expect(formatPromptEvent(preToolUse("AskUserQuestion", null), "ja")).toEqual([]);
    });
  });

  describe("PreToolUse - ExitPlanMode", () => {
    it("計画の先頭見出しと定型文を読み上げる（本文は読まない）", () => {
      const result = formatPromptEvent(
        preToolUse("ExitPlanMode", {
          plan: "# ログ監視の改善\n\n## Context\n\nここは読み上げられない長い本文。\n",
          planFilePath: "/tmp/plan.md",
        }),
        "ja",
      );

      expect(result).toEqual([
        { type: "speak", text: "「ログ監視の改善」の計画がまとまりました。確認をお願いします。", kind: "prompt" },
      ]);
    });

    it("見出しがなければ定型文のみ読み上げる", () => {
      const result = formatPromptEvent(preToolUse("ExitPlanMode", { plan: "見出しのない計画本文です。" }), "ja");

      expect(result[0].text).toBe("計画がまとまりました。確認をお願いします。");
    });
  });

  describe("Notification（許可プロンプト）", () => {
    it("message があればそれを読み上げる", () => {
      const result = formatPromptEvent(
        {
          hook_event_name: "Notification",
          session_id: "session-abc",
          message: "Claude needs your permission to use Bash",
        },
        "ja",
      );

      expect(result).toEqual([{ type: "speak", text: "Claude needs your permission to use Bash", kind: "prompt" }]);
    });

    it("message がなければ定型文にフォールバックする", () => {
      const result = formatPromptEvent({ hook_event_name: "Notification", session_id: "session-abc" }, "ja");

      expect(result[0].text).toBe("許可を求めています。確認をお願いします。");
    });

    it("message が空文字列でも定型文にフォールバックする", () => {
      const result = formatPromptEvent({ hook_event_name: "Notification", message: "   " }, "ja");

      expect(result[0].text).toBe("許可を求めています。確認をお願いします。");
    });
  });

  describe("対象外のイベント", () => {
    it("読み上げ対象外のツールは空配列を返す", () => {
      expect(formatPromptEvent(preToolUse("Bash", { command: "ls -la" }), "ja")).toEqual([]);
      expect(formatPromptEvent(preToolUse("Edit", { file_path: "/tmp/a.ts" }), "ja")).toEqual([]);
    });

    it("未知の hook_event_name は空配列を返す", () => {
      expect(formatPromptEvent({ hook_event_name: "PostToolUse", tool_name: "AskUserQuestion" }, "ja")).toEqual([]);
    });

    it("payload がオブジェクトでなければ空配列を返す", () => {
      expect(formatPromptEvent(null, "ja")).toEqual([]);
      expect(formatPromptEvent("文字列", "ja")).toEqual([]);
      expect(formatPromptEvent([1, 2, 3], "ja")).toEqual([]);
    });
  });
});

describe("formatPromptEvent（英語）", () => {
  it("質問文と選択肢のlabelを英語の定型文で読み上げる", () => {
    const result = formatPromptEvent(
      preToolUse("AskUserQuestion", {
        questions: [{ question: "Which one?", options: [{ label: "A" }, { label: "B" }] }],
      }),
      "en",
    );

    expect(result).toEqual([{ type: "speak", text: "Which one? The options are A or B.", kind: "prompt" }]);
  });

  it("ラベルにカンマを含む2択は or でつなぐ", () => {
    const result = formatPromptEvent(
      preToolUse("AskUserQuestion", {
        questions: [{ question: "Proceed?", options: [{ label: "Yes, continue" }, { label: "No, stop" }] }],
      }),
      "en",
    );

    expect(result).toEqual([
      { type: "speak", text: "Proceed? The options are Yes, continue or No, stop.", kind: "prompt" },
    ]);
  });

  it("3択は A, B, or C でつなぐ", () => {
    const result = formatPromptEvent(
      preToolUse("AskUserQuestion", {
        questions: [{ question: "Which one?", options: [{ label: "A" }, { label: "B" }, { label: "C" }] }],
      }),
      "en",
    );

    expect(result).toEqual([{ type: "speak", text: "Which one? The options are A, B, or C.", kind: "prompt" }]);
  });

  it("選択肢がなければ質問文のみ読み上げる", () => {
    const result = formatPromptEvent(
      preToolUse("AskUserQuestion", { questions: [{ question: "What now?", options: [] }] }),
      "en",
    );

    expect(result[0].text).toBe("What now?");
  });

  it("計画の見出しがあれば定型文に含める", () => {
    const result = formatPromptEvent(preToolUse("ExitPlanMode", { plan: "# Improve logging\n\nbody\n" }), "en");

    expect(result[0].text).toBe('The plan "Improve logging" is ready. Please take a look.');
  });

  it("見出しがなければ定型文のみ読み上げる", () => {
    const result = formatPromptEvent(preToolUse("ExitPlanMode", { plan: "no heading" }), "en");

    expect(result[0].text).toBe("The plan is ready. Please take a look.");
  });

  it("許可プロンプトは message が無ければ定型文、あればそのまま読み上げる", () => {
    expect(formatPromptEvent({ hook_event_name: "Notification" }, "en")[0].text).toBe(
      "I need your permission. Please take a look.",
    );
    expect(formatPromptEvent({ hook_event_name: "Notification", message: "   " }, "en")[0].text).toBe(
      "I need your permission. Please take a look.",
    );
    expect(formatPromptEvent({ hook_event_name: "Notification", message: "許可を。" }, "en")[0].text).toBe("許可を。");
  });
});

describe("getEventSessionId", () => {
  it("session_id を取り出す", () => {
    expect(getEventSessionId({ session_id: "abc-123" })).toBe("abc-123");
  });

  it("session_id がなければ null を返す", () => {
    expect(getEventSessionId({})).toBeNull();
    expect(getEventSessionId({ session_id: "" })).toBeNull();
    expect(getEventSessionId(null)).toBeNull();
  });
});

describe("getEventPromptId", () => {
  it("prompt_id を取り出す", () => {
    expect(getEventPromptId({ prompt_id: "prompt-1" })).toBe("prompt-1");
  });

  it("prompt_id がなければ null を返す", () => {
    expect(getEventPromptId({})).toBeNull();
    expect(getEventPromptId({ prompt_id: "" })).toBeNull();
    expect(getEventPromptId(null)).toBeNull();
  });
});

describe("getEventHookName", () => {
  it("hook_event_name を取り出す", () => {
    expect(getEventHookName({ hook_event_name: "Notification" })).toBe("Notification");
  });

  it("hook_event_name がなければ null を返す", () => {
    expect(getEventHookName({})).toBeNull();
    expect(getEventHookName(undefined)).toBeNull();
  });
});
