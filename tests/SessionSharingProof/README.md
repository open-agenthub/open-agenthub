# Session sharing HTTP and Chromium proof (#328)

The host uses the production sharing controllers, access service, and real Postgres stores.
The proof authentication handler supplies synthetic signed-in identities; it does not contact
an identity provider. Postgres listens on 127.0.0.1:54329 with database proof328.

Run `dotnet run --project tests/SessionSharingProof/Host.csproj`, then Vite on port 5173.
Set `CDP` to the isolated Chromium endpoint, `UI` to the Vite origin reachable by Chromium,
and `PW_MODULE` to the installed playwright-core module, then run `node baseline.cjs`.

Current-main baseline at f168837: the generated bearer link opens successfully in Chromium
(document HTTP 200, token API HTTP 200). Its `/shared/<token>` path matches the frontend,
SPA fallback, and ingress. This does not establish the owner's deployed configuration.
