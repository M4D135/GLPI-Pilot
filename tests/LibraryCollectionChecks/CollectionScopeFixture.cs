using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    public void LoadCollectionScopeFixture(ExtractionResult data) => SetResult(data);
}
