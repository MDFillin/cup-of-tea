using System.Security.Cryptography;
using System.Text.Json;

namespace PVpn.Core.Profiles;

public sealed class ProfileData
{
    public List<Profile> Profiles { get; set; } = [];
    public Guid? SelectedId { get; set; }
    public List<Subscription> Subscriptions { get; set; } = [];

    /// <summary>
    /// Добавляет профиль или заменяет профиль с тем же именем (Id сохраняется). Имя без флага-эмодзи тоже считается
    /// тем же («🇳🇱 my-hy2» и «my-hy2»), если такой сервер один. Возвращает true, если добавлен новый.
    /// </summary>
    public bool Upsert(Profile profile)
    {
        var index = Profiles.FindIndex(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            var plain = Profiles.FindAll(p => string.Equals(PlainName(p.Name), PlainName(profile.Name), StringComparison.OrdinalIgnoreCase));
            if (plain.Count == 1)
                index = Profiles.IndexOf(plain[0]);
        }
        if (index < 0)
        {
            Profiles.Add(profile);
            return true;
        }
        Profiles[index] = profile with { Id = Profiles[index].Id };
        return false;
    }

    /// <summary>Ищет по Id, по полному имени или по имени без флага-эмодзи впереди («my-xhttp» найдёт «🇳🇱 my-xhttp»).</summary>
    public Profile? Find(string nameOrId) =>
        Profiles.FirstOrDefault(p => p.Id.ToString() == nameOrId)
        ?? Profiles.FirstOrDefault(p => string.Equals(p.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? Profiles.FirstOrDefault(p => string.Equals(PlainName(p.Name), nameOrId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Имя без начальных эмодзи, пробелов и знаков.</summary>
    public static string PlainName(string name)
    {
        var i = 0;
        while (i < name.Length && !char.IsLetterOrDigit(name[i]))
            i++;
        return name[i..];
    }
}

/// <summary>Профили пользователя в %APPDATA%\PVpn, зашифрованные DPAPI (расшифровать может только этот пользователь Windows).</summary>
public sealed class ProfileStore(string? path = null)
{
    // Не менять: с этой добавкой зашифрованы уже сохранённые серверы (рабочее имя кода — PVpn).
    private static readonly byte[] Entropy = "PVpn.profiles.v1"u8.ToArray();

    public static string DefaultPath =>
        Path.Combine(Product.DataDir(Environment.SpecialFolder.ApplicationData), "profiles.dat");

    public string FilePath { get; } = path ?? DefaultPath;

    public ProfileData Load()
    {
        if (!File.Exists(FilePath))
            return new ProfileData();
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize(plain, CoreJsonContext.Default.ProfileData) ?? new ProfileData();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Save(ProfileData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(data, CoreJsonContext.Default.ProfileData);
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, FilePath, overwrite: true);
    }
}
