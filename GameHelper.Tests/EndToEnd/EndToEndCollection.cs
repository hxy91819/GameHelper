using Xunit;

namespace GameHelper.Tests.EndToEnd
{
    [CollectionDefinition("EndToEndSequential", DisableParallelization = true)]
    public sealed class EndToEndSequentialCollection : ICollectionFixture<object>
    {
    }
}