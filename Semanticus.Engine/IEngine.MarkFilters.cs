using System.Threading.Tasks;

namespace Semanticus.Engine;

public partial interface IEngine
{
    Task<bool> SetMeasureFiltersOnPurposeAsync(string measureRef, bool onPurpose, string origin = "human");
}
