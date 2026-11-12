using System.IO;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class PrivateCollectionPasswordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-private-" + Guid.NewGuid().ToString("N"));
    private string Config => Path.Combine(_root, "config.yml");
    private string CredentialPath => Path.GetFullPath(Config) + ".private-collection.json";

    public PrivateCollectionPasswordTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MissingFileMeansNoPasswordAndReadOnlyCannotWrite()
    {
        var store = new PrivateCollectionPasswordStore(Config);
        Assert.False(store.HasPassword);
        Assert.True(store.CanPersist);
        var readOnly = new PrivateCollectionPasswordStore(Config, readOnly: true);
        Assert.False(readOnly.CanPersist);
        Assert.Throws<InvalidOperationException>(() => readOnly.Set("abcd"));
        var noPath = new PrivateCollectionPasswordStore(null);
        Assert.False(noPath.CanPersist);
        Assert.Throws<InvalidOperationException>(() => noPath.Set("abcd"));
    }

    [Fact]
    public void SetupStoresOnlyASaltedHashAndVerificationIsCaseInsensitive()
    {
        var store = new PrivateCollectionPasswordStore(Config);
        store.Set("AbC123");
        Assert.True(store.HasPassword);
        var json = File.ReadAllText(CredentialPath);
        Assert.DoesNotContain("AbC123", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Salt\"", json); Assert.Contains("\"Hash\"", json);

        var reopened = new PrivateCollectionPasswordStore(Config);
        Assert.True(reopened.HasPassword);
        Assert.True(reopened.Verify("abc123"));
        Assert.True(reopened.Verify("ABC123"));
        Assert.False(reopened.Verify("abc124"));
        Assert.False(reopened.Verify(""));

        var access = new PrivateCollectionAccess(reopened);
        Assert.False(access.IsUnlocked);
        Assert.False(access.TryUnlock("wrong"));
        Assert.True(access.TryUnlock("abc123"));
        Assert.True(access.IsUnlocked);
        access.Lock();
        Assert.False(access.IsUnlocked);
    }

    [Theory]
    [InlineData("abcd", true)]
    [InlineData("A1b2C3d4", true)]
    [InlineData("abc", false)]
    [InlineData("123456789012345678901234567890123", false)]
    [InlineData("abc 12", false)]
    [InlineData("密码1234", false)]
    [InlineData("abc-12", false)]
    public void PasswordAlphabetAndLengthAreEnforced(string password, bool valid) =>
        Assert.Equal(valid, PrivateCollectionPasswordStore.Validate(password) is null);

    [Fact]
    public void ChangeAndRemoveRequireTheCurrentPassword()
    {
        var access = new PrivateCollectionAccess(new PrivateCollectionPasswordStore(Config));
        access.SetPassword("oldpass");
        Assert.Throws<ArgumentException>(() => access.ChangePassword("wrong", "newpass"));
        Assert.True(access.Store.Verify("oldpass"));
        access.ChangePassword("OLDPASS", "newpass");
        Assert.False(access.Store.Verify("oldpass"));
        Assert.True(access.Store.Verify("NEWPASS"));
        Assert.True(access.IsUnlocked);
        Assert.Throws<ArgumentException>(() => access.RemovePassword("wrong"));
        Assert.True(access.Store.HasPassword);
        access.RemovePassword("newpass");
        Assert.False(access.Store.HasPassword);
        Assert.False(File.Exists(CredentialPath));
        Assert.True(access.IsUnlocked);
    }

    [Fact]
    public void FormExplainsValidationAndWrongPasswordWithoutClosingTheCollection()
    {
        var access = new PrivateCollectionAccess(new PrivateCollectionPasswordStore(Config));
        var form = new PrivateCollectionPasswordForm(access);
        Assert.False(form.Setup("abc", "abc"));
        Assert.Contains("4–32", form.Error);
        Assert.False(form.Setup("abcd", "abce"));
        Assert.Contains("不一致", form.Error);
        Assert.True(form.Setup("abcd", "ABCD"));
        Assert.True(access.IsUnlocked);

        access.Lock();
        Assert.False(form.Unlock(""));
        Assert.Contains("请输入密码", form.Error);
        Assert.False(form.Unlock("wrong"));
        Assert.Contains("不正确", form.Error);
        Assert.False(access.IsUnlocked);
        Assert.True(form.Unlock("AbCd"));

        Assert.False(form.Change("bad", "next1", "next1"));
        Assert.Contains("当前密码", form.Error);
        Assert.True(form.Change("abcd", "next1", "NEXT1"));
        Assert.True(form.Remove("next1"));
        Assert.False(access.Store.HasPassword);
    }

    [Fact]
    public void CorruptOrMalformedCredentialIsTreatedAsUnset()
    {
        File.WriteAllText(CredentialPath, "{broken");
        Assert.False(new PrivateCollectionPasswordStore(Config).HasPassword);
        File.WriteAllText(CredentialPath, """{"Version":1,"Salt":"xx","Hash":"yy","Iterations":210000}""");
        Assert.False(new PrivateCollectionPasswordStore(Config).HasPassword);
        File.WriteAllText(CredentialPath, """{"Version":99,"Salt":"AAAAAAAAAAAAAAAAAAAAAA==","Hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","Iterations":210000}""");
        Assert.False(new PrivateCollectionPasswordStore(Config).HasPassword);
    }

    [Fact]
    public void ConsolePasswordKeyboardRejectsSpacesAndStopsAtThePasswordLimit()
    {
        var keyboard = new ConsoleKeyboard("", PrivateCollectionPasswordStore.MaximumLength, alphanumericOnly: true);
        keyboard.Move(6, 3); // 空格
        keyboard.Press();
        Assert.Equal("", keyboard.Text);
        keyboard.Move(-6, -3); // A
        keyboard.Press();
        Assert.Equal("A", keyboard.Text);
        var limited = new ConsoleKeyboard("", 2, alphanumericOnly: true);
        limited.Press(); limited.Move(1, 0); limited.Press(); limited.Move(1, 0); limited.Press();
        Assert.Equal("AB", limited.Text);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
