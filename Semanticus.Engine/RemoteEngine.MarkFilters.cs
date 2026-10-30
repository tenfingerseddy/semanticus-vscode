using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class RemoteEngine
{
    public Task<bool> SetMeasureFiltersOnPurposeAsync(string measureRef, bool onPurpose, string origin = "human")
        => _rpc.InvokeAsync<bool>("setMeasureFiltersOnPurpose", measureRef, onPurpose, origin);
}
