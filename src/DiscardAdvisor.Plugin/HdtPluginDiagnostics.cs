using System.IO;
using Hearthstone_Deck_Tracker;

namespace DiscardAdvisor.Plugin;

internal static class HdtPluginDiagnostics
{
    public static IPluginDiagnostics Create()
    {
        var root = Path.Combine(Config.Instance.DataDir, "DiscardAdvisor");
        return new RedactedDiagnosticStore(
            Path.Combine(root, "Diagnostics"),
            fixtureExporter: new SnapshotFixtureExporter(Path.Combine(root, "Fixtures")));
    }
}

