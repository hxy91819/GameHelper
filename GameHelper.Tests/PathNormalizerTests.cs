using System;
using System.IO;
using GameHelper.Core.Utilities;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// PathNormalizer 独立单元测试。
    /// 覆盖路径归一化、名称归一化、Windows 驱动器/UNC 解析、尾部斜杠处理。
    /// 这些测试在 Step 1 拆出 PathNormalizer 后补充，确保纯函数模块行为正确。
    /// </summary>
    public class PathNormalizerTests
    {
        #region NormalizeName

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData("game", "game.exe")]
        [InlineData("game.exe", "game.exe")]
        [InlineData("GAME.EXE", "GAME.EXE")]
        [InlineData("C:\\Games\\game.exe", "game.exe")]
        [InlineData("  game  ", "game.exe")]
        public void NormalizeName_VariousInputs_ReturnsExpected(string? input, string? expected)
        {
            var actual = PathNormalizer.NormalizeName(input);
            Assert.Equal(expected, actual, StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region NormalizePath

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData(@"C:\Games\game.exe", @"C:\Games\game.exe")]
        [InlineData(@"c:\games\game.exe", @"C:\Games\game.exe")]
        [InlineData(@"C:\Games\game.exe\", @"C:\Games\game.exe")]
        [InlineData(@"\\server\share\games\game.exe", @"\\server\share\games\game.exe")]
        [InlineData(@"\\server\share\games\game.exe\", @"\\server\share\games\game.exe")]
        public void NormalizePath_VariousInputs_ReturnsExpected(string? input, string? expected)
        {
            var actual = PathNormalizer.NormalizePath(input);
            Assert.Equal(expected, actual, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void NormalizePath_RelativePath_ResolvesToAbsolute()
        {
            var input = "game.exe";
            var expected = Path.GetFullPath("game.exe");
            var actual = PathNormalizer.NormalizePath(input);
            Assert.Equal(expected, actual);
        }

        #endregion

        #region TryResolveDrivePath

        [Theory]
        [InlineData(@"C:\Games\game.exe", @"C:\Games\game.exe", @"C:\Games\")]
        [InlineData(@"C:\Games\Foo\..\game.exe", @"C:\Games\game.exe", @"C:\Games\")]
        [InlineData(@"C:\Games\Foo\..\..\game.exe", @"C:\game.exe", @"C:\")]
        [InlineData(@"C:\.\Games\game.exe", @"C:\Games\game.exe", @"C:\Games\")]
        [InlineData(@"C:\Games\", @"C:\Games\", @"C:\Games\")]
        public void TryResolveDrivePath_VariousInputs_ReturnsExpected(string input, string expectedPath, string expectedDir)
        {
            var ok = PathNormalizer.TryResolveWindowsPath(input, out var normalizedPath, out var directory);
            Assert.True(ok);
            Assert.Equal(expectedPath, normalizedPath);
            Assert.Equal(expectedDir, directory);
        }

        [Fact]
        public void TryResolveDrivePath_EscapeRoot_ReturnsFalse()
        {
            var ok = PathNormalizer.TryResolveWindowsPath(@"C:\..\game.exe", out _, out _);
            Assert.False(ok);
        }

        #endregion

        #region TryResolveUncPath

        [Theory]
        [InlineData(@"\\server\share\games\game.exe", @"\\server\share\games\game.exe", @"\\server\share\games\")]
        [InlineData(@"\\server\share\games\Foo\..\game.exe", @"\\server\share\games\game.exe", @"\\server\share\games\")]
        [InlineData(@"\\server\share\games\", @"\\server\share\games\", @"\\server\share\games\")]
        public void TryResolveUncPath_VariousInputs_ReturnsExpected(string input, string expectedPath, string expectedDir)
        {
            var ok = PathNormalizer.TryResolveWindowsPath(input, out var normalizedPath, out var directory);
            Assert.True(ok);
            Assert.Equal(expectedPath, normalizedPath);
            Assert.Equal(expectedDir, directory);
        }

        [Fact]
        public void TryResolveUncPath_DoubleDotDoesNotEscapeShare()
        {
            // UNC 路径中 ".." 无法弹掉 server\share 这对基础段
            var ok = PathNormalizer.TryResolveWindowsPath(@"\\server\share\..\..\game.exe", out var path, out _);
            Assert.True(ok);
            Assert.Equal(@"\\server\share\game.exe", path);
        }

        #endregion

        #region EnsureTrailingSeparator

        [Theory]
        [InlineData("", "")]
        [InlineData("C:\\Games", "C:\\Games\\")]
        [InlineData("C:\\Games\\", "C:\\Games\\")]
        public void EnsureTrailingSeparator_VariousInputs_ReturnsExpected(string input, string expected)
        {
            var actual = PathNormalizer.EnsureTrailingSeparator(input);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void EnsureTrailingSeparator_PreferBackslash_UsesBackslash()
        {
            var actual = PathNormalizer.EnsureTrailingSeparator("C:/Games", preferBackslash: true);
            Assert.Equal("C:/Games\\", actual);
        }

        #endregion
    }
}
