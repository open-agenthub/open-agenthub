# Recipient picker flake: deterministic reproduction (#518)

The picker tests in `frontend/src/components/share-session-dialog.test.js` failed now and then in
the full concurrent suite: `findShareRecipients` was called once too often, with `carol` or
`nobody`. Neither name is typed in a picker test. The license tests above them type those names
with real timers and never unmounted their dialog, so the dialog's 250 ms lookup stayed pending.
On a loaded worker that timer came due inside the next picker test, after `vi.clearAllMocks()`,
and counted as a second lookup.

`run.sh` turns "a loaded worker" into a fixed 300 ms stall at the start of each picker test's
setup (a copy of the test file; the original is untouched), so the leaked timer is due there on
every run:

```sh
tests/RecipientPickerFlakeProof/run.sh frontend 3 origin/main   # before: every run fails
tests/RecipientPickerFlakeProof/run.sh frontend 50              # after: 50/50 pass
```

The stall reads a clock bound at import. On main, a failing picker test leaves fake timers
installed, and a stall on the faked `Date.now` never ends.
