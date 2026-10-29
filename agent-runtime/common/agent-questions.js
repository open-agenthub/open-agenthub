'use strict';
// The questions an agent's question tool asks, as the tool's own input carries them (#343):
// Claude Code's AskUserQuestion and Codex's request_user_input ({questions:[{header, question,
// multiSelect?, options:[{label, description}]}]}) and OpenCode's question tool (the same shape,
// `multiple` for multi-select). The Workspace read the question off the terminal screen before,
// and a question taller than the screen lost its first lines there, and every option its
// description.
//
// Used by the hooks that report a question and by the session agent that relays it, which
// cannot trust what a hook sent: bounded so a frame sent twice a second stays small.

const MAX_QUESTIONS = 8;
const MAX_OPTIONS = 16;
const MAX_QUESTION = 4000;
const MAX_HEADER = 200;
const MAX_LABEL = 500;
const MAX_DESCRIPTION = 1000;

function text(value, max) {
  if (typeof value !== 'string') return '';
  // A cut through a surrogate pair would leave half a character for JSON.stringify to escape.
  return value.length > max ? value.slice(0, max).replace(/[\uD800-\uDBFF]$/, '') : value;
}

/** The questions in a question tool's input, or [] when it carries none. */
function normalizeQuestions(input) {
  const list = Array.isArray(input) ? input : Array.isArray(input?.questions) ? input.questions : [];
  return list.flatMap(entry => {
    if (!entry || typeof entry !== 'object') return [];
    const question = text(entry.question, MAX_QUESTION);
    if (!question.trim()) return [];
    const options = (Array.isArray(entry.options) ? entry.options : []).slice(0, MAX_OPTIONS).flatMap(option => {
      const label = text(option?.label, MAX_LABEL);
      return label.trim() ? [{ label, description: text(option?.description, MAX_DESCRIPTION) }] : [];
    });
    return [{ header: text(entry.header, MAX_HEADER), question, multiSelect: entry.multiSelect === true || entry.multiple === true, options }];
  }).slice(0, MAX_QUESTIONS);
}

/** The same questions with `mask` applied to every text a viewer will read. */
function maskQuestions(questions, mask) {
  return questions.map(entry => ({
    ...entry,
    header: mask(entry.header),
    question: mask(entry.question),
    options: entry.options.map(option => ({ label: mask(option.label), description: mask(option.description) }))
  }));
}

module.exports = { normalizeQuestions, maskQuestions };
