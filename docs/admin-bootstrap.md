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

Ownership is by username. The API key acts as `auth.apiKey.owner` (default `admin`; `dev` on
installs upgraded from the open mode), and the sessions, credentials and projects created with it
belong to that name. After a switch to OIDC they belong to whichever identity-provider user signs
in with the same username — on a provider with self-registration, whoever registers it first.

What the hub does about it:

- **No admin rights by name.** The names an install used without OIDC are *single-user owners*:
  the API-key owner and the development user, recorded in `single_user_owners` on every start in
  those modes, the name the chart carries forward under OIDC (`Auth__FormerApiKeyOwner`, from
  `auth.apiKey.owner`, the old ConfigMap or the key Secret), and the chart defaults `admin` and
  `dev`. Under OIDC such a name is an admin only through `Ee:Admins`. A per-user admin role or an
  admin group does not count for it, and promoting it in the admin area answers 409. An admin row
  left over for such a name does not count as a designated admin either.
- **A notice while it holds data.** When a single-user owner owns sessions or projects, admins see
  "Data from API-key mode is under the name X (n sessions, m projects). It is visible to the
  identity-provider user with the same username." and the backend logs the same warning at
  startup.
- **Seat.** The key owner keeps its row in *Users & seats* with a seat; revoke it there.

What the operator does, before switching: either pick a key owner name nobody can register at the
provider (`auth.apiKey.owner`, ideally from the start), or move or delete the data. After the
switch, an admin hands the data to the OIDC user it belongs to in *Users & seats*
(`docs/apikey-handover.md`).

Rejected alternatives:

- **Rewrite owner columns on the switch** (as for the API-key upgrade, `docs/no-oidc-api-key.md`):
  it touches many tables during a configuration change, a partial run splits one person's data
  across two names, and the backend cannot know which OIDC user the key holder becomes.
- **Hide the old owner's data from every OIDC user.** Safer for the data, but it would also lock
  out the legitimate case — the key holder whose provider username is the same — with no way back
  short of SQL.
- **Only the recorded name, without the defaults `admin` and `dev`.** Installs that switched before
  the record existed would not be covered. The cost of including the defaults is that a provider
  user named `admin` or `dev` can be an admin only through `Ee:Admins`.

## Tests

`AdminAccessTests`: the API-key owner name gets no admin from a role or group under OIDC, and only
`Ee:Admins` makes it one; no designation means no admin (`NoDesignation_NobodyIsAdmin`), a configured
admin is one, an admin group mapping and a per-user admin role still grant it, and the last-admin
check refuses demoting the only admin. `HubAuthTests`: the key owner and the dev user are admins
without `Ee:Admins`. `AdminRolesPostgresTests` drives the real controllers and `SingleUserOwnerStore` on Postgres,
including the 409 for promoting the old owner name. `tests/helm/api-key-values.ps1` checks
`Auth__FormerApiKeyOwner`. `tests/AdminBootstrapProof/` runs the real backend through a switch from
the API key to OIDC. In the
frontend, `app-navigation.test.js` covers the notice.
