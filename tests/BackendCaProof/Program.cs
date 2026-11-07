using var client = new HttpClient();
var discovery = await client.GetStringAsync(Environment.GetEnvironmentVariable("PROOF_ISSUER") + "/.well-known/openid-configuration");
if (!discovery.Contains("jwks_uri")) throw new Exception("Discovery missing signing keys");
Console.WriteLine("PASS: real .NET HttpClient trusts the private issuer");

var publicUrl = Environment.GetEnvironmentVariable("PROOF_PUBLIC_TLS_URL");
if (!string.IsNullOrEmpty(publicUrl))
{
    using var response = await client.GetAsync(publicUrl);
    response.EnsureSuccessStatusCode();
    Console.WriteLine("PASS: same .NET HttpClient retains public TLS trust with the private-only bundle");
}
