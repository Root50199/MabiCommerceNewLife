using System.IO;
using System.Text.Json;

namespace MabiCommerceNewLife;

public sealed class CommunityCredentialStore(string path)
{
    public const string FileName = "community-credentials.json";

    public sealed class Credentials
    {
        public required string Endpoint { get; init; }
        public required string ConsentVersion { get; init; }
        public required string InstallationId { get; init; }
        public required string Token { get; init; }
    }

    public Credentials? Load(string endpoint, string consentVersion)
    {
        if (!File.Exists(path)) return null;
        var credentials = JsonSerializer.Deserialize<Credentials>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Saved sharing credentials are empty. Sharing remains disconnected.");
        Validate(credentials, endpoint, consentVersion);
        return credentials;
    }

    public void Save(Credentials credentials)
    {
        Validate(credentials, credentials.Endpoint, credentials.ConsentVersion);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(credentials));
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(Credentials credentials, string endpoint, string consentVersion)
    {
        if (credentials.Endpoint != endpoint || credentials.ConsentVersion != consentVersion)
            throw new InvalidDataException("Saved sharing credentials belong to a different service or policy. Nothing was sent.");
        if (!Guid.TryParse(credentials.InstallationId, out var id) || id == Guid.Empty ||
            credentials.Token is null || credentials.Token.Length != 64 ||
            credentials.Token.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidDataException("Saved sharing credentials are invalid. Sharing remains disconnected.");
    }
}
