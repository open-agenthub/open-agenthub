# T3 Code adoption source check

The evaluation is documentation only. This check reads the real pinned upstream checkout,
checks the eleven-entry driver registry, resolves the document's upstream and local source
references, and checks the licence inputs used in the evaluation. It intentionally does not
install upstream dependencies or run downloaded agents, because that would not establish
account entitlement or distribution permission.

Clone `https://github.com/pingdotgg/t3code` into a temporary directory and check out
`25b53299b75d4d8d1d0667ad47e9cfed3749776e`, then run from this worktree:

```sh
python3 tests/T3CodeAdoptionProof/check.py /tmp/t3code-454
git diff --check
```

The script writes its counts to stdout. Keep generated output in Files or a temporary directory;
commit only this script and README. Manual inspection of the linked implementations is still
needed: existence checks do not prove the feature assessments. No live-model, native-client,
device or complete binary/transitive-licence proof is claimed. Each proposed implementation
must supply the real-runtime checks listed in the design before its own push.
