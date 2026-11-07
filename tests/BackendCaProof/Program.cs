using var client = new HttpClient();
var discovery = await client.GetStringAsync(Environment.GetEnvironmentVariable("PROOF_ISSUER") + "/.well-known/openid-configuration");
if (!discovery.Contains("jwks_uri")) throw new Exception("Discovery missing signing keys");
Console.WriteLine("PASS: real .NET HttpClient trusts the private issuer");
