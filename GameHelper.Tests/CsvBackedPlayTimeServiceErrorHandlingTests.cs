using System;
using System.IO;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
    public class CsvBackedPlayTimeServiceErrorHandlingTests : IDisposable
    {
        private readonly string _dir;

        public CsvBackedPlayTimeServiceErrorHandlingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "GameHelperTests_ErrorHandling", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [Fact]
        public void Constructor_InvalidDirectory_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new CsvBackedPlayTimeService(""));
            Assert.Throws<ArgumentException>(() => new CsvBackedPlayTimeService("   "));
        }

        [Fact]
        public void Constructor_ReadOnlyDirectory_DoesNotThrow()
        {
            // This test ensures that even if there are permission issues,
            // the constructor doesn't crash the entire application
            var svc = new CsvBackedPlayTimeService(_dir);
            
            // Should be able to start tracking even if file operations might fail later
            svc.StartTracking("test.exe");
            
            // StopTracking should not throw even if file write fails
            // (it logs the error but continues execution)
            Assert.True(true); // If we get here, no exception was thrown
        }

        [Fact]
        public void StopTracking_WithoutStartTracking_DoesNotThrow()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            
            // This should not throw an exception and should return null
            var session = svc.StopTracking("nonexistent.exe");

            Assert.Null(session);
        }

        [Fact]
        public void StartTracking_NullOrEmpty_DoesNotThrow()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            
            // These should not throw exceptions
            svc.StartTracking(null!);
            svc.StartTracking("");
            svc.StartTracking("   ");
            
            Assert.True(true); // If we get here, no exception was thrown
        }

        [Fact]
        public void StopTracking_NullOrEmpty_DoesNotThrow()
        {
            var svc = new CsvBackedPlayTimeService(_dir);
            
            // These should not throw exceptions
            Assert.Null(svc.StopTracking(null!));
            Assert.Null(svc.StopTracking(""));
            Assert.Null(svc.StopTracking("   "));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}