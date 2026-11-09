namespace Shoreline.Api.Security;

// A slow hash must not let concurrent login attempts allocate unbounded scrypt workspaces.
public sealed class PasswordService : IDisposable
{
    private readonly SemaphoreSlim slots = new(2, 2);

    public Task<bool> VerifyAsync(string encoded, string password, CancellationToken ct) =>
        RunAsync(() => PasswordHash.Verify(encoded, password), ct);

    public Task<string> CreateAsync(string password, CancellationToken ct) =>
        RunAsync(() => PasswordHash.Create(password), ct);

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken ct)
    {
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(1), ct))
            throw new Infrastructure.ApiError("TooManyRequests", 1, "请稍后重试", 429);
        try { return await Task.Run(action, ct); }
        finally { slots.Release(); }
    }

    public void Dispose() => slots.Dispose();
}
