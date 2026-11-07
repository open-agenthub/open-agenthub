# The first administrator: explicit designation only

## Decision

Nobody is an administrator by default. An OIDC user is an admin only when one of these says so:

1. `Ee:Admins` (Helm `ee.admins`), by `preferred_username`;
2. an admin role set for that user in *Users & seats*;
3. an OIDC group mapped to the admin role (Enterprise Edition, licence-gated).

The first one has to come from `ee.admins`, since 2 and 3 need an admin to set them. The two
single-user modes need no designation: the API-key owner and the insecure development user `dev`
are always admins, because each is the install's only identity.

While none of the three designates anyone, `GET /api/admin/access` answers
`adminDesignated: false` to every signed-in user, and the app shows them a notice naming the Helm
value and their own username. The backend logs a warning at startup when `Ee:Admins` is empty,
and `helm install`/`upgrade` prints the same instruction for an OIDC release without
`ee.admins`.

This replaces the former bootstrap rule: while no admin was designated, **every** signed-in user
was one, until someone was made an admin. The quickstart and docs never asked for a designation,
so on an OIDC install the first users — whoever could sign in at the IdP — held the admin area,
licence, roles and instance-wide settings, possibly for good.

## Alternatives considered

- **Keep bootstrap, add a louder warning.** Leaves exactly the open state that is the defect; a
  warning in an admin area that everyone can reach does not narrow who can reach it.
- **The first user to sign in becomes the admin.** Workable, and needs no Helm value, but it is a
  race: on an IdP shared with an organisation, whoever signs in first after install — or after a
  switch from the API key to OIDC, or after a database restore — wins it permanently, and the
  operator cannot tell from the outside who that was. It also re-arms whenever no admin exists.
- **A one-time claim code** (printed in the backend log or stored in a Secret, entered in the UI
  by the first admin). Safe, since only someone with cluster access can read it, but it is new
  machinery — a code store, an expiry, a claim endpoint, a UI flow — to do what a Helm value
  already does, and the person installing the chart already edits Helm values.
- **Make `ee.admins` required whenever `oidc.authority` is set** (fail the render). Most
  explicit, but it would fail the upgrade of every existing install that designated its admins in
  the admin area or through groups, which is the supported way since #103.

Explicit designation by Helm value is the smallest change that never grants admin to someone
nobody named, and the operator of a Helm release is by definition someone with that authority.

## Existing installs

- Admins from `ee.admins`, from a per-user role and from a mapped admin group keep their rights:
  the precedence in `docs/admin-roles.md` is unchanged apart from the removed bootstrap step.
- An install that was still in bootstrap — nobody designated — has no admin after the upgrade.
  That is the intended safe failure: its users see the notice, and the operator sets `ee.admins`.
- An install whose only admins come from an admin group falls to "no admin" if the Enterprise
  licence lapses, where it used to fall back to everyone. `ee.admins` is the break-glass account
  for exactly that.

The last-admin guard (`AdminAccess.KeepsAnAdminAsync`) no longer has to treat "fell back into
bootstrap" as a special case: demoting the last admin simply leaves none, and is refused.

## Switching from the API key to OIDC

Ownership is by username. The API key acts as `auth.apiKey.owner` (default `admin`), and the
sessions, credentials and projects created with it belong to that name. After the switch:

- **Admin rights**: the key owner is no longer an admin by mode; set `ee.admins` to your OIDC
  username.
- **Seat**: the key owner keeps its row in *Users & seats* with a seat, which still counts. Revoke
  it there once the data is no longer needed.
- **Sessions and data**: they stay under the key owner's name, visible only to a user with that
  `preferred_username`. If your IdP username is the same as the key owner, you keep everything.
  Otherwise finish or export what you need before switching.

Rewriting owner columns on a mode switch was rejected for the same reason as on the API-key
upgrade (`docs/no-oidc-api-key.md`): it would touch many tables during a configuration change,
a partial run would split one person's data across two names, and the backend cannot know which
OIDC user the key holder becomes.

## Tests

`AdminAccessTests`: no designation means no admin (`NoDesignation_NobodyIsAdmin`), a configured
admin is one, an admin group mapping and a per-user admin role still grant it, and the last-admin
check refuses demoting the only admin. `HubAuthTests`: the key owner and the dev user are admins
without `Ee:Admins`. `AdminRolesPostgresTests` drives the real controllers on Postgres. In the
frontend, `app-navigation.test.js` covers the notice.
