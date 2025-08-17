namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Optional interface to expose the underlying config file path used by a provider.
    /// </summary>
    public interface IConfigPathProvider
    {
        string ConfigPath { get; }
    }
}
