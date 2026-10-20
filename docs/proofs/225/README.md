# Dependency security refresh (#225)

The SDK parent is updated to 1.32.1 and its transitive dependencies refreshed within
supported ranges. Mermaid 12.2.0 permits patched KaTeX; the 11.x parent remains on
KaTeX 0.16. We prefer the released parent over a forced incompatible override.
The XML signature package retains a direct patched reference because the
parent's transitive floor still allows vulnerable versions. No npm overrides.

Before runtime replacement, the same update passed session-agent (732), MCP (51),
frontend (846) and the frontend production build, with all three npm audits clear.
Those outputs were lost before the branch was saved. Repeat verification, real
Chromium flowchart/sequence rendering, bundle comparison and backend/Postgres
verification are pending on this saved draft.
