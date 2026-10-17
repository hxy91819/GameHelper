using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHelper.Tests.Services;

public class MonitorModeSchedulerTests
{
    private sealed class RecordingController : IMonitorModeController
    {
        public bool Available { get; set; } = true;

        public IReadOnlyDictionary<byte, uint>? State { get; set; }

        public List<MonitorMode> AppliedModes { get; } = new();

        public List<IReadOnlyList<MonitorModeWrite>> AppliedRecipes { get; } = new();

        public int ReadCount { get; private set; }

        public bool IsAvailable => Available;

        public IReadOnlyDictionary<byte, uint>? TryReadState(IReadOnlyList<MonitorModeWrite> probeCodes)
        {
            ReadCount++;
            return State;
        }

        public void Apply(IReadOnlyList<MonitorModeWrite> writes)
        {
            AppliedRecipes.Add(writes);
            AppliedModes.Add(Classify(writes));

            // Mirrors a real controller: after applying, the state readback reflects the preset.
            State = writes.ToDictionary(write => write.Code, write => write.Value);
        }

        private static MonitorMode Classify(IReadOnlyList<MonitorModeWrite> writes)
        {
            var e2 = writes.FirstOrDefault(write => write.Code == 0xE2);
            return e2.Value == 0x04 ? MonitorMode.Game : MonitorMode.Comfort;
        }
    }

    private static MonitorModePlan CreatePlan(string start = "00:00", string end = "23:59") =>
        new(new MonitorModeSettings
        {
            Enabled = true,
            DaylightStart = start,
            DaylightEnd = end,
            GamePresets = new List<MonitorModePresetData>
            {
                new() { Code = "DC", Value = "0x05" },
                new() { Code = "E2", Value = "0x04" },
                new() { Code = "F0", Value = "0x00" }
            },
            ComfortPresets = new List<MonitorModePresetData>
            {
                new() { Code = "DC", Value = "0x00" },
                new() { Code = "E2", Value = "0x1D" },
                new() { Code = "F0", Value = "0x0C" }
            }
        });

    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Update_DaylightWithoutGame_AppliesComfort()
    {
        var controller = new RecordingController { State = null };
        var scheduler = new MonitorModeScheduler();
        var plan = CreatePlan();

        scheduler.Update(plan, anyGameActive: false, controller, NullLogger.Instance, Noon);

        Assert.Equal(MonitorMode.Comfort, Assert.Single(controller.AppliedModes));
        Assert.Equal(3, Assert.Single(controller.AppliedRecipes).Count);
    }

    [Fact]
    public void Update_DaylightWithGame_AppliesGame()
    {
        var controller = new RecordingController { State = new Dictionary<byte, uint> { [0xDC] = 0, [0xE2] = 0x1D, [0xF0] = 0x0C } };
        var scheduler = new MonitorModeScheduler();
        var plan = CreatePlan();

        scheduler.Update(plan, anyGameActive: true, controller, NullLogger.Instance, Noon);

        Assert.Equal(MonitorMode.Game, Assert.Single(controller.AppliedModes));
    }

    [Fact]
    public void Update_AfterSunset_EvenWithGame_AppliesComfort()
    {
        var controller = new RecordingController { State = new Dictionary<byte, uint> { [0xDC] = 5, [0xE2] = 4, [0xF0] = 0 } };
        var scheduler = new MonitorModeScheduler();
        // Fixed window 00:00-00:00 collapses to "never daylight".
        var plan = CreatePlan("00:00", "00:00");

        scheduler.Update(plan, anyGameActive: true, controller, NullLogger.Instance, Noon);

        Assert.Equal(MonitorMode.Comfort, Assert.Single(controller.AppliedModes));
    }

    [Fact]
    public void Update_AlreadyAtTarget_SkipsWrites()
    {
        var controller = new RecordingController
        {
            State = new Dictionary<byte, uint> { [0xDC] = 0, [0xE2] = 0x1D, [0xF0] = 0x0C }
        };
        var scheduler = new MonitorModeScheduler();
        var plan = CreatePlan();

        scheduler.Update(plan, anyGameActive: false, controller, NullLogger.Instance, Noon);

        Assert.Empty(controller.AppliedModes);
        Assert.Equal(1, controller.ReadCount);
    }

    [Fact]
    public void Update_ControllerUnavailable_DoesNothing()
    {
        var controller = new RecordingController { Available = false, State = null };
        var scheduler = new MonitorModeScheduler();
        var plan = CreatePlan();

        scheduler.Update(plan, anyGameActive: true, controller, NullLogger.Instance, Noon);

        Assert.Empty(controller.AppliedModes);
        Assert.Equal(0, controller.ReadCount);
    }

    [Fact]
    public void Update_UnreadableState_StillAppliesDesired()
    {
        var controller = new RecordingController { State = null };
        var scheduler = new MonitorModeScheduler();
        var plan = CreatePlan();

        scheduler.Update(plan, anyGameActive: true, controller, NullLogger.Instance, Noon);

        Assert.Equal(MonitorMode.Game, Assert.Single(controller.AppliedModes));
    }

    [Fact]
    public void TryCreate_DisabledOrNull_ReturnsNull()
    {
        Assert.Null(MonitorModePlan.TryCreate(null, NullLogger.Instance));
        Assert.Null(MonitorModePlan.TryCreate(new MonitorModeSettings { Enabled = false }, NullLogger.Instance));
    }

    [Fact]
    public void TryCreate_InvalidPreset_ReturnsNullAndLogs()
    {
        var settings = new MonitorModeSettings
        {
            Enabled = true,
            GamePresets = new List<MonitorModePresetData> { new() { Code = "ZZ", Value = "0x05" } },
            ComfortPresets = new List<MonitorModePresetData> { new() { Code = "DC", Value = "0x00" } }
        };

        Assert.Null(MonitorModePlan.TryCreate(settings, NullLogger.Instance));
    }

    [Theory]
    [InlineData("DC", "0x05", true)]
    [InlineData("0xDC", "29", true)]
    [InlineData("E2", "0x1D", true)]
    [InlineData("", "0x05", false)]
    [InlineData("DC", "", false)]
    [InlineData("GG", "0x05", false)]
    [InlineData("100", "0x05", false)]
    [InlineData("DC", "0x100", false)]
    public void MonitorModeWrite_Parse_AcceptsFlexibleNotation(string code, string value, bool expected)
    {
        var parsed = MonitorModeWrite.TryParse(code, value, out var write);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.InRange((int)write.Code, 0, 0xFF);
            Assert.InRange((int)write.Value, 0, 0xFF);
        }
    }
}