# License gating in the UI: one gate, not one card per feature

Every enterprise endpoint answers a Community instance with `402 {"error":"An active enterprise
license is required."}`. What the browser made of that depended on which pane the request came
from: the share dialog printed the body verbatim (`402 {"error":…}` in red under the form), the
library share controls said "Enterprise license required for sharing.", the admin limits pane
"Enterprise feature. … activate one in the License tab.", the skill-publishing pane "Library
sharing is an enterprise feature". Four texts, one meaning, and none of them was a link.

## What is built

`frontend/src/components/LicenseGate.vue` is the single card for this state: a lock icon, the
title "Enterprise feature", one sentence naming what the viewer tried (`feature` prop), and either
a link "Activate a license →" to `/settings/license` for an admin, or "Ask an administrator to
activate a license." for everyone else. `lib/license.js` carries `isLicenseError(err)` and the
path constant; `api.js` stamps `err.code = 'license_required'` on every 402 so a pane never has
to know whether the controller it talked to sends a code (the sharing service does) or only the
message (the others).

The panes that gate: `ShareSessionDialog` (a 402 on load replaces the form, a 402 on a change
keeps the form under the gate so the owner still sees the existing shares), `GroupsPane`,
`LibraryShareControls` (inside the MCP catalog and skill rows), `SkillsPane` (the publish toggle),
`AdminLimitsView` (limits, group roles and the agent allowlist live in one pane and share one
gate). The "↗ Share" button stays visible on a session: the gate is the answer to pressing it,
hiding the button would only move the question to "where did sharing go".

## Why one component and not a text per pane

**The alternative was to keep each pane's own card and merely add the link to each.** It was
rejected because the drift was the bug: the texts had been written at different times and said
different things, and the share dialog had none at all — which is how the raw body reached a
user. A single component makes the next enterprise pane get the right card by importing it; a
convention ("remember to add a link") does not survive the next contributor.

## Why the admin flag is injected

The gate needs to know whether to show a link (admin) or a hint (everyone else). `isAdmin` is
known in `App.vue`; the gates sit in the share dialog inside `TerminalView`, in a library row
inside a settings pane, three to four levels down. **The alternative — a prop on every component
on the way — was rejected** because it couples panes to a concern they do not have (the MCP
catalog row does not care who is an admin) and because the first forgotten prop would silently
show the "ask an administrator" hint to the administrator. `App.vue` therefore `provide`s both
`isAdmin` and `openSettings`; a gate without a provider (component tests, a pane mounted on its
own) reads that as "not an admin" and lets the anchor navigate the old way, which is the safe
reading of "unknown".

## Why the link navigates through `openSettings`

The link is a real `<a href="/settings/license">`, so middle-click and "copy link" work, but a
plain click calls the same `openSettings('license')` that the gear icon uses. **The alternative,
letting the anchor navigate, was rejected** because that reloads the application: the session
list, the admin check and the open session are fetched again, and the back button then returns
to a fresh page rather than to the dialog the user came from. The URL still follows (`App.vue`
pushes it from the page state), so a reload on the license tab lands on the license tab.

## What is deliberately not done

- No change to the backend's 402 shape. The code is normalised in `api.js` instead, so older
  backends keep working with this frontend.
- No gate on the Slack/Telegram/Signal panes: they do not answer 402 today — when one of them
  starts to, it imports the component.
- The non-admin variant has no link to the admin: the hub does not know who that is, and a
  mailto to an unknown address would be worse than the sentence.
