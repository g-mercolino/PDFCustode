using System.Security.Cryptography;
using System.Text.Json;
using PdfCustode.Core;
using PdfCustode.Server;

if (args.Length > 0 && args[0] == "--annual-quota-child")
{
    AnnualActivationTests.RunChild(args);
    return;
}
await AnnualActivationTests.RunAsync();
await GraphTests.RunAsync();
await LocalIntegrationTests.RunAsync();
await SharePointSyncTests.RunAsync();

using RSA authority = RSA.Create(3072);
using RSA device = RSA.Create(2048);
using RSA other = RSA.Create(2048);
string pub = Convert.ToBase64String(device.ExportSubjectPublicKeyInfo());
string otherPub = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
string authorityPub = Convert.ToBase64String(authority.ExportSubjectPublicKeyInfo());
byte[] key = RandomNumberGenerator.GetBytes(32);
byte[] pdf = "%PDF-test"u8.ToArray();
var doc = Crypto.Encrypt(pdf, "test", "Test", key);
Assert(pdf.SequenceEqual(Crypto.Decrypt(doc, key)), "PDF roundtrip");
Reject(() => Crypto.Decrypt(doc with { DocumentId = "other" }, key));
Reject(() => Crypto.Decrypt(doc, RandomNumberGenerator.GetBytes(32)));
var oldLicense = Crypto.Sign(new("old-license", doc.DocumentId, Crypto.DeviceHash(pub), "unused",
    DateTimeOffset.UtcNow.AddDays(-31).ToString("O")), authority);
Reject(() => Crypto.Verify(oldLicense, authorityPub, doc.DocumentId, pub));

string folder = Path.Combine(Path.GetTempPath(), "pdfcustode-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    string code = ActivationStore.Create(folder, doc.DocumentId, key, authority);
    using var store = new ActivationStore(folder);
    var request = new ActivationRequest(code, doc.DocumentId, pub);
    Assert(store.Activate(request with { DocumentId = "other" }) is null, "wrong document rejected");
    Assert(store.Activate(request with { Code = code + "1" }) is null, "wrong code rejected");
    var race = await Task.WhenAll(
        Task.Run(() => store.Activate(request)),
        Task.Run(() => store.Activate(request with { DevicePublicKey = otherPub })));
    Assert(race.Count(x => x is not null) == 1, "only one device may claim code");
    var winner = race[0] is not null ? request : request with { DevicePublicKey = otherPub };
    var signed = race[0] ?? race[1]!;
    Assert(store.Activate(winner) == signed, "retry returns identical license");
    using (var reopened = new ActivationStore(folder))
        Assert(reopened.Activate(winner) == signed, "retry after restart");
    string anotherCode = store.IssueCode();
    var loser = race[0] is null ? request : request with { DevicePublicKey = otherPub };
    Assert(store.Activate(loser with { Code = anotherCode }) is not null, "a newly issued code can bind a second device");
    Assert(store.Activate(winner) == signed, "issuing a code preserves existing licenses");
    var payload = Crypto.Verify(signed, authorityPub, doc.DocumentId, winner.DevicePublicKey);
    var issuedAt = DateTimeOffset.Parse(payload.IssuedAt);
    Assert(payload.ExpiresAt is not null && Crypto.GetExpiration(payload) == issuedAt.AddDays(30), "new licenses expire after exactly 30 days");
    Assert(Crypto.Verify(signed, authorityPub, doc.DocumentId, winner.DevicePublicKey, issuedAt.AddDays(30).AddTicks(-1)) == payload, "license valid immediately before expiry");
    Reject(() => Crypto.Verify(signed, authorityPub, doc.DocumentId, winner.DevicePublicKey, issuedAt.AddDays(30)));
    Reject(() => Crypto.Verify(signed, authorityPub, doc.DocumentId, winner.DevicePublicKey, issuedAt.AddDays(31)));
    Assert(Crypto.Verify(signed, authorityPub, doc.DocumentId, winner.DevicePublicKey, issuedAt.AddSeconds(-1)) == payload, "small device clock differences are tolerated");
    Reject(() => Crypto.Verify(signed, authorityPub, doc.DocumentId, winner.DevicePublicKey, issuedAt.AddMinutes(-6)));
    var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(new { payload.LicenseId, payload.DocumentId, payload.DeviceKeyHash, payload.WrappedKey, payload.IssuedAt });
    var legacy = new SignedLicense(Convert.ToBase64String(legacyBytes),
        Convert.ToBase64String(authority.SignData(legacyBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    Assert(Crypto.Verify(legacy, authorityPub, doc.DocumentId, winner.DevicePublicKey, issuedAt.AddDays(29)).ExpiresAt is null, "legacy licenses retain original activation date");
    Reject(() => Crypto.Verify(legacy, authorityPub, doc.DocumentId, winner.DevicePublicKey, issuedAt.AddDays(30)));
    var invalidExpiry = Crypto.Sign(payload with { ExpiresAt = "invalid" }, authority);
    Reject(() => Crypto.Verify(invalidExpiry, authorityPub, doc.DocumentId, winner.DevicePublicKey));
    Reject(() => Crypto.GetExpiration(payload with { ExpiresAt = issuedAt.AddDays(31).ToString("O") }));
    Reject(() => Crypto.GetExpiration(payload with { IssuedAt = "invalid" }));
    Assert(key.SequenceEqual(Crypto.UnwrapKey(payload, race[0] is not null ? device : other)), "license unwrap");
    Reject(() => Crypto.Verify(signed with { Signature = Convert.ToBase64String(new byte[384]) }, authorityPub, doc.DocumentId, winner.DevicePublicKey));
    Reject(() => Crypto.Verify(signed, authorityPub, "other", winner.DevicePublicKey));
    Reject(() => Crypto.Verify(signed, authorityPub, doc.DocumentId, race[0] is not null ? otherPub : pub));
    var catalog = new ActivationCatalog(folder);
    Assert(catalog.Activate(winner) == signed, "catalog supports legacy server folders");
    string secondFolder = Path.Combine(folder, "second", "server");
    Directory.CreateDirectory(secondFolder);
    string secondCode = ActivationStore.Create(secondFolder, "second-document", key, authority);
    var secondRequest = new ActivationRequest(secondCode, "second-document", pub);
    var secondLicense = catalog.Activate(secondRequest);
    Assert(secondLicense is not null, "catalog discovers added documents without restart");
    Assert(catalog.Activate(secondRequest with { DocumentId = "../../unknown" }) is null, "unknown document rejected");
    Assert(catalog.Activate(secondRequest with { Code = code }) is null, "code is bound to its document");

    var library = new DocumentLibrary(Path.Combine(folder, "library"));
    var package = new DocumentPackage(1, doc, authorityPub);
    var firstImport = await Import(package);
    await File.WriteAllTextAsync(Path.Combine(firstImport.Folder, "license.json"), "existing license");
    var repeated = await Import(package);
    Assert(firstImport.Folder == repeated.Folder && library.Documents().Count() == 1, "reimport is idempotent");
    Assert(await File.ReadAllTextAsync(Path.Combine(repeated.Folder, "license.json")) == "existing license", "reimport preserves license");
    var secondImport = await Import(package with { Document = doc with { DocumentId = "../second" } });
    Assert(secondImport.Folder != firstImport.Folder && Path.GetDirectoryName(secondImport.Folder) == Path.GetDirectoryName(firstImport.Folder), "document IDs cannot escape library");
    Assert(!File.Exists(Path.Combine(secondImport.Folder, "license.json")), "licenses are isolated per document");
    Assert(await DocumentLibrary.ReadAsync(firstImport.Folder) == package, "package roundtrip");
    try { await Import(package with { Version = 99 }); throw new Exception("unsupported package accepted"); }
    catch (InvalidDataException) { }
    try { await Import(package with { Document = doc with { Ciphertext = "invalid base64!" } }); throw new Exception("invalid package accepted"); }
    catch (FormatException) { }
    Assert(library.Documents().Count() == 2, "failed imports leave library intact");

    var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    string expiryFolder = Path.Combine(folder, "expiry");
    string expiryCode = ActivationStore.Create(expiryFolder, "expiry-document", key, authority);
    using var expiringStore = new ActivationStore(expiryFolder, clock);
    var expiryRequest = new ActivationRequest(expiryCode, "expiry-document", pub);
    var originalLicense = expiringStore.Activate(expiryRequest)!;
    clock.Now = clock.Now.AddDays(31);
    Assert(expiringStore.Activate(expiryRequest) == originalLicense, "reactivation never extends an existing code");
    Reject(() => Crypto.Verify(originalLicense, authorityPub, "expiry-document", pub, clock.Now));
    string renewalCode = expiringStore.IssueCode();
    var renewedLicense = expiringStore.Activate(expiryRequest with { Code = renewalCode })!;
    var renewed = Crypto.Verify(renewedLicense, authorityPub, "expiry-document", pub, clock.Now);
    Assert(Crypto.GetExpiration(renewed) == clock.Now.AddDays(30), "new renewal code starts a fresh 30-day term");
    Assert(expiringStore.Activate(expiryRequest) == originalLicense, "renewal preserves the original code history");

    async Task<LibraryDocument> Import(DocumentPackage value)
    {
        using var source = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value, PackageJson.Default.DocumentPackage));
        return await library.ImportAsync(source);
    }
}
finally { Directory.Delete(folder, true); }

Console.WriteLine("PDF Custode tests passed.");

static void Assert(bool passed, string name)
{
    if (!passed) throw new Exception(name);
}
static void Reject(Action action)
{
    try { action(); }
    catch (CryptographicException) { return; }
    throw new Exception("Expected rejection");
}

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

