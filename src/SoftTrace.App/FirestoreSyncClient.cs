using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoftTrace.Core;

namespace SoftTrace.App;

public sealed record FirestorePullPage(
    IReadOnlyList<SyncActivity> Activities,
    SyncPullCursor? NextCursor,
    bool HasMore);

public sealed class FirestoreSyncClient(HttpClient httpClient)
{
    public async Task PushActivitiesAsync(
        string projectId,
        string userId,
        string idToken,
        IReadOnlyCollection<SyncActivity> activities,
        CancellationToken cancellationToken = default)
    {
        if (activities.Count == 0)
        {
            return;
        }

        var writes = new JsonArray();
        foreach (var activity in activities)
        {
            var fields = new JsonObject
            {
                ["syncId"] = StringValue(activity.SyncId),
                ["deviceId"] = StringValue(activity.DeviceId),
                ["deviceName"] = StringValue(activity.DeviceName),
                ["processName"] = StringValue(activity.ProcessName),
                ["appName"] = StringValue(activity.AppName),
                ["executablePath"] = activity.ExecutablePath is null
                    ? new JsonObject { ["nullValue"] = "NULL_VALUE" }
                    : StringValue(activity.ExecutablePath),
                ["startUtc"] = TimestampValue(activity.StartUtc),
                ["endUtc"] = TimestampValue(activity.EndUtc),
                ["updatedUtc"] = TimestampValue(activity.UpdatedUtc),
                ["isOpen"] = BooleanValue(activity.IsOpen),
                ["source"] = StringValue(activity.Source)
            };
            var documentName =
                $"projects/{projectId}/databases/(default)/documents/users/{userId}/activities/{activity.SyncId}";
            writes.Add(new JsonObject
            {
                ["update"] = new JsonObject
                {
                    ["name"] = documentName,
                    ["fields"] = fields
                },
                ["updateTransforms"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["fieldPath"] = "serverUpdatedUtc",
                        ["setToServerValue"] = "REQUEST_TIME"
                    }
                }
            });
        }

        var endpoint =
            $"https://firestore.googleapis.com/v1/projects/{Uri.EscapeDataString(projectId)}/databases/(default)/documents:commit";
        await SendJsonAsync(
            endpoint,
            idToken,
            new JsonObject { ["writes"] = writes },
            cancellationToken);
    }

    public async Task<FirestorePullPage> PullActivitiesAsync(
        string projectId,
        string userId,
        string idToken,
        SyncPullCursor? cursor,
        int pageSize = 250,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        var structuredQuery = new JsonObject
        {
            ["from"] = new JsonArray
            {
                new JsonObject { ["collectionId"] = "activities" }
            },
            ["orderBy"] = new JsonArray
            {
                OrderBy("serverUpdatedUtc"),
                OrderBy("__name__")
            },
            ["limit"] = pageSize
        };
        if (cursor is not null)
        {
            structuredQuery["startAt"] = new JsonObject
            {
                ["values"] = new JsonArray
                {
                    TimestampValue(cursor.ServerUpdatedUtc),
                    new JsonObject { ["referenceValue"] = cursor.DocumentName }
                },
                ["before"] = false
            };
        }

        var endpoint =
            $"https://firestore.googleapis.com/v1/projects/{Uri.EscapeDataString(projectId)}/databases/(default)/documents/users/{Uri.EscapeDataString(userId)}:runQuery";
        var responseBody = await SendJsonAsync(
            endpoint,
            idToken,
            new JsonObject { ["structuredQuery"] = structuredQuery },
            cancellationToken);

        using var document = JsonDocument.Parse(responseBody);
        var activities = new List<SyncActivity>();
        SyncPullCursor? nextCursor = null;
        foreach (var result in document.RootElement.EnumerateArray())
        {
            if (!result.TryGetProperty("document", out var firestoreDocument) ||
                !firestoreDocument.TryGetProperty("fields", out var fields))
            {
                continue;
            }

            var documentName = firestoreDocument.GetProperty("name").GetString()!;
            var serverUpdatedUtc = GetTimestamp(fields, "serverUpdatedUtc");
            activities.Add(new SyncActivity(
                GetString(fields, "syncId"),
                GetString(fields, "deviceId"),
                GetString(fields, "deviceName"),
                GetString(fields, "processName"),
                GetString(fields, "appName"),
                GetNullableString(fields, "executablePath"),
                GetTimestamp(fields, "startUtc"),
                GetTimestamp(fields, "endUtc"),
                GetTimestamp(fields, "updatedUtc"),
                GetBoolean(fields, "isOpen"),
                GetString(fields, "source"),
                serverUpdatedUtc));
            nextCursor = new SyncPullCursor(serverUpdatedUtc, documentName);
        }

        return new FirestorePullPage(activities, nextCursor, activities.Count == pageSize);
    }

    private async Task<string> SendJsonAsync(
        string endpoint,
        string idToken,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetFirestoreError(responseBody));
        }
        return responseBody;
    }

    private static JsonObject OrderBy(string fieldPath) => new()
    {
        ["field"] = new JsonObject { ["fieldPath"] = fieldPath },
        ["direction"] = "ASCENDING"
    };

    private static JsonObject StringValue(string value) => new() { ["stringValue"] = value };

    private static JsonObject BooleanValue(bool value) => new() { ["booleanValue"] = value };

    private static JsonObject TimestampValue(DateTimeOffset value) => new()
    {
        ["timestampValue"] = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    };

    private static string GetString(JsonElement fields, string fieldName) =>
        fields.GetProperty(fieldName).GetProperty("stringValue").GetString() ?? string.Empty;

    private static string? GetNullableString(JsonElement fields, string fieldName)
    {
        if (!fields.TryGetProperty(fieldName, out var field) || field.TryGetProperty("nullValue", out _))
        {
            return null;
        }
        return field.TryGetProperty("stringValue", out var value) ? value.GetString() : null;
    }

    private static bool GetBoolean(JsonElement fields, string fieldName) =>
        fields.GetProperty(fieldName).GetProperty("booleanValue").GetBoolean();

    private static DateTimeOffset GetTimestamp(JsonElement fields, string fieldName) =>
        DateTimeOffset.Parse(
            fields.GetProperty(fieldName).GetProperty("timestampValue").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static string GetFirestoreError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString()
                   ?? "Firestore 同步失败";
        }
        catch
        {
            return "Firestore 同步失败";
        }
    }
}
