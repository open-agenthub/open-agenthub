// agenthub-fleet: delivers Open AgentHub fleet messages into this Claude Code session
// (docs/priority-messages.md, docs/claude-code-mods.md) and reports the account's usage
// limits to the hub (docs/account-limits.md). The session agent beside the CLI queues pushed
// messages at a loopback endpoint; this mod polls it, puts a priority message into the running
// turn (or submits it as a prompt, aborting the running turn when asked), and submits plain ones
// once no turn runs. It also
// watches the rate-limit windows Claude Code reports and tells the session agent when one is
// used up; the hub, not the mod, decides what to do about it.
import { formatFleetMessage, inboxStatus, inboxText, limitKey, parseInbox, parseThreshold, planDelivery, planLimitReports, unreadAppends } from './lib.mjs'

const POLL_MS = 3000
const INBOX_URL = 'http://127.0.0.1:7681/agenthub/mod/inbox'
const HEARTBEAT_URL = 'http://127.0.0.1:7681/agenthub/mod/heartbeat'
const LIMIT_URL = 'http://127.0.0.1:7681/agenthub/mod/limit'
const ACK_URL = 'http://127.0.0.1:7681/agenthub/mod/ack'

// Plain messages that arrived during a turn and are not yet shown to Claude; the first idle
// poll releases them as a prompt, unless a prompt.submit hook or /inbox drained them first.
let waiting = []
// The id the main loop's turn.start handed us, while that turn runs; what $.turn.abort needs.
let runningTurn = null
// Priority messages appended into the running turn, with the text their row carries; checked
// at its end for one the model never read.
let appended = []
let modToken = ''
// Percent of a window at which it is reported, and the windows already reported this session.
let limitThreshold = 100
const reportedLimits = new Set()

export function register(on) {
  on('session.start', async ($, e, next) => {
    modToken = (await $.env.get('AGENTHUB_MOD_TOKEN')) || ''
    limitThreshold = parseThreshold(await $.env.get('AGENTHUB_LIMIT_THRESHOLD'))
    // Without a token there is no session agent to poll; stay inert rather than hammer
    // localhost in a developer's own Claude session that happens to load this directory.
    if (modToken) {
      // The heartbeat tells the session agent to route to the mod from now on; the first
      // poll is three seconds away, and a message in between would otherwise be typed into
      // the PTY — where a -p run never reads it.
      void heartbeat($)
      $.clock.every(POLL_MS, () => { void poll($) })
    }
    // Registered last: a taken name throws, and the poll above must already be running.
    try {
      await $.command.register({
        name: 'inbox',
        description: 'Show the fleet messages waiting for this session',
        immediate: true
      })
    } catch {}
    return next(e)
  })

  // The main loop's turns only. A subagent's start and end fire here too, and taking them for
  // the main turn's cleared it while the main turn still ran: a message then counted as idle.
  on('turn.start', async ($, e, next) => {
    if (e.agentId === undefined) runningTurn = e.turnId
    return next(e)
  })

  on('turn.complete', async ($, e, next) => {
    if (e.agentId !== undefined) return next(e)
    if (runningTurn === e.turnId) runningTurn = null
    if (appended.length) {
      const items = appended
      appended = []
      await submitUnread($, items)
    }
    // A turn that died on an API error is the one a person would have noticed. The windows
    // may not have moved a whole point since the last measurement, so they are read afresh
    // rather than waited for; the dedupe keeps a window already reported from going twice.
    if (e.reason === 'error' && modToken) {
      try {
        const usage = await $.session.usage()
        await reportLimits($, usage.rateLimits)
      } catch (error) {
        $.ui.log('usage read after a failed turn failed: ' + describe(error), { to: 'debug' })
      }
    }
    return next(e)
  })

  // Claude Code pushes the rate-limit windows after every turn and whenever one moves a whole
  // point. The mod only relays; the threshold is the one knob it has.
  on('session.measure', async ($, e, next) => {
    if (modToken) await reportLimits($, e.rateLimits)
    return next(e)
  })

  // Waiting messages ride along with the next prompt the person sends, as context Claude
  // reads after the prompt. Attached once: the list is cleared here.
  on('prompt.submit', async ($, e, next) => {
    if (!waiting.length) return next(e)
    const extra = inboxText(waiting)
    waiting = []
    $.ui.status(undefined)
    return next({ ...e, context: [...(e.context ?? []), extra] })
  })

  on('command.run', { command: 'inbox' }, async ($) => {
    const text = inboxText(waiting)
    waiting = []
    $.ui.status(undefined)
    return { text }
  })
}

async function heartbeat($) {
  try {
    await $.http.fetch(HEARTBEAT_URL, { method: 'POST', headers: { Authorization: 'Bearer ' + modToken } })
  } catch (error) {
    $.ui.log('fleet heartbeat failed: ' + describe(error), { to: 'debug' })
  }
}

// Never throws: a session agent that is restarting must not take a hook down with it.
async function reportLimits($, rateLimits) {
  for (const report of planLimitReports(rateLimits, limitThreshold, reportedLimits)) {
    try {
      const response = await $.http.fetch(LIMIT_URL, {
        method: 'POST',
        headers: { Authorization: 'Bearer ' + modToken, 'Content-Type': 'application/json' },
        body: JSON.stringify(report)
      })
      if (!response.ok) {
        $.ui.log('usage limit report answered HTTP ' + response.status, { to: 'debug' })
        continue
      }
      reportedLimits.add(limitKey(report))
      $.ui.log('reported the ' + report.kind + ' window at ' + report.percentUsed + '% to the hub', { to: 'debug' })
    } catch (error) {
      $.ui.log('usage limit report failed: ' + describe(error), { to: 'debug' })
    }
  }
}

// Runs outside any event, from the timer. Never throws: a failed poll is a debug line and the
// next period tries again, so a session agent that is restarting does not kill the timer.
async function poll($) {
  try {
    const response = await $.http.fetch(INBOX_URL, { headers: { Authorization: 'Bearer ' + modToken } })
    if (!response.ok) {
      $.ui.log('fleet inbox answered HTTP ' + response.status, { to: 'debug' })
      return
    }
    const plan = planDelivery(parseInbox(response.text), runningTurn !== null, waiting)
    // Taken over before the first await: a prompt the person submits meanwhile attaches and
    // clears the list, and must not find messages this poll is about to release as well.
    waiting = plan.waiting
    if (plan.abort) {
      // The turn id is checked by the engine; a turn that ended between the check and the
      // call rejects, and the prompt below is then simply the next one.
      try { await $.turn.abort({ turnId: runningTurn }) } catch (error) {
        $.ui.log('fleet interrupt failed: ' + describe(error), { to: 'debug' })
      }
    }
    const outcomes = []
    for (const message of plan.submit) {
      // A plugin's prompt runs once the session is idle, and a busy session's turn can be its
      // whole task: submitted during it, a priority message reached the model hours later,
      // while agent_inbox had nothing because the hub had marked it delivered (#390). Appended
      // to the conversation it rides with the next model request, after the running tool call.
      if (runningTurn !== null && !plan.abort) {
        outcomes.push({ message, outcome: (await append($, message)) ? 'appended' : 'failed' })
        continue
      }
      // Not awaited: the call resolves when the turn starts, which is after the running one
      // ends, and a poll that waited for it would hold the timer and every later message.
      submit($, formatFleetMessage(message), [message])
      outcomes.push({ message, outcome: 'submitted' })
    }
    if (outcomes.length) await acknowledge($, outcomes)
    if (plan.release.length) release($, plan.release)
    else if (waiting.length) $.ui.status(inboxStatus(waiting.length))
  } catch (error) {
    $.ui.log('fleet poll failed: ' + describe(error), { to: 'debug' })
  }
}

// A user-role row of the main conversation, as the engine keeps a reminder: the model reads it
// with the next request of the running turn, and the person does not see it as typed.
async function append($, message) {
  const text = formatFleetMessage(message)
  try {
    const result = await $.session.append({ message: { type: 'user', content: [{ type: 'text', text }] } })
    if (result && typeof result.deny === 'string') {
      $.ui.log('fleet append refused: ' + result.deny, { to: 'debug' })
      return false
    }
    appended.push({ message, text })
    return true
  } catch (error) {
    $.ui.log('fleet append failed: ' + describe(error), { to: 'debug' })
    return false
  }
}

// Tells the session agent what became of each priority message, so the hub records the
// channel that took it. A message the session agent no longer waits for — its answer to the
// hub already went out — is the mod's alone: one that could not be appended is then submitted
// as a prompt for when the turn ends, late rather than lost.
async function acknowledge($, outcomes) {
  let accepted = []
  try {
    const response = await $.http.fetch(ACK_URL, {
      method: 'POST',
      headers: { Authorization: 'Bearer ' + modToken, 'Content-Type': 'application/json' },
      body: JSON.stringify({ results: outcomes.map(({ message, outcome }) => ({ id: message.id, outcome })) })
    })
    if (response.ok) {
      const parsed = JSON.parse(response.text)
      if (parsed && Array.isArray(parsed.accepted)) accepted = parsed.accepted
    } else {
      $.ui.log('fleet ack answered HTTP ' + response.status, { to: 'debug' })
    }
  } catch (error) {
    $.ui.log('fleet ack failed: ' + describe(error), { to: 'debug' })
  }
  for (const { message, outcome } of outcomes) {
    if (outcome === 'failed' && !accepted.includes(message.id)) submit($, formatFleetMessage(message), [message])
  }
}

// At the end of a turn: a message appended while its last answer streamed is in the
// conversation, but no request read it, and none will until another turn starts.
async function submitUnread($, items) {
  let unread = items
  try {
    unread = unreadAppends(await $.session.messages({ as: 'api' }), items)
  } catch (error) {
    $.ui.log('fleet transcript read failed: ' + describe(error), { to: 'debug' })
  }
  if (unread.length) {
    const messages = unread.map(item => item.message)
    submit($, inboxText(messages), messages)
  }
}

// The session is idle and the session agent has already told the hub these were delivered, so
// they become a turn now rather than wait for a prompt nobody may type (#98).
function release($, messages) {
  $.ui.status(undefined)
  submit($, inboxText(messages), messages)
}

// The session agent has already told the hub these messages were delivered, so a submit that
// does not enter must not lose them: they go back on the waiting list, stay on the status line,
// ride along with the next prompt and are released again on the next idle poll (#340).
// A refusal resolves to { drop } and never rejects — a settings UserPromptSubmit hook that
// blocks, or another mod's prompt.submit hook — so a .catch alone dropped such messages
// silently. A rejection is the engine failing outright, and is handled the same way.
function submit($, text, messages) {
  const keep = reason => {
    $.ui.log('fleet prompt failed: ' + reason, { to: 'debug' })
    waiting = [...messages, ...waiting]
    $.ui.status(inboxStatus(waiting.length))
  }
  $.prompt.submit({ text }).then(
    result => { if (result && typeof result.drop === 'string') keep('dropped: ' + result.drop) },
    error => keep(describe(error))
  )
}

function describe(error) {
  return error && error.message ? error.message : String(error)
}
