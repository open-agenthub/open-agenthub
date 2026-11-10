# Frontend test dependency proof

Run `bash tests/FrontendTestDependencyProof/check.sh` with Node 26 and npm.
It installs the locked dependency graph, lists the test tooling chain, runs the full
frontend suite, audits the graph and builds production assets. Keep output outside Git.

The scoped override selects glob 14.0.4 for js-beautify until that package supports
the fixed release directly. It retains test-utils 2.5.1. Downgrading test-utils to
2.4.11 would move the chain onto older tooling; retaining the vulnerable resolution
would leave the audit finding. Remove the override once an upstream dependency
range selects a fixed glob release without it.

These packages are dev dependencies. The frontend runtime image copies only the
built dist directory from the build stage, not node_modules or the test tools.
