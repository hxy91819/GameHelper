using GameHelper.Core.Abstractions;

namespace GameHelper.Infrastructure.Controllers
{
    public class NoOpHdrController : IHdrController
    {
        public bool IsEnabled { get; private set; }

        public void Enable()
        {
            IsEnabled = true;
        }

        public void Disable()
        {
            IsEnabled = false;
        }
    }
}
