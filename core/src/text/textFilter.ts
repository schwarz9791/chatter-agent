/**
 * Originally from kazakago/cc-mascot (Apache-2.0, Copyright 2026 kazakago)
 *   electron/filters/textFilter.ts @ 46f7def
 * Modified for chatter-agent.
 */

/**
 * Text filtering utilities for speech synthesis
 * Removes markdown syntax and other elements that shouldn't be spoken
 */

/**
 * Clean text for speech synthesis by removing markdown syntax and
 * replacing special characters with readable alternatives
 */
export function cleanTextForSpeech(text: string): string {
  let cleaned = text;

  // 1. Remove code blocks (```...```)
  cleaned = cleaned.replace(/```[\s\S]*?```/g, "");

  // 2. Remove XML/HTML tags (<example>, </example>, etc.)
  cleaned = cleaned.replace(/<[^>]+>/g, "");

  // 3. Remove markdown headings (##, ###, etc.)
  cleaned = cleaned.replace(/^#{1,6}\s+/gm, "");

  // 4. Remove horizontal rules (---, ***)
  cleaned = cleaned.replace(/^[-*]{3,}$/gm, "");

  // 5. Remove table syntax (|...|)
  cleaned = cleaned.replace(/^\|.*\|$/gm, "");

  // 6. Remove blockquote markers (>)
  cleaned = cleaned.replace(/^>\s*/gm, "");

  // 7. Remove list markers (-, *)
  cleaned = cleaned.replace(/^[-*]\s+/gm, "");

  // 8. Remove URLs (https://...)
  cleaned = cleaned.replace(/https?:\/\/[^\s]+/g, "");

  // 9. Remove git commit hashes (7-40 character hex strings)
  cleaned = cleaned.replace(/\b[0-9a-f]{7,40}\b/g, "");

  // 10. Remove inline code backticks but keep the content  (`...`)
  cleaned = cleaned.replace(/`([^`]+)`/g, "$1");

  return cleaned;
}

// English abbreviations whose trailing period is not a sentence end.
const ABBREVIATIONS = "e\\.g|i\\.e|etc|vs|Mr|Mrs|Ms|Dr|Prof|Jr|Sr|St|approx";

/**
 * Split text into individual sentences for sequential speech synthesis.
 * Splits on Japanese sentence-ending punctuation (。！？!?) and newlines, and on an
 * English sentence-ending period followed by whitespace. A period belonging to an
 * abbreviation, a list number at the start of a line, a decimal, a version number, or an
 * ellipsis is not a split point.
 * Returns trimmed sentences (including empty strings as spacing information).
 */
export function splitIntoSentences(text: string): string[] {
  // Split on sentence-ending punctuation (keeping the punctuation attached) and newlines
  const parts = text.split(
    new RegExp(
      `(?<=[。！？!?])|(?<!\\.\\.)(?<!\\b(?:${ABBREVIATIONS})\\.)(?<!(?:^|[\\n\\r])[ \\t]*\\d+\\.)(?<=\\.)(?=[ \\t])|[\\n\\r]+`,
      "i",
    ),
  );

  return parts.map((s) => s.trim());
}
