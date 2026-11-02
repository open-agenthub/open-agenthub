# Deleting a session file

The Files pane lists a session's files; #74 adds a way to remove one. This records the choices
behind it.

## Two requests, not one

| Request | Takes | Who |
|---|---|---|
| `DELETE /api/sessions/{id}/files/{fileId}` | a completed (`Ready`) file | anyone who may write files: the session owner and collaborators |
| `DELETE /api/sessions/{id}/files/{fileId}/upload` | a reservation that never completed (`Reserved`, `Uploading`, `Failed`) | the user who reserved it, or the session owner |

Before, one `DELETE` took a file in any state. The upload queue used it to give up a
reservation when the user removed a row mid-upload, and it was the only way to delete anything
else. **The alternative was to keep the one request and widen its permission.** Rejected, because
the two actions need different rules. Giving up a reservation is housekeeping for the uploader's
own upload. Deleting a file removes something the whole session uses. A collaborator who could
delete through the "cancel" path could also pull a reservation out from under an upload that the
agent or another collaborator still has in flight. And a stale cancel from a composer (an id
whose upload has since completed) would silently destroy a file the session now owns.

Each request now refuses the other's state with a 409 that names it: `file_upload_pending` when
a delete hits a reservation, `file_upload_complete` when a cancel hits a completed file. The
upload queue cancels a row that has not completed and deletes one that has. One race is left:
the user removes a row while its `complete` call is in flight, and `complete` wins. The cancel
then answers `file_upload_complete`, and the queue deletes the file, because removing it is what
the user asked for.

## Who may delete

The session owner and collaborators (`SessionAccessRules.CanWriteFiles`). **The alternative was
owner-only, which the old route had.** A collaborator can already upload, present and dismiss
files, so owner-only let them fill the session's file quota but not clear out a wrong upload.
Viewers get 403. Share links have no delete route at all, for either role; the shared file
controller is read-only, and a test pins its action list. Users without access get 404, as
everywhere else.

Without an Enterprise licence, shares do not resolve (`SessionAccessService`), so only the
session owner can delete. The route and the service are open-core. Only the collaborator role
depends on the Enterprise sharing.

## What a delete removes, and what it does not

Removed:

- the registry row, moved to `Deleted` first. That transition is the commit point: once it lands,
  nothing lists or serves the file. A storage error afterwards is logged and leaves bytes behind,
  never a listed file whose content is gone.
- the bytes: the object in object storage (`DeleteObject`), or, for pod storage, the copy the
  session agent keeps for the backend, if the pod is running.
- an active presentation of the file, which is dismissed for everyone.
- the document's converted Office preview. The preview is listed like any file but belongs to its
  document, whose preview pane fetches it by id, so it goes with the document. Deleting the
  preview on its own is refused (`file_is_preview`) while its document exists, and the pane does
  not offer it. If the document is deleted while its preview is still converting, the worker
  deletes the finished preview.

Not removed:

- copies outside the registry: a file the agent wrote and then uploaded stays in its workspace,
  and sibling sessions keep copies they already fetched with `fetch_project_file`. They stop
  seeing the file in `list_project_files`. The confirmation says so for a session in a project.
- earlier object versions or backups. On a bucket with versioning, `DeleteObject` without a
  version id adds a delete marker and keeps the old versions; the operator's lifecycle rules
  decide when those go.

## The confirmation lives in the row

**The alternative was `confirm()`.** Rejected. A native dialog blocks the whole tab, the
session's live updates included. A host that embeds the page can also refuse it, and then
`confirm()` returns false and the button silently does nothing. The row shows what will happen
(presentation dismissed, preview taken along, project sessions losing sight of it) and asks for
a second click. Escape or Keep closes it.
