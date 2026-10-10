// agenthub-fleet: delivers Open AgentHub fleet messages into this Claude Code session
// (docs/priority-messages.md, docs/claude-code-mods.md). The session agent beside the CLI
// queues pushed messages at a loopback endpoint; this mod polls it, submits priority messages
// as prompts (aborting the running turn when asked), and holds plain ones behind /inbox.
import { formatFleetMessage, inboxStatus, inboxText, parseInbox, planDelivery } from './lib.mjs'

const POLL_MS = 3000
const INBOX_URL = 'http://127.0.0.1:7681/agenthub/mod/inbox'
const HEARTBEAT_URL = 'http://127.0.0.1:7681/agenthub/mod/heartbeat'

// Plain messages not yet shown to Claude; a prompt.submit hook or /inbox drains them.
let waiting = []
// The id turn.start handed us, while that turn runs; what $.turn.abort needs.
let runningTurn = null
let modToken = ''

export function register(on) {
  on('session.start', async ($, e, next) => {
    modToken = (await $.env.get('AGENTHUB_MOD_TOKEN')) || ''
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

  on('turn.start', async ($, e, next) => {
    runningTurn = e.turnId
    return next(e)
  })

  on('turn.complete', async ($, e, next) => {
    if (runningTurn === e.turnId) runningTurn = null
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

// Runs outside any event, from the timer. Never throws: a failed poll is a debug line and the
// next period tries again, so a session agent that is restarting does not kill the timer.
async function poll($) {
  try {
    const response = await $.http.fetch(INBOX_URL, { headers: { Authorization: 'Bearer ' + modToken } })
    if (!response.ok) {
      $.ui.log('fleet inbox answered HTTP ' + response.status, { to: 'debug' })
      return
    }
    const plan = planDelivery(parseInbox(response.text), runningTurn !== null)
    if (plan.abort) {
      // The turn id is checked by the engine; a turn that ended between the check and the
      // call rejects, and the prompt below is then simply the next one.
      try { await $.turn.abort({ turnId: runningTurn }) } catch (error) {
        $.ui.log('fleet interrupt failed: ' + describe(error), { to: 'debug' })
      }
    }
    for (const message of plan.submit) {
      // Not awaited: the call resolves when the turn starts, which is after the running one
      // ends, and a poll that waited for it would hold the timer and every later message.
      $.prompt.submit({ text: formatFleetMessage(message) }).catch(error => {
        $.ui.log('fleet prompt failed: ' + describe(error), { to: 'debug' })
      })
    }
    if (plan.waiting.length) {
      waiting = [...waiting, ...plan.waiting]
      $.ui.status(inboxStatus(waiting.length))
    }
  } catch (error) {
    $.ui.log('fleet poll failed: ' + describe(error), { to: 'debug' })
  }
}

function describe(error) {
  return error && error.message ? error.message : String(error)
}
