# Default image tag: the chart's appVersion

## Decision

`image.tag` is empty by default, and empty means the chart's `appVersion`. Chart 1.19.0 runs the
backend, frontend, artifact renderer, browser and all five agent runtimes of release 1.19.0, and
upgrading to chart 1.20.0 moves every one of them. A tag set explicitly still wins, `latest`
included; `browser.image.tag`, `files.officePreview.image.tag` and the `agent.images.*` overrides
keep overriding the shared tag for their component.

The tag is resolved once, in `agenthub.imageTag` in `_helpers.tpl`, and every component reads it
from there. Before, the backend and frontend used `image.tag` directly, the agent runtimes
required it, and only the browser fell back to `appVersion`: three rules for one value.

## The failure this prevents

The old default was `latest` with `pullPolicy: IfNotPresent`. A quickstart install of chart
1.18.0 therefore ran whatever `main` had last published — the UI footer showed a `main` commit,
not 1.18.0. Re-running the installer or upgrading the chart rendered the same `:latest`
reference, so Kubernetes saw no change and nothing rolled; and two nodes that pulled `latest` on
different days ran different builds under the same name. It also defeated the runtime image
update check (`docs/session-runtime-image-updates.md`), which compares image references and
cannot see a moving tag change underneath one.

## Alternatives considered

- **Keep `latest`, default `pullPolicy` to `Always`.** Nodes would converge on the newest build,
  but a chart version would still not say what it installs, a pod restart would silently update
  a running instance, and every install would track unreleased `main`.
- **Write the version into `values.yaml` (`tag: "1.19.0"`) at release time.** It has to be kept
  in step with `Chart.yaml` by hand or by the release script, and `helm upgrade --reuse-values`
  carries the old literal forward as if the user had set it. The empty default is never stored
  as a user value, so `--reuse-values` and `--reset-then-reuse-values` both pick up the new
  chart's `appVersion`.
- **Digest pinning.** Exact, but the digests only exist after the image build, which runs after
  the chart is versioned; it would need a second commit per release.

## Consequences

- An install that relied on the old default now runs the release that matches its chart. Someone
  who wants to follow `main` sets `image.tag` to a commit tag (or `latest` together with
  `pullPolicy: Always` and `agent.pullPolicy: Always`) on purpose.
- Images for `appVersion` must exist. Tag pushes publish `X.Y.Z` for every component
  (`build-images.yml`), and `appVersion` moves only in a release commit.
- The quickstart workflow (`install-smoke.yml`) installs through the default: it sets the local
  chart's `appVersion` to the checkout's commit tag instead of setting `image.tag`, checks every
  project image the release runs or hands to the backend, then upgrades to a chart with a new
  `appVersion` and checks that all of them moved (`tests/InstallSmokeProof/`).
