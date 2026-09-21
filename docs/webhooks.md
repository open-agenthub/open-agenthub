# Git webhook triggers

A webhook trigger starts an autonomous session whenever GitLab or GitHub delivers a
merge request / pull request event — for example an automatic review session for every
new MR.

## Creating a trigger

1. Open **Settings → Webhooks** and create a trigger:
   - **Git account** (optional): the connected GitHub/GitLab account whose OAuth token
     is used to clone the repository. Leave empty for public repositories.
   - **Events**: which MR/PR actions start a session (default: `opened`, `reopened`;
     also available: `updated`, `closed`, `merged`). GitLab action names are normalized
     to these values, so one trigger works for both providers.
   - **Repository filter** (optional): a case-insensitive substring of the repository's
     full name (`group/repo`). Deliveries from other repositories are ignored.
   - **Prompt template**: the session prompt. Placeholders are replaced from the event:
     `{{title}}`, `{{description}}`, `{{source_branch}}`, `{{target_branch}}`,
     `{{url}}`, `{{repo}}`, `{{id}}`, `{{action}}`.
   - **Agent** and **auto-approve**: the session runs autonomously under your account;
     enable auto-approve only if you are fine with the agent acting unattended.
2. After creating, the **webhook URL** and the **secret** are shown exactly once — copy
   both. The secret is stored encrypted and cannot be retrieved again (delete the
   trigger and create a new one if you lose it).

The session checks out the MR/PR **source branch** and runs the rendered prompt with
the settings above. Repeated deliveries of the same MR/PR and action (provider retries,
double-configured hooks) are deduplicated for a few minutes.

## Registering the webhook in GitLab

In the repository: **Settings → Webhooks → Add new webhook**

- **URL**: the webhook URL from the trigger (e.g. `https://agenthub.example.test/api/git/webhooks/<id>`)
- **Secret token**: the secret from the trigger (sent as `X-Gitlab-Token`)
- **Trigger**: check **Merge request events** only

## Registering the webhook in GitHub

In the repository: **Settings → Webhooks → Add webhook**

- **Payload URL**: the webhook URL from the trigger
- **Content type**: `application/json` (required — form-encoded payloads are rejected
  because the signature is verified over the raw JSON body)
- **Secret**: the secret from the trigger (GitHub signs each delivery with it,
  `X-Hub-Signature-256`)
- **Events**: "Let me select individual events" → **Pull requests** only

## Notes

- The delivery endpoint (`POST /api/git/webhooks/<id>`) is public by design; every
  delivery is authenticated with the trigger's secret (GitLab: constant-time token
  comparison, GitHub: HMAC-SHA256 over the raw body).
- Unsubscribed actions, filtered repositories, and non-MR/PR events (pushes, pings, …)
  are acknowledged with `200` and ignored, so the hook can stay coarse on the provider
  side.
- If a session cannot be started (e.g. session limit reached), the delivery is also
  acknowledged with `200` and an `error` status, to keep the provider from retrying
  into the same limit. Check the trigger's "last triggered" timestamp and the backend
  logs when sessions do not appear.
