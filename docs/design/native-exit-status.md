# Native exit status includes the signal

The real PTY can report exit code zero alongside signal 9 when its native child is killed.
Classifying only the code marks that session Succeeded. Normalize a signal termination to
a failing process code before ordinary exit reporting, keeping the signal in the termination
diagnostic. Intentional source shutdown and deliberate runtime restart branches run first.

Reject treating code zero as success regardless of the signal: it loses the distinction
between normal completion and a killed agent. Reject changing every zero exit to failure:
the real native `/exit` controls must continue to report Succeeded. The real CLI proof uses
both providers and recording status callbacks; it does not claim a real OOM event.
