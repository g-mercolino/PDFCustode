using System.Security.Cryptography;
using PdfCustode.Core;
using PdfCustode.Server;

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
    Assert(key.SequenceEqual(Crypto.UnwrapKey(payload, race[0] is not null ? device : other)), "license unwrap");
    Reject(() => Crypto.Verify(signed with { Signature = Convert.ToBase64String(new byte[384]) }, authorityPub, doc.DocumentId, winner.DevicePublicKey));
    Reject(() => Crypto.Verify(signed, authorityPub, "other", winner.DevicePublicKey));
    Reject(() => Crypto.Verify(signed, authorityPub, doc.DocumentId, race[0] is not null ? otherPub : pub));
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

