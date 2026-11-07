# Installer guidance source

The quickstart installers live in the website repository, alongside the getting-started
page. Their final output links directly to the team-login migration section, which owns
the hostname, TLS and OIDC instructions. Duplicating those steps in installer output was
rejected because partial commands drift from the guide. Both shell and PowerShell
installers use the same destination. The getting-started page lists all five agents and
points to current provider sign-in instructions instead of an obsolete release workaround.

Implementation and generated site changes are reviewed in a separate website PR for
issue #510; this decision stays with the product repository.
