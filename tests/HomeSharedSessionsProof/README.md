# Home shared sessions (#428)

The host uses production session/project sharing controllers, access services and Postgres
stores. A cookie selects alice/bob instead of an identity provider. A Kubernetes stand-in
lists no pods and the browser service reports stopped; no terminal or real pod is proved.
This is adapted from ProjectSharingProof so session access decisions remain production code.

Run a temporary Postgres on port 54329 and create database `proof428`. Build this directory
with the .NET 10 SDK and run its Host.dll (port 8080). Serve frontend with Vite on port 5173.
Run `browser.cjs` with `PW_MODULE` pointing to playwright-core and `CHROME` to Chromium;
`OUT` selects an untracked screenshot directory. Each run needs a fresh database.

The script opens two browser contexts, grants project Viewer and direct Collaborator access
through real HTTP APIs, and checks the home section beyond eight owned sessions. It verifies
inherited visibility, role/owner/status, the Viewer waiting exclusion, navigation and live
revocation. `EXPECT=main` against the unchanged frontend reproduces the missing section.
Generated screenshots and console output must stay outside Git.
