# Quickstart image proof

The Quickstart install workflow builds the backend, frontend and artifact renderer
from its checkout, tags them with the seven-character workflow SHA, and places a
Docker archive in k3s's startup import directory before running the published
installer. Its identity step prints deployment image references and pod image IDs.
The existing smoke and upgrade-in-place checks then exercise that checkout.

The container workflow publishes SHA tags on pushes, but its PR builds are
cache-only. Building locally was chosen over polling for PR tags that never appear
or falling back to latest, which would hide backend startup regressions. This also
avoids package-write credentials for fork PRs. Build errors fail the job; they do
not silently test another commit. Provider runtime and browser images are not
started by the quickstart smoke and are outside this proof.

For a negative control, dispatch this workflow on the branch with
`broken_backend=true`. It replaces the built backend's entrypoint with an exit of
1. The actual installer must fail its Helm readiness wait; diagnostics must show
`deliberate-startup-failure`. This dispatch is intentionally red. Dispatch again
with the default false for the healthy install and upgrade proof.

Nightly runs use main's checkout; PR runs use the tested merge SHA, matching the
container workflow's application version convention.
