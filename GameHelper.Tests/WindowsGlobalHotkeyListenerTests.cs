using System.Threading;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Hotkeys;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class WindowsGlobalHotkeyListenerTests
{
    [Fact]
    public void Start_RegistersWithModNoRepeat()
    {
        var platform = new FakeWindowsHotkeyPlatform();
        var listener = new WindowsGlobalHotkeyListener(NullLogger<WindowsGlobalHotkeyListener>.Instance, platform);

        var result = listener.Start(new HotkeyBinding("Ctrl+Alt+F10", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x79), () => { });

        Assert.True(result.Success, result.Message);
        Assert.Equal((uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat), platform.LastModifiers);

        listener.Stop();
    }

    [Fact]
    public void Stop_PostsQuitMessageToListenerThread()
    {
        var platform = new FakeWindowsHotkeyPlatform();
        var listener = new WindowsGlobalHotkeyListener(NullLogger<WindowsGlobalHotkeyListener>.Instance, platform);

        var result = listener.Start(new HotkeyBinding("Ctrl+Alt+F10", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x79), () => { });
        Assert.True(result.Success, result.Message);

        listener.Stop();

        Assert.True(platform.PostQuitCalled);
    }

    [Fact]
    public void HotkeyMessage_InvokesCallbackOnce()
    {
        var platform = new FakeWindowsHotkeyPlatform();
        var listener = new WindowsGlobalHotkeyListener(NullLogger<WindowsGlobalHotkeyListener>.Instance, platform);
        var signal = new ManualResetEventSlim(false);
        var count = 0;

        var result = listener.Start(
            new HotkeyBinding("Ctrl+Alt+F10", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x79),
            () =>
            {
                Interlocked.Increment(ref count);
                signal.Set();
            });

        Assert.True(result.Success, result.Message);
        platform.EnqueueHotkey();
        Assert.True(signal.Wait(1000));

        listener.Stop();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Start_WhenRegisterFails_ReturnsConflictMessage()
    {
        var platform = new FakeWindowsHotkeyPlatform { RegisterResult = false };
        var listener = new WindowsGlobalHotkeyListener(NullLogger<WindowsGlobalHotkeyListener>.Instance, platform);

        var result = listener.Start(new HotkeyBinding("Ctrl+Alt+F10", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x79), () => { });

        Assert.False(result.Success);
        Assert.Contains("占用", result.Message);
    }

    private sealed class FakeWindowsHotkeyPlatform : IWindowsHotkeyPlatform
    {
        private readonly AutoResetEvent _messageSignal = new(false);
        private readonly Queue<NativeMessage> _messages = new();

        public bool RegisterResult { get; set; } = true;

        public uint LastModifiers { get; private set; }

        public bool PostQuitCalled { get; private set; }

        public uint GetCurrentThreadId() => 1234;

        public void EnsureMessageQueue()
        {
        }

        public bool RegisterHotKey(nint handle, int id, uint modifiers, uint virtualKey)
        {
            LastModifiers = modifiers;
            return RegisterResult;
        }

        public bool UnregisterHotKey(nint handle, int id)
        {
            return true;
        }

        public int GetMessage(out NativeMessage message)
        {
            while (true)
            {
                lock (_messages)
                {
                    if (_messages.Count > 0)
                    {
                        message = _messages.Dequeue();
                        return message.Message == 0x0012 ? 0 : 1;
                    }
                }

                _messageSignal.WaitOne(1000);
            }
        }

        public bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam)
        {
            PostQuitCalled = true;
            Enqueue(message, wParam);
            return true;
        }

        public void EnqueueHotkey()
        {
            Enqueue(0x0312, 0x4748);
        }

        private void Enqueue(uint message, nuint wParam)
        {
            lock (_messages)
            {
                _messages.Enqueue(new NativeMessage
                {
                    Message = message,
                    WParam = wParam
                });
            }

            _messageSignal.Set();
        }
    }
}
