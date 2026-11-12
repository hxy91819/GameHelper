using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameHelper.Desktop;

/// <summary>Local private-collection credential. A salted hash verifier, never the password itself.</summary>
internal sealed class PrivateCollectionPasswordStore
{
    internal const int MinimumLength = 4;
    internal const int MaximumLength = 32;
    private const int CurrentVersion = 1;
    private const int DefaultIterations = 210_000;
    private const int MinimumIterations = 10_000;
    private const int MaximumIterations = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string? _path;
    private readonly bool _canPersist;
    private Credential? _credential;

    internal PrivateCollectionPasswordStore(string? configPath, bool readOnly = false)
    {
        _canPersist = !readOnly && configPath is not null;
        _path = configPath is null ? null : Path.GetFullPath(configPath) + ".private-collection.json";
        _credential = Read(_path);
    }

    internal bool HasPassword => _credential is not null;
    internal bool CanPersist => _canPersist;

    /// <summary>Creates a new salted verifier; rejects passwords outside the documented alphabet.</summary>
    internal void Set(string password)
    {
        EnsureWritable();
        if (Validate(password) is { } error) throw new ArgumentException(error, nameof(password));
        var salt = RandomNumberGenerator.GetBytes(16);
        var credential = new Credential(CurrentVersion, Convert.ToBase64String(salt),
            Convert.ToBase64String(Hash(password, salt, DefaultIterations)), DefaultIterations);
        DesktopDocumentStore.Write(_path!, credential);
        _credential = credential;
    }

    internal bool Verify(string? password)
    {
        if (_credential is not { } credential || string.IsNullOrEmpty(password)) return false;
        try
        {
            var salt = Convert.FromBase64String(credential.Salt);
            var expected = Convert.FromBase64String(credential.Hash);
            var iterations = Math.Clamp(credential.Iterations, MinimumIterations, MaximumIterations);
            return CryptographicOperations.FixedTimeEquals(Hash(password, salt, iterations), expected);
        }
        catch (FormatException) { return false; }
    }

    internal void Change(string current, string next)
    {
        if (!Verify(current)) throw new ArgumentException("当前密码不正确。", nameof(current));
        Set(next);
    }

    internal void Clear(string current)
    {
        if (!Verify(current)) throw new ArgumentException("当前密码不正确。", nameof(current));
        EnsureWritable();
        File.Delete(_path!);
        _credential = null;
    }

    /// <summary>Letters are case-insensitive so the console keyboard can enter the same password.</summary>
    internal static string? Validate(string password) =>
        password.Length is < MinimumLength or > MaximumLength ? $"密码需为 {MinimumLength}–{MaximumLength} 位字母或数字。"
        : !password.All(char.IsAsciiLetterOrDigit) ? "密码只能使用字母和数字。" : null;

    private static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password.ToLowerInvariant(), salt, iterations, HashAlgorithmName.SHA256, 32);

    private void EnsureWritable()
    {
        if (!_canPersist) throw new InvalidOperationException("当前界面不能保存私人收藏密码。");
    }

    private static Credential? Read(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var credential = JsonSerializer.Deserialize<Credential>(File.ReadAllText(path), JsonOptions);
            return credential is { Version: CurrentVersion, Iterations: >= MinimumIterations and <= MaximumIterations }
                && TryDecode(credential.Salt, 16) && TryDecode(credential.Hash, 32) ? credential : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }

    private static bool TryDecode(string? value, int length)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try { return Convert.FromBase64String(value).Length == length; }
        catch (FormatException) { return false; }
    }

    private sealed record Credential(int Version, string Salt, string Hash, int Iterations);
}

/// <summary>One window's unlock state over the shared credential; leaving the collection locks it.</summary>
internal sealed class PrivateCollectionAccess(PrivateCollectionPasswordStore store)
{
    internal PrivateCollectionPasswordStore Store => store;
    internal bool HasPassword => store.HasPassword;
    internal bool IsUnlocked { get; private set; }

    internal bool TryUnlock(string? password)
    {
        if (!store.Verify(password)) return false;
        IsUnlocked = true;
        return true;
    }

    internal void SetPassword(string password)
    {
        store.Set(password);
        IsUnlocked = true;
    }

    internal void ChangePassword(string current, string next)
    {
        store.Change(current, next);
        IsUnlocked = true;
    }

    internal void RemovePassword(string current)
    {
        store.Clear(current);
        IsUnlocked = true;
    }

    internal void Lock() => IsUnlocked = false;
}

/// <summary>Validation and error text for the password dialog, independent of WPF.</summary>
internal sealed class PrivateCollectionPasswordForm(PrivateCollectionAccess access)
{
    internal string? Error { get; private set; }
    internal bool IsUnlocked => access.IsUnlocked;

    internal bool Unlock(string password)
    {
        Error = null;
        if (string.IsNullOrEmpty(password)) { Error = "请输入密码。"; return false; }
        if (access.TryUnlock(password)) return true;
        Error = "密码不正确，请重试。";
        return false;
    }

    internal bool Setup(string password, string confirm)
    {
        if (ValidateNew(password, confirm) is { } error) { Error = error; return false; }
        return Save(() => access.SetPassword(password), "密码未能保存：");
    }

    internal bool Change(string current, string next, string confirm)
    {
        if (string.IsNullOrEmpty(current)) { Error = "请输入当前密码。"; return false; }
        if (ValidateNew(next, confirm) is { } error) { Error = error; return false; }
        return Save(() => access.ChangePassword(current, next), "密码未能保存：");
    }

    internal bool Remove(string current)
    {
        if (string.IsNullOrEmpty(current)) { Error = "请输入当前密码。"; return false; }
        return Save(() => access.RemovePassword(current), "密码未能取消：");
    }

    private string? ValidateNew(string password, string confirm)
    {
        if (PrivateCollectionPasswordStore.Validate(password) is { } problem) return problem;
        return string.Equals(password, confirm, StringComparison.OrdinalIgnoreCase) ? null : "两次输入的新密码不一致。";
    }

    private bool Save(Action action, string failurePrefix)
    {
        Error = null;
        try { action(); return true; }
        catch (ArgumentException ex) { Error = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Error = failurePrefix + ex.Message; }
        return false;
    }
}
