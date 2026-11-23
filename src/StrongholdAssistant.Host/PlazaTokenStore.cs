using System.Security.Cryptography;
using System.Text;

namespace StrongholdAssistant.Host;

public sealed class PlazaTokenStore(string userDirectory)
{
    private readonly string _path = Path.Combine(userDirectory, "plaza-token.bin");

    public void Save(string token)
    {
#if LINUX_PORT
        var encrypted = LinuxSecretBox.Protect(Encoding.UTF8.GetBytes(token));
#else
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
#endif
        Directory.CreateDirectory(userDirectory);
        File.WriteAllBytes(_path, encrypted);
    }

    public string? Read()
    {
        if (!File.Exists(_path)) return null;
        try
        {
#if LINUX_PORT
            return Encoding.UTF8.GetString(LinuxSecretBox.Unprotect(File.ReadAllBytes(_path)));
#else
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser));
#endif
        }
        catch (CryptographicException) { return null; }
    }

    public void Delete() => File.Delete(_path);
}
