using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Desktop;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class MonitorDeviceBindingTests
{
    private static MonitorDeviceIdentity Device(string name = "Dell", string path = "dell-path", string? hardware = "DEL:serial-1") =>
        new() { Name = name, DevicePath = path, HardwareKey = hardware };
    private static HdrDisplayInfo Display(MonitorDeviceIdentity device, bool hdr = false, string gdi = "display-2") =>
        new(device.Name, device.DevicePath, true, hdr, false, true, device, gdi);
    private static MonitorModeDisplayProfile Profile(MonitorDeviceIdentity device, uint value = 12) => new()
    {
        Device = device, GamePresets = [new() { Code = "F0", Value = "0" }],
        ComfortPresets = [new() { Code = "F0", Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) }]
    };

    [Fact]
    public void Identity_NeverMatchesNameOrRecycledPort_AndRejectsDuplicateSerials()
    {
        var saved = Device();
        var television = Device("Dell", "dell-path", "TCL:serial-2"); // Same name and recycled path are insufficient.
        Assert.Null(saved.Resolve([Display(television)]));
        var moved = Device(path: "new-port");
        Assert.Equal("new-port", saved.Resolve([Display(television), Display(moved)])!.DevicePath);
        Assert.Null(saved.Resolve([Display(moved), Display(Device(path: "other-port"))]));
        var pathOnly = Device(hardware: null);
        Assert.Null(pathOnly.Resolve([Display(Device(path: "new-port", hardware: null))]));
        Assert.NotNull(pathOnly.Resolve([Display(Device(hardware: null))]));
    }

    [Fact]
    public void DdcMapping_SelectsBoundSecondScreen_AndRejectsHdrClonesAndUnknownMappings()
    {
        var target = Device();
        var tv = Display(Device("TV", "tv", "TCL:2"), hdr: true, gdi: "display-1");
        var dell = Display(target);
        Assert.Same(dell, DdcMonitorModeController.ResolveDevice(target, [tv, dell]));
        Assert.Null(DdcMonitorModeController.ResolveDevice(target, [dell with { Enabled = true }]));
        Assert.Null(DdcMonitorModeController.ResolveDevice(target, [dell with { GdiDeviceName = "" }]));
        Assert.Null(DdcMonitorModeController.ResolveDevice(target, [dell, tv with { GdiDeviceName = dell.GdiDeviceName }]));
    }

    [Fact]
    public void Edid_UsesProductAndSerial_RejectsCorruptionAndMissingSerial()
    {
        var edid = new byte[128];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(edid, 0);
        new byte[] { 0x10, 0xAC, 0x01, 0x02 }.CopyTo(edid, 8);
        edid[57] = 0xFF;
        System.Text.Encoding.ASCII.GetBytes("TEST-SERIAL\n ").CopyTo(edid, 59);
        Checksum();
        var key = MonitorEdidIdentity.HardwareKey(edid);
        Assert.Equal("10AC0102:TEST-SERIAL", key);
        edid[30]++; Checksum(); // Other EDID data is not the device identity.
        Assert.Equal(key, MonitorEdidIdentity.HardwareKey(edid));
        edid[30]++;
        Assert.Null(MonitorEdidIdentity.HardwareKey(edid));
        Array.Clear(edid, 59, 13); Checksum();
        Assert.Null(MonitorEdidIdentity.HardwareKey(edid));
        edid[12] = 42; Checksum();
        Assert.Equal("10AC0102:0000002A", MonitorEdidIdentity.HardwareKey(edid));
        Assert.Null(MonitorEdidIdentity.HardwareKey(new byte[12]));
        void Checksum() { edid[127] = 0; edid[127] = unchecked((byte)-edid.Sum(value => (int)value)); }
    }

    [Fact]
    public void UnboundLegacyRecipes_NeverCreateOrTouchAnAutomaticController()
    {
        var root = new Mock<IMonitorModeController>(MockBehavior.Strict);
        var config = new Mock<IGameConfiguration>();
        config.Setup(c => c.Read()).Returns(new AppConfig { MonitorMode = new() { Enabled = true } });
        var automation = new GameAutomationService(Mock.Of<IProcessMonitor>(), config.Object, Mock.Of<IHdrController>(),
            Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance, monitorModeController: root.Object);
        try { automation.Start(); automation.ReloadConfig(); }
        finally { automation.Stop(); }
        root.VerifyNoOtherCalls();
    }

    [Fact]
    public void Automation_UsesIndependentRecipes_WhileTelevisionHdrAndReconnectCannotRedirectWrites()
    {
        var dell = Device(); var other = Device("Other", "other", "OTHER:3"); var tv = Device("TV", "tv", "TCL:2");
        IReadOnlyList<HdrDisplayInfo> displays = [Display(tv, true, "display-1"), Display(dell), Display(other, false, "display-3")];
        var hdr = new Mock<IHdrController>();
        hdr.Setup(h => h.TryGetDisplays(out It.Ref<IReadOnlyList<HdrDisplayInfo>>.IsAny))
            .Returns((out IReadOnlyList<HdrDisplayInfo> result) => { result = displays; return true; });
        var root = new Mock<IMonitorModeController>(MockBehavior.Strict);
        var first = new Controller(); var second = new Controller();
        root.Setup(c => c.ForDevice(It.Is<MonitorDeviceIdentity>(d => d.Key() == dell.Key()))).Returns(first);
        root.Setup(c => c.ForDevice(It.Is<MonitorDeviceIdentity>(d => d.Key() == other.Key()))).Returns(second);
        var clock = new Clock();
        var configuration = new Mock<IGameConfiguration>();
        var config = new AppConfig { MonitorMode = new() { Enabled = true, Displays = [Profile(dell), Profile(other, 7)] } };
        configuration.Setup(c => c.Read()).Returns(config);
        var automation = new GameAutomationService(Mock.Of<IProcessMonitor>(), configuration.Object, hdr.Object,
            Mock.Of<IPlayTimeService>(), NullLogger<GameAutomationService>.Instance, monitorModeController: root.Object, timeProvider: clock);
        try
        {
            automation.Start(); Assert.Equal(12u, first.State[0xF0]); Assert.Equal(7u, second.State[0xF0]);
            var touches = first.Touches;
            displays = [Display(Device("TV", "dell-path", "TCL:2")) , Display(other, false, "display-3")];
            clock.Tick(); Assert.Equal(touches, first.Touches); // The replacement screen never receives Dell's recipe.
            first.State.Clear();
            displays = [Display(Device(path: "new-port")), Display(other, false, "display-3")];
            clock.Tick(); Assert.Equal(12u, first.State[0xF0]);
            var alias = Device(path: "new-port", hardware: null);
            root.Setup(c => c.ForDevice(It.Is<MonitorDeviceIdentity>(d => d.Key() == alias.Key()))).Returns(first);
            config.MonitorMode.Displays.Add(Profile(alias, 99));
            touches = first.Touches;
            automation.ReloadConfig();
            Assert.Equal(touches, first.Touches); // Hardware and path aliases cannot compete for the same physical screen.
            automation.Stop(); first.State.Clear(); clock.Tick(); Assert.Empty(first.State);
        }
        finally { automation.Stop(); }
        root.Verify(c => c.ForDevice(It.Is<MonitorDeviceIdentity>(d => d.Key() == tv.Key())), Times.Never);
    }

    [Fact]
    public void SettingsDraft_PreservesOfflineProfiles_AndRequiresExplicitSelection()
    {
        var dell = Device(); var tv = Device("TV", "tv", "TCL:2");
        var snapshot = new AppSettingsSnapshot { DisplaysAvailable = true, Displays = [Display(tv, true)],
            MonitorMode = new() { Enabled = true, Displays = [Profile(dell)] } };
        var draft = new GlobalSettingsViewModel(snapshot);
        Assert.Null(draft.SelectedMonitorDevice);
        Assert.Throws<InvalidOperationException>(() => draft.RequireMonitorDevice());
        Assert.Contains("等待设备", draft.MonitorProfiles[0].Status);
        draft.MonitorProfiles[0].Profile.Enabled = false;
        Assert.True(snapshot.MonitorMode.Displays[0].Enabled); // Editing is detached.
        Assert.Equal(dell.Key(), Assert.Single(draft.ToRequest().MonitorMode!.Displays).Device.Key());
        draft.SelectedMonitorDevice = draft.MonitorDevices[0];
        draft.BindMonitorRecipes([new() { Code = "DC", Value = "1" }], [new() { Code = "DC", Value = "2" }], false);
        Assert.Equal(2, draft.ToRequest().MonitorMode!.Displays.Count);
        Assert.Equal("1", draft.MonitorMode.Displays[1].GamePresets[0].Value);
        Assert.Throws<InvalidOperationException>(() => draft.BindMonitorRecipes([], [], false));
    }

    [Fact]
    public void Yaml_RoundTripsBoundRecipes_AndRejectsMissingOrDuplicateIdentities()
    {
        var path = Path.Combine(Path.GetTempPath(), "gh-device-config-" + Guid.NewGuid().ToString("N"), "config.yml");
        try
        {
            var provider = new YamlConfigProvider(path);
            provider.Change(config => config.MonitorMode = new() { Enabled = true, Displays = [Profile(Device())] });
            var settings = provider.Read().MonitorMode!;
            Assert.Equal(Device().Key(), settings.Displays.Single().Device.Key());
            Assert.Equal("12", settings.Displays[0].ComfortPresets[0].Value);
            settings.Displays.Add(Profile(Device(path: "other-port")));
            Assert.Throws<ArgumentException>(settings.Validate);
            settings.Displays = [Profile(new() { Name = "Dell" })];
            Assert.Throws<ArgumentException>(settings.Validate);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private sealed class Controller : IMonitorModeController
    {
        internal readonly Dictionary<byte, uint> State = [];
        internal int Touches;
        public bool IsAvailable { get { Touches++; return true; } }
        public IReadOnlyDictionary<byte, uint> TryReadState(IReadOnlyList<MonitorModeWrite> codes) { Touches++; return new Dictionary<byte, uint>(State); }
        public void Apply(IReadOnlyList<MonitorModeWrite> writes) { Touches++; foreach (var write in writes) State[write.Code] = write.Value; }
    }
    private sealed class Clock : TimeProvider
    {
        private Action? tick;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { tick = () => callback(state); return Mock.Of<ITimer>(); }
        internal void Tick() => tick!();
    }
}
