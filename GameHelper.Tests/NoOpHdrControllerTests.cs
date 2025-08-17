using GameHelper.Infrastructure.Controllers;
using Xunit;

namespace GameHelper.Tests
{
    public class NoOpHdrControllerTests
    {
        [Fact]
        public void Enable_Sets_IsEnabled_True()
        {
            var ctrl = new NoOpHdrController();
            Assert.False(ctrl.IsEnabled);
            ctrl.Enable();
            Assert.True(ctrl.IsEnabled);
        }

        [Fact]
        public void Disable_Sets_IsEnabled_False()
        {
            var ctrl = new NoOpHdrController();
            ctrl.Enable();
            Assert.True(ctrl.IsEnabled);
            ctrl.Disable();
            Assert.False(ctrl.IsEnabled);
        }

        [Fact]
        public void Toggle_Enable_Disable_Sequence_Works()
        {
            var ctrl = new NoOpHdrController();
            ctrl.Enable();
            ctrl.Disable();
            ctrl.Enable();
            Assert.True(ctrl.IsEnabled);
        }
    }
}
