using System;

namespace DiscardAdvisor.Domain;

public sealed class RuntimeCompatibility
{
    public RuntimeCompatibility(
        int hearthstoneBuild,
        string hdtVersion,
        string cardDefsSha256,
        string hearthDbSha256)
    {
        HearthstoneBuild = hearthstoneBuild;
        HdtVersion = hdtVersion ?? throw new ArgumentNullException(nameof(hdtVersion));
        CardDefsSha256 = cardDefsSha256 ?? throw new ArgumentNullException(nameof(cardDefsSha256));
        HearthDbSha256 = hearthDbSha256 ?? throw new ArgumentNullException(nameof(hearthDbSha256));
    }

    public int HearthstoneBuild { get; }

    public string HdtVersion { get; }

    public string CardDefsSha256 { get; }

    public string HearthDbSha256 { get; }
}

public static class TargetRuntimeCompatibility
{
    public const int HearthstoneBuild = 247416;
    public const string HdtVersion = "1.53.11";
    public const string CardDefsSha256 = "a3b0e3dcd112626aa47ba16ede1b26506eed175b1fda288c1b6952065c06aac4";
    public const string HearthDbSha256 = "e465ad55f9b460750246abc198da7b15650164429893681714bfe72797e638ca";
}
