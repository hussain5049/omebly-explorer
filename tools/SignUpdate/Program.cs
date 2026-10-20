// dotnet run -- newkey <key.pem>                      makes the signing key and prints the public key for Updater.cs
// dotnet run -- sign <key.pem> <version> <url> <file>   prints update.json (version, url, SHA-256 of the installer, signature)
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length >= 2 && args[0] == "newkey")
{
    if (File.Exists(args[1])) { Console.Error.WriteLine("A key is already there; not replaced."); return 1; }
    using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(args[1], k.ExportPkcs8PrivateKeyPem());
    Console.WriteLine(Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    return 0;
}
if (args.Length >= 5 && args[0] == "sign")
{
    using var k = ECDsa.Create(); k.ImportFromPem(File.ReadAllText(args[1]));
    var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[4])));
    var msg = $"{args[2]}|{args[3]}|{sha}";
    var sig = Convert.ToBase64String(k.SignData(Encoding.UTF8.GetBytes(msg), HashAlgorithmName.SHA256));
    Console.WriteLine(JsonSerializer.Serialize(new { version = args[2], url = args[3], sha256 = sha, signature = sig }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
// dotnet run -- verify <public key base64> <update.json>   checks a published update file the way the app does
if (args.Length >= 3 && args[0] == "verify")
{
    using var k = ECDsa.Create(); k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(args[1]), out _);
    using var doc = JsonDocument.Parse(File.ReadAllText(args[2])); var r = doc.RootElement;
    var msg = $"{r.GetProperty("version").GetString()}|{r.GetProperty("url").GetString()}|{r.GetProperty("sha256").GetString()}";
    var ok = k.VerifyData(Encoding.UTF8.GetBytes(msg), Convert.FromBase64String(r.GetProperty("signature").GetString()!), HashAlgorithmName.SHA256);
    Console.WriteLine(ok ? "signature OK" : "signature BAD"); return ok ? 0 : 1;
}
Console.Error.WriteLine("newkey <key.pem> | sign <key.pem> <version> <url> <installer> | verify <public key> <update.json>");
return 2;
