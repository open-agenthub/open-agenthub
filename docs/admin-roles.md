# Admin roles: who is an admin, and why

Until now an admin was whoever stood in `Ee:Admins` (the `ee.admins` Helm value), plus — in the
Enterprise Edition — anyone in an OIDC group mapped to the `admin` role. Promoting or demoting
one person meant a values change and a redeploy, and the admin area did not say who was an admin
or where that came from. Issue #103 asked for both. This file records how it is decided now and
which alternatives were turned down.

## Precedence

`AdminAccess.ResolveAsync` returns a role and the source that decided it. First match wins:

| # | Source | Role | Can the admin area change it? |
|---|---|---|---|
| 1 | `values` — the owner is listed in `Ee:Admins` | admin | No. Shown as a break-glass admin. |
| 2 | `user` — a role set for this user in *Users & seats* | admin or user | Yes (Community Edition). |
| 3 | `group` — an OIDC group mapped to `admin` (Enterprise, licence-gated) | admin | Yes, in *Usage limits & groups*. |
| 4 | `bootstrap` — no source designates any admin | admin | Ends as soon as one does. |
| 5 | `group` — an OIDC group mapped to `user` | user | Label only. |
| 6 | `default` | user | — |

The admin area lists every known user with this role and source (`GET /api/admin/roles`), and
`Ee:Admins` entries who have never signed in are listed too, so the break-glass account is visible
before anyone needs it.

**A per-user role wins over group mappings**, as the issue proposed. The alternative, groups over
users, would make the per-user setting useless exactly where it matters: demoting one member of
an admin group, or promoting one person without creating a group in the IdP for them.

**`Ee:Admins` wins over everything** and cannot be demoted in the UI. It is the account that still
works when the database has been edited into a corner; letting the UI override it would remove the
only way back in that does not need SQL. The API answers 409 for a values admin.

**Within groups, admin wins over user.** Someone in both an admin group and a user group is an
admin. The alternative — user wins — would turn a `user` mapping into a deny: adding a person to
one more group in the IdP would silently take admin rights away. Membership in an additional
group never removes rights here.

## Decided: "members of these groups become users" only labels

The issue left open whether a `user` mapping should also restrict sign-in to members of mapped
groups. It does not. A `user` group mapping changes nothing about who may sign in, and nothing
about what a non-admin may do; it shows up as the source of a user's role in the list.

Restricting sign-in was rejected for now because it changes the failure mode of a mistake from
"someone is labelled wrongly" to "nobody can sign in": a typo in a group name, or an IdP whose
groups claim is missing from one client's token, would lock out everyone who is not in
`Ee:Admins`. It would also have to redefine bootstrap ("empty `Ee:Admins` means everyone is an
admin"), because with a restriction in place "everyone" is no longer everyone who can reach the
IdP. Access control on sign-in belongs to the IdP's client configuration, which already has it.
If it is needed in the hub later, it should be its own explicit setting, not a side effect of a
role label.

## Decided: the per-user role is Community Edition, group mapping stays Enterprise

The per-user toggle replaces a values edit that every Community install can already make, so
gating it behind a licence would only make the Community Edition worse at something it can do
anyway. `user_roles`, `role_audit` and `AdminRolesController` live in `backend/`. Group mappings
read the token's groups claim, which is an Enterprise feature already (`ee/backend/Identity/`);
`GroupRoleAdminProvider` returns nothing without a valid licence, so a lapsed licence falls back
to per-user roles and `Ee:Admins`, never to an error.

## The last admin

An admin cannot remove the last admin, themselves included. Both `PUT /api/admin/users/{owner}/role`
and `PUT /api/ee/admin/groups/{group}/role` apply the change, then ask `AdminAccess.KeepsAnAdminAsync`
whether an admin is left among the known users (or in `Ee:Admins`), and undo the change with a
409 if not.

Checking the outcome instead of predicting it keeps the precedence rules in one place. A
"what-if" check would have to restate them — in SQL or in a second code path — and the copy is
what drifts the day the rules change.

**Falling back into bootstrap counts as removing the last admin.** Without that rule, demoting the
only explicitly designated admin would leave nobody designated, bootstrap would start again, and
every signed-in user would become an admin — the check would pass because "everyone" is an admin.
The first version of this change had exactly that hole; the Postgres test
`SetRole_RefusesToRemoveTheLastAdmin_IncludingYourself` caught it. Changes made *while* bootstrap
is active are still allowed, so an admin can label people before designating anyone; designating
the first admin ends bootstrap for everyone else, which the UI says next to the list.

Role changes take a Postgres advisory lock (`UserRoleStore.LockAsync`) around apply-check-undo.
Without it, two admins demoting each other at the same moment on two replicas would each see the
other still an admin, and both changes would pass.

## No cache

`AdminAccess` reads the role tables on every check. A change applies at the user's next request
on every replica, which is the point of moving it out of the values. The checks run on admin
endpoints and a few library calls, not on the session or WebSocket paths, so the extra queries
are not on a hot path. A per-process cache would need invalidation across replicas — the
licence already needs a poller for that (`LicenseRefresher`) — for no measurable gain.

## Audit

Every effective change is written to `role_audit`: when, who (the caller's `preferred_username`),
whether a user or a group, which one, old role, new role (`null` = cleared). A refused change and
a change to the same value write nothing. `GET /api/admin/roles/audit` returns the newest 100; the
admin area shows the latest under *Recent role changes*. Rows are never deleted by the hub, so the
trail survives the user it describes.

## API

| Method | Path | Edition | |
|---|---|---|---|
| GET | `/api/admin/roles` | CE | Users with role, source, groups, explicit role; `bootstrap`; `valuesAdmins`; `groupMapping` (licence active) |
| PUT | `/api/admin/users/{owner}/role` | CE | `{ "role": "admin" \| "user" \| null }`; 404 unknown user, 409 values admin or last admin |
| GET | `/api/admin/roles/audit` | CE | Newest first, `?limit=` up to 1000 |
| GET | `/api/ee/admin/groups` | EE | Groups seen at sign-in and mapped groups, with member counts |
| PUT | `/api/ee/admin/groups/{group}/role` | EE | Same body; a group nobody has signed in with yet is accepted (free entry) and normalised like the claim (leading `/` dropped); 409 last admin |

The group endpoints stay under `/api/ee/admin`, where they were, rather than moving to
`/api/admin`: every Enterprise admin endpoint is there and answers 402 without a licence, and the
frontend's licence gate keys on that.

Roles can only be set for users who have signed in at least once (`app_users`). Pre-provisioning
by name was left out: a typo would create a role for nobody, and the person can be promoted a
minute after their first sign-in, or put in `Ee:Admins` if they must be an admin from the start.
