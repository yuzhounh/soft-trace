using System.IO;
using System.Net;
using System.Net.Http;
using SoftTrace.App;
using SoftTrace.Core;

namespace SoftTrace.App.Tests;

public sealed class FirebaseCredentialRecoveryTests
{
    [Theory]
    [InlineData("not-base64")]
    [InlineData("AQID")]
    public async Task UnreadableCredentialKeepsAccountSettingsAndRecordsAndRequestsReauthorization(string token)
    {
        var stem = Path.Combine(AppContext.BaseDirectory, $"credential-recovery-{Guid.NewGuid():N}");
        var settingsStore = new FirebaseSyncSettingsStore(stem + ".json");
        var original = new FirebaseSyncSettings(true, "original@example.com", "Original", null, "original-user", token);
        settingsStore.Save(original);
        var originalFile = await File.ReadAllTextAsync(settingsStore.SettingsPath);
        try
        {
            var store = new ActivityStore(stem + ".db");
            await store.InitializeAsync();
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            await store.AddCompletedSegmentAsync("pc", new(new("app", "Original app", null), start, start.AddMinutes(5)));
            using var handler = new NoNetworkHandler();
            using var http = new HttpClient(handler);
            await using var sync = new FirebaseSyncCoordinator(store, settingsStore,
                new FirebaseAuthClient(http), new FirestoreSyncClient(http));
            await sync.SyncNowAsync();
            Assert.True(sync.CurrentStatus.IsSignedIn);
            Assert.True(sync.CurrentStatus.HasError);
            Assert.True(sync.CurrentStatus.RequiresReauthentication);
            Assert.Equal(original.Email, sync.CurrentStatus.Email);
            Assert.Equal(original.DisplayName, sync.CurrentStatus.DisplayName);
            Assert.Equal(original.UserId, sync.CurrentUserId);
            Assert.Contains("重新授权", sync.CurrentStatus.Message);
            Assert.Equal(originalFile, await File.ReadAllTextAsync(settingsStore.SettingsPath));
            Assert.Equal(300, (await store.GetDailyTimelineAsync("pc", start, start.AddDays(1))).ActiveSeconds);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            foreach (var suffix in new[] { ".json", ".db", ".db-wal", ".db-shm" }) File.Delete(stem + suffix);
        }
    }

    [Fact]
    public void NewCredentialsRemainEncryptedForCurrentWindowsUser()
    {
        const string token = "synthetic-refresh-token";
        var encrypted = FirebaseSyncSettingsStore.ProtectRefreshToken(token);
        Assert.DoesNotContain(token, encrypted);
        Assert.Equal(token, FirebaseSyncSettingsStore.UnprotectRefreshToken(encrypted));
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
