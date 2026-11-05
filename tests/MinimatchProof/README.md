# Frontend minimatch parent compatibility

After `npm ci` in frontend, run `node tests/MinimatchProof/parents.cjs` from the
repository root. It exercises the installed editorconfig and glob consumers through
js-beautify's dependency resolution, plus formatting with the parsed settings.
Temporary fixtures are removed automatically; no generated results belong in Git.

For issue #445, the current test-utils and js-beautify releases still require
editorconfig 3.0.2 and glob 13.0.6, which constrain minimatch to version 10.
Updating glob alone was rejected because editorconfig would retain that resolution.
The frontend override selects patched minimatch 11 for both consumers. Remove the
override when released parent ranges accept patched minimatch without it.

Run the full frontend suite and production build as well. Preserve a baseline dist
outside the checkout before changing dependencies, compare every emitted file with
`diff -qr`, and use `tests/DependencyAlertProof/bundle-sizes.cjs` to report entry and
all-JavaScript raw/gzip deltas. Identical output proves this tooling-only update does
not change the deployed bundle.
