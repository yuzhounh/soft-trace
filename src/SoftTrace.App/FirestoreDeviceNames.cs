using System.Text.Json;
using System.Text.Json.Nodes;
using SoftTrace.Core;

namespace SoftTrace.App;

public sealed partial class FirestoreSyncClient
{
    public async Task PushDeviceDisplayNamesAsync(
        string projectId, string userId, string idToken,
        IReadOnlyCollection<DeviceDisplayNameChange> names,
        CancellationToken cancellationToken = default)
    {
        if (names.Count == 0) return;
        var writes = new JsonArray();
        foreach (var name in names)
        {
            var documentName = $"projects/{projectId}/databases/(default)/documents/users/{userId}/devices/{name.DeviceId}";
            var write = new JsonObject
            {
                ["update"] = new JsonObject
                {
                    ["name"] = documentName,
                    ["fields"] = new JsonObject
                    {
                        ["deviceId"] = StringValue(name.DeviceId),
                        ["displayName"] = StringValue(name.DisplayName),
                        ["revision"] = StringValue(name.Revision)
                    }
                },
                ["updateTransforms"] = new JsonArray
                {
                    new JsonObject { ["fieldPath"] = "serverUpdatedUtc", ["setToServerValue"] = "REQUEST_TIME" }
                }
            };
            // An older local alias must not replace a name already synced by another device.
            if (name.IsLegacy) write["currentDocument"] = new JsonObject { ["exists"] = false };
            writes.Add(write);
        }
        await SendJsonAsync(
            $"https://firestore.googleapis.com/v1/projects/{Uri.EscapeDataString(projectId)}/databases/(default)/documents:commit",
            idToken, new JsonObject { ["writes"] = writes }, cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceDisplayNameChange>> PullDeviceDisplayNamesAsync(
        string projectId, string userId, string idToken,
        CancellationToken cancellationToken = default)
    {
        var names = new List<DeviceDisplayNameChange>();
        string? lastDocument = null;
        const int pageSize = 250;
        while (true)
        {
            var query = new JsonObject
            {
                ["from"] = new JsonArray { new JsonObject { ["collectionId"] = "devices" } },
                ["orderBy"] = new JsonArray { OrderBy("__name__") },
                ["limit"] = pageSize
            };
            if (lastDocument is not null)
            {
                query["startAt"] = new JsonObject
                {
                    ["values"] = new JsonArray { new JsonObject { ["referenceValue"] = lastDocument } },
                    ["before"] = false
                };
            }
            var response = await SendJsonAsync(
                $"https://firestore.googleapis.com/v1/projects/{Uri.EscapeDataString(projectId)}/databases/(default)/documents/users/{Uri.EscapeDataString(userId)}:runQuery",
                idToken, new JsonObject { ["structuredQuery"] = query }, cancellationToken);
            using var json = JsonDocument.Parse(response);
            var count = 0;
            foreach (var result in json.RootElement.EnumerateArray())
            {
                if (!result.TryGetProperty("document", out var document)) continue;
                lastDocument = document.GetProperty("name").GetString();
                var fields = document.GetProperty("fields");
                names.Add(new(GetString(fields, "deviceId"), GetString(fields, "displayName"), GetString(fields, "revision")));
                count++;
            }
            if (count < pageSize) break;
        }
        return names;
    }
}
