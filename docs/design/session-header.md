# Session header: one row and a "⋯" menu (#551)

The Workspace session bar had grown one control per feature: account picker (with the full
e-mail), Switch provider, Model, Files, Browser, the view switch, Pause, Messages, Edit session,
Continue locally, Share and the connection status as text. On a 1280 px screen it took two rows
(98 px), at 1024 three, at 768 five (207 px), and every new feature pushed the conversation lower.

## Decision

- **The bar keeps what is used all the time, at every width:** title with status, the model
  chip, the view switch (Workspace / Terminal / Shell), Pause or Resume, Share.
- **Everything else is in one "⋯" session menu:** account, Switch provider, Files, Browser,
  Messages, Edit session, Continue locally, and the outdated-image update.
- **The account shows the name the person gave the login** (else the e-mail's local part); the
  full identity sits under the picker and in each option's tooltip. Two logins that would read
  alike are spelled out in full.
- **The connection is a dot** whose words ("Agent: connected") are its tooltip and accessible
  name.
- **The closed menu carries a dot** when something inside wants attention — open messages, an
  outdated image, a provider replacement that is pending — and names it in the button's label.
  Without it a running replacement or an unread message would be invisible behind the menu.
- **One row at every width.** The bar no longer wraps; a container query on the session view
  (not the viewport: the sidebar changes the width too) drops the Pause/Share labels and the
  "Model" caption below 860 px, then tightens spacing and lets the title and model id truncate
  below 720 px. Measured in Chromium: 56 px at 1280 and 1024, 52 px at 768 (view width 520 px).
- **Phones split the bar on purpose.** Below a 512 px view (the narrowest in which an owner's
  bar fits one row) the title and "⋯" take the first line and the controls the second, instead
  of the row overflowing sideways: 90 px at 390.

Roles are unchanged: a viewer and a shared-link reader see the view switch, the read-only model
(viewer), "Read-only" and a menu with Files/Browser only.

## Rejected alternatives

- **Wrap onto a second row (the old behaviour).** The conversation's top edge moved with the
  session's features and the window width — the complaint itself.
- **A session details side panel** for the secondary controls. It would take width from the
  conversation and the companion panes and needs its own open/closed state per session; a
  dropdown costs nothing while closed.
- **An ARIA `role="menu"`.** A menu may only contain menu items, and the account picker is a
  `<select>`. The menu is a disclosure (button with `aria-expanded`/`aria-controls`); arrow keys
  move between its items, Escape closes it and returns focus to the button.
- **Rendering the menu's contents only while open (`v-if`).** The provider-switch component
  inside it reads the replacement status once on mount, which is what tells the bar a
  replacement is pending. It stays mounted behind `v-show` and still polls only while its panel
  is open or a replacement runs, as before.
- **Icon-only buttons at every width.** Labels help at desktop widths, where there is room;
  they go only where they would push the bar past one row, and the icon buttons keep their
  names as `aria-label` and tooltip.
