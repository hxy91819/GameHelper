using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface ISettingsService
{
    AppSettingsSnapshot Get();

    AppSettingsSnapshot Update(UpdateAppSettingsRequest request);
}
