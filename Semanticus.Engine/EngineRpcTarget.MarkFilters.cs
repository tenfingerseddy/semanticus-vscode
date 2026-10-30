using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class EngineRpcTarget
{
    public Task<bool> setMeasureFiltersOnPurpose(string measureRef, bool onPurpose, string origin = "human")
        => _engine.SetMeasureFiltersOnPurposeAsync(measureRef, onPurpose, _origin);
}
