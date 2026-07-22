using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using DiscardAdvisor.Domain.Snapshots;

namespace DiscardAdvisor.Plugin;

public static class SnapshotJsonSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.Indented
    };

    public static string Serialize(GameSnapshot snapshot) => JsonConvert.SerializeObject(snapshot, Settings);
}

