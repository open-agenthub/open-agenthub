import { isInteractive, report } from '../common/fleet-turn-hook.mjs';
import agentQuestions from '../common/agent-questions.js';

/**
 * OpenCode's question tool for the Workspace (#343): the counterpart of the Claude and Codex hooks
 * that report a question tool opening and closing (common/fleet-turn-hook.mjs). OpenCode 1.18.35
 * publishes `question.asked` with the questions as the tool gave them when its dialog opens, and
 * `question.replied` or `question.rejected` when it is answered or dismissed; `tool.execute.after`
 * would miss the dismissal. Without these the Workspace read the question off the screen only.
 */
export function createQuestionRelay({ env = process.env, reportImpl = report } = {}) {
  return {
    async onEvent(event) {
      if (!event || !isInteractive(env)) return;
      if (event.type === 'question.asked') {
        const questions = agentQuestions.normalizeQuestions(event.properties && event.properties.questions);
        await reportImpl('question', env, undefined, questions.length ? { questions } : {});
      } else if (event.type === 'question.replied' || event.type === 'question.rejected') {
        await reportImpl('answered', env);
      }
    }
  };
}
