using System.Text.Json;
using CarpaNet.OAuth.Storage;
using Koan.Web.Auth.Connector.Atproto.Infrastructure;
using Koan.Web.Auth.Connector.Atproto.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Koan.Web.Auth.Connector.Atproto.Storage;

internal sealed class ProtectedAtprotoStore(IDataProtectionProvider protection, IOptions<AtprotoOptions> options, IHostEnvironment environment)
    : IOAuthStateStore, IOAuthSessionStore, IDisposable
{
    private readonly object gate = new();
    private readonly IDataProtector protector = protection.CreateProtector(Constants.ProtectionPurpose);
    private StoreData? data;
    private FileStream? ownership;
    private string file = "";

    private StoreData Data
    {
        get
        {
            if (data is not null) return data;
            var directory = Path.GetFullPath(options.Value.SessionDirectory, environment.ContentRootPath);
            Directory.CreateDirectory(directory);
            ownership = new FileStream(Path.Combine(directory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            file = Path.Combine(directory, "protocol.protected");
            data = File.Exists(file) ? JsonSerializer.Deserialize<StoreData>(protector.Unprotect(File.ReadAllText(file)))! : new();
            return data;
        }
    }

    private void Save()
    {
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(Data)));
        File.Move(temporary, file, overwrite: true);
    }

    public Task StoreAsync(string state, OAuthStateData value, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            foreach (var expired in Data.States.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) Data.States.Remove(expired);
            Data.States[state] = value; Save();
        }
        return Task.CompletedTask;
    }

    internal OAuthStateData? Peek(string state)
    {
        lock (gate) return Data.States.GetValueOrDefault(state) is { } value && value.ExpiresAt > DateTimeOffset.UtcNow ? value : null;
    }

    public Task<OAuthStateData?> ConsumeAsync(string state, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var value = Data.States.GetValueOrDefault(state);
            Data.States.Remove(state); Save();
            return Task.FromResult(value is not null && value.ExpiresAt > DateTimeOffset.UtcNow ? value : null);
        }
    }

    public Task StoreAsync(string sub, OAuthSessionData value, CancellationToken cancellationToken = default)
    {
        if (value.TokenSet.Sub != sub) throw new InvalidOperationException("AT token subject changed.");
        lock (gate) { Data.Sessions[sub] = value; Save(); }
        return Task.CompletedTask;
    }

    public Task<OAuthSessionData?> GetAsync(string sub, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult(Data.Sessions.GetValueOrDefault(sub));
    }

    public Task DeleteAsync(string sub, CancellationToken cancellationToken = default)
    {
        lock (gate) { Data.Sessions.Remove(sub); Save(); }
        return Task.CompletedTask;
    }

    public void Dispose() { lock (gate) ownership?.Dispose(); }

    private sealed class StoreData
    {
        public Dictionary<string, OAuthStateData> States { get; set; } = new();
        public Dictionary<string, OAuthSessionData> Sessions { get; set; } = new();
    }
}
