# Contributing to Open AgentHub

Thank you for considering a contribution! Bug reports, feature requests,
and pull requests are all welcome.

## Building the project

- **Backend** (ASP.NET Core): `cd backend && dotnet build`
  (local dev: `dotnet run`, uses your `~/.kube/config`)
- **Frontend** (Vue 3 + Vite): `cd frontend && npm install && npm run build`
  (local dev: `npm run dev`, proxies `/api` and `/ws` to `localhost:8080`)

See the [README](./README.md) for full build & deployment instructions
(container images, Helm chart, plain manifests).

## Pull requests

1. Fork the repository and create a feature branch.
2. Keep changes focused; describe the motivation in the PR description.
3. Make sure the backend builds (`dotnet build`) and the frontend builds
   (`npm run build`) before submitting.

## Contributor License Agreement (CLA)

Open AgentHub uses an open-core model: the core is licensed under
AGPL-3.0, while code under `ee/` is offered under a commercial license.
To make this possible, **all contributions require agreeing to our
[Contributor License Agreement](.github/CLA.md)**.

By signing the CLA you grant the Open AgentHub maintainers the right to
license your contribution under **both** the AGPL-3.0 **and** commercial
licenses. You keep the copyright to your contribution.

We plan to automate CLA signing via [cla-assistant.io](https://cla-assistant.io)
— you will be asked to sign once, on your first pull request.
