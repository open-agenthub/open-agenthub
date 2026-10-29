# Browser view in a separate window

The session opens a same-origin application window and suspends its own VNC connection and
viewport updates while showing a Bring back placeholder. The separate window mounts the same
browser pane in its own document, so input, clipboard permissions and viewport measurements
belong to the focused window. Returning or closing that window reconnects the session pane.
Closing the session pane also closes its pop-out; this keeps control tied to the session view.

Session state and existing shared-view credentials travel only through messages checked against
both the expected window and origin. The URL contains no session credential. The normal sign-in
bootstrap uses the existing browser storage. The server continues to enforce control permissions.

Moving the existing canvas into another document was rejected because VNC input listeners and
clipboard gesture handling can remain bound to the original window. A URL carrying a new bearer
credential was rejected because it would persist in history and access logs. Pop-up blocking
leaves the embedded view connected and displays a retry hint.
