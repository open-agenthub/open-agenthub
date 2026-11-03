# Account-email privacy contract

Image CI runs this proof with `--network none`, synthetic credentials and local stand-ins,
in roughly 40 seconds. Failures say `privacy predicate drift`. Run this proof for **every pinned CLI bump**, alongside the runtime suite and fleet mod tests.
It intentionally fails if the native account-email prompt disappears, changes shape, our
matching predicate drifts, the unattended turn stalls, or an email reaches either callback.

Prerequisites: Node, openssl, the runtime test dependencies, and the candidate CLI installed
at `$RIG_HOME/clis/node_modules/.bin/claude`. Explicitly run its `install.cjs` if npm suppressed
postinstall. Use a fresh absolute scratch directory outside the repository; paths must not
contain whitespace. Keep generated output out of Git.

```sh
RIG_HOME=/absolute/scratch bash tests/AccountEmailPrivacyProof/run.sh
```

The real candidate runs in a PTY with a synthetic OAuth identity, a loopback TLS proxy,
synthetic model responses, and the production PreToolUse and privacy PermissionRequest hooks.
The callback is a stand-in for the controller's live approval-mode decision; controller tests
exercise the real authenticated controller for interactive, autoApprove and autonomous modes.
The model fixture counts whether it sees the explicit refusal and whether the email receiver
was called, without storing model request bodies. Only synthetic identities belong here.

`check.mjs` requires the native privacy reason and complete Yes/No menu in interactive mode,
a refusal followed by a final answer with autoApprove, and an autonomous process that exits
successfully with the refusal in its native transcript. Callback evidence records only boolean assertions: no address leaked, the command
remained, the replacement marker was present, and the dedicated privacy callback ran. The email receiver must never be called. A native prompt missing
from a future version is a failure to investigate, rather than a reason to silently loosen
our local predicate.

This proves the real CLI and production hooks, not a deployed cluster, real account,
relay delivery or browser rendering. The long-question and fleet proofs cover their own
separate contracts.
