/**
 * A piece of an answer, ready to render without innerHTML:
 * - `text`: plain text (shown with white-space: pre-wrap, so lists and line breaks stay).
 * - `citation`: a `[n]` whose n is the index of one of the answer's sources.
 * - `code`: `inline code` or a ``` fenced block (the runbooks are full of commands).
 */
export type MessageSegment =
  | { kind: 'text'; text: string }
  | { kind: 'citation'; index: number }
  | { kind: 'code'; text: string; block: boolean };

// Only digits between brackets are citations: "[conversación previa]" (which the model
// sometimes writes) or "[nota]" stay as text.
const CITATION = /\[(\d+)\]/g;
const CODE = /```[^\n`]*\n?([\s\S]*?)```|`([^`\n]+)`/g;

/**
 * Splits an answer into segments. `sourceIndexes` are the `index` values of the
 * answer's sources: a `[n]` that does not match any of them stays as text, so a
 * made-up citation is never shown as a link.
 *
 * Works on partial text too (while streaming): an unclosed ``` or ` is text until
 * its closing marker arrives.
 */
export function parseMessageText(content: string, sourceIndexes: ReadonlySet<number>): MessageSegment[] {
  const segments: MessageSegment[] = [];
  let last = 0;
  for (const match of content.matchAll(CODE)) {
    pushText(segments, content.slice(last, match.index), sourceIndexes);
    const block = match[1] !== undefined;
    segments.push({ kind: 'code', text: block ? match[1].replace(/\n$/, '') : match[2], block });
    last = match.index + match[0].length;
  }
  pushText(segments, content.slice(last), sourceIndexes);
  return segments;
}

/** The source indexes an answer cites, in order of first appearance (code excluded). */
export function citedIndexes(content: string, sourceIndexes: ReadonlySet<number>): number[] {
  const cited = parseMessageText(content, sourceIndexes)
    .filter((s): s is { kind: 'citation'; index: number } => s.kind === 'citation')
    .map((s) => s.index);
  return [...new Set(cited)];
}

function pushText(segments: MessageSegment[], text: string, sourceIndexes: ReadonlySet<number>): void {
  let last = 0;
  for (const match of text.matchAll(CITATION)) {
    const index = Number(match[1]);
    if (!sourceIndexes.has(index)) continue; // stays inside the surrounding text
    appendText(segments, text.slice(last, match.index));
    segments.push({ kind: 'citation', index });
    last = match.index + match[0].length;
  }
  appendText(segments, text.slice(last));
}

function appendText(segments: MessageSegment[], text: string): void {
  if (!text) return;
  const previous = segments.at(-1);
  if (previous?.kind === 'text') previous.text += text;
  else segments.push({ kind: 'text', text });
}
