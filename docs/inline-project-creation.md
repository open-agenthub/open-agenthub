# Inline project creation

The new-session dialog creates a project through the same endpoint as Projects, with a name
and the normal defaults (no color and no selected project secrets). This keeps name validation,
case-insensitive duplicate detection, and ownership identical in both entry points.

Project creation and workload creation cannot share a database transaction: starting a session
also creates cluster resources. Instead, the dialog reports that the project was saved even
when the session request fails, keeps it selected, and retries with that same project id.
Deleting it automatically was rejected because a failed HTTP response does not establish that
no session was created, and another tab may already be using the project. Creating a duplicate
on retry would leave confusing empty projects, so the successful project id replaces the
“New project…” selection before requesting the session.
