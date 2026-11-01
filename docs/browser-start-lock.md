# Browser start lock scope

The per-session local gate and database advisory lock cover lease lookup, pod creation or
adoption, and startup finalization. They do not cover readiness reads or polling delays. Holding
both until readiness occupied a lock-pool connection for up to 90 seconds and prevented Stop
from interrupting startup. Increasing the pool would postpone exhaustion and increase database
connection pressure; it would not fix that ordering.

Two starts on different replicas serialize creation. The second adopts the same Pending lease
and waits independently. Both can publish the same Running generation; only one pod is created.
A readiness snapshot is advisory until finalization reacquires the lock and checks the lease.

Stop takes the same locks, marks the lease Stopping, deletes the pod and policies, then removes
the lease. A readiness waiter sees a missing, stopping, failed or different generation and returns
`Browser start was stopped or superseded by another start.` It never recreates the stopped pod.
A stale readiness response cannot mark the replacement Running or delete it: all finalization
checks the generation while locked. Cancellation cleanup checks the generation too, and skips
an already Running lease so cancelling one waiter cannot tear down another waiter's success.

A start arriving during Stop waits for that critical section to finish, then creates a fresh
lease and pod. Stop deliberately retains its lock through deletion: releasing it earlier would
allow session-named deletion to race with creation of a replacement. Slow pod creation and stop
cleanup can still occupy lock connections; the startup readiness timeout no longer does.

Rejected alternatives were keeping the local gate through readiness (Stop on that replica would
still block), and merely releasing the advisory lock without generation checks (an old timeout
or cancelled waiter could delete a new pod by its stable session name). A readiness result is
valid at the finalization lock boundary; a subsequent Stop can still stop that browser normally.

The regression proof uses the real lease store and advisory lock against a running database,
with a delayed cluster API, multiple service instances, 24 sessions and a lock pool of two.
It also holds an old readiness response across Stop and replacement creation.
