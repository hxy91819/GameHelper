namespace GameHelper.Core.Abstractions
{
    public interface IHdrController
    {
        bool IsEnabled { get; }
        void Enable();
        void Disable();
    }
}
