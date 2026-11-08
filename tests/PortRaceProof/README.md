# PortRaceProof (#545)

Reproduces the probe-then-bind race behind `HttpListenerException: Address already in use` in
the parallel backend suite, and shows `tests/TestSupport/LoopbackHttpListener.cs` removes it.

Half the threads start and stop an `HttpListener` in a loop; the other half hold ephemeral
ports briefly, as parallel tests with an open `TcpListener(0)` do.

```bash
dotnet build -c Release tests/PortRaceProof
dotnet tests/PortRaceProof/bin/Release/net10.0/PortRaceProof.dll old 32 20   # probe, release, bind
dotnet tests/PortRaceProof/bin/Release/net10.0/PortRaceProof.dll new 32 20   # LoopbackHttpListener
```

`old` reports failures with "Address already in use" and exits 1; `new` must report
`failures=0`. Numbers from a run are in the PR, not here.
