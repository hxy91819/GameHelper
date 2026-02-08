using System;
using GameHelper.ConsoleHost.Interactive;
using Xunit;

namespace GameHelper.Tests.Interactive
{
    public sealed class InteractiveScriptTests
    {
        private enum SampleAction
        {
            First,
            Second,
            Third
        }

        private enum SparseAction
        {
            Alpha = 10,
            Beta = 20
        }

        [Fact]
        public void TryDequeue_Enum_ByName()
        {
            var script = new InteractiveScript().Enqueue("Second");
            Assert.True(script.TryDequeue(out SampleAction action));
            Assert.Equal(SampleAction.Second, action);
        }

        [Fact]
        public void TryDequeue_Enum_ByNumericString()
        {
            var script = new InteractiveScript().Enqueue("1");
            Assert.True(script.TryDequeue(out SampleAction action));
            Assert.Equal(SampleAction.Second, action);
        }

        [Fact]
        public void TryDequeue_Enum_NumericString_UsesIndexForSparseEnums()
        {
            var script = new InteractiveScript().Enqueue("1");
            Assert.True(script.TryDequeue(out SparseAction action));
            Assert.Equal(SparseAction.Beta, action);
        }

        [Fact]
        public void TryDequeue_Bool_FromNumeric()
        {
            var script = new InteractiveScript()
                .Enqueue(1)
                .Enqueue(0);

            Assert.True(script.TryDequeue(out bool first));
            Assert.True(first);
            Assert.True(script.TryDequeue(out bool second));
            Assert.False(second);
        }

        [Fact]
        public void TryDequeue_String_FromNonString()
        {
            var script = new InteractiveScript().Enqueue(42);
            Assert.True(script.TryDequeue(out string value));
            Assert.Equal("42", value);
        }

        [Fact]
        public void TryDequeue_InvalidEnum_Throws()
        {
            var script = new InteractiveScript().Enqueue("NotAnEnum");
            var ex = Assert.Throws<InvalidOperationException>(() => script.TryDequeue(out SampleAction _));
            Assert.Contains("cannot be converted", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryDequeue_Enum_NumericString_NotDefined_Throws()
        {
            var script = new InteractiveScript().Enqueue("999");
            var ex = Assert.Throws<InvalidOperationException>(() => script.TryDequeue(out SampleAction _));
            Assert.Contains("cannot be converted", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
