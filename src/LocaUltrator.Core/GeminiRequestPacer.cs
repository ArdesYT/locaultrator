using System.Net;

namespace LocaUltrator.Core;

public sealed class GeminiLocalQuotaException(string message) : InvalidOperationException(message);

public sealed class GeminiRequestPacer
{
    private sealed class QuotaState
    {
        public DateTimeOffset NextAllowedUtc { get; set; }
        public Queue<TokenReservation> TokenReservations { get; } = new();
        public DateOnly PacificDay { get; set; }
        public int DailyRequests { get; set; }
    }

    private readonly record struct TokenReservation(DateTimeOffset Utc, int Tokens);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, QuotaState> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _minimumGlobalInterval;
    private DateTimeOffset _globalNextAllowedUtc;
    private static readonly TimeZoneInfo PacificTime = FindPacificTime();

    public GeminiRequestPacer(TimeSpan? minimumGlobalInterval = null) =>
        _minimumGlobalInterval = minimumGlobalInterval ?? TimeSpan.FromMilliseconds(750);

    public Task WaitAsync(GeminiApiKeyEntry key, Action<TimeSpan, string>? waiting = null,
        CancellationToken cancellationToken = default) =>
        WaitAsync(key, GeminiModelRouting.Flash37, 1, waiting, cancellationToken);

    public Task WaitAsync(GeminiApiKeyEntry key, string model, Action<TimeSpan, string>? waiting = null,
        CancellationToken cancellationToken = default) =>
        WaitAsync(key, model, 1, waiting, cancellationToken);

    public async Task WaitAsync(GeminiApiKeyEntry key, string model, int estimatedInputTokens,
        Action<TimeSpan, string>? waiting = null, CancellationToken cancellationToken = default)
    {
        string groupName = GroupName(key, model);
        int rpm = Math.Clamp(GeminiModelRouting.RequestsPerMinute(key, model), 1, 10_000);
        int tpm = Math.Clamp(GeminiModelRouting.TokensPerMinute(key, model), 1, 2_000_000_000);
        int rpd = Math.Clamp(GeminiModelRouting.RequestsPerDay(key, model), 1, 10_000_000);
        int reservation = Math.Max(1, estimatedInputTokens);
        if (reservation > tpm)
            throw new GeminiLocalQuotaException(
                $"A becsült bemenet ({reservation:N0} token) nagyobb a(z) {model} beállított TPM-limitjénél ({tpm:N0}). Csökkentsd a kötegméretet vagy írd át az AI Studio szerinti TPM-et.");
        TimeSpan groupInterval = TimeSpan.FromMinutes(1d / rpm);

        while (true)
        {
            await _gate.WaitAsync(cancellationToken);
            TimeSpan delay;
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                QuotaState state = GetState(groupName);
                DateOnly today = CurrentPacificDay(now);
                if (state.PacificDay != today)
                {
                    state.PacificDay = today;
                    state.DailyRequests = 0;
                }
                if (state.DailyRequests >= rpd)
                    throw new GeminiLocalQuotaException(
                        $"A(z) {model} beállított napi RPD-limitje ({rpd:N0}) ebben a programfutásban elfogyott. A program másik modellre vált; a Google RPD csendes-óceáni idő szerint éjfélkor nullázódik.");

                DateTimeOffset tokenWindowStart = now - TimeSpan.FromMinutes(1);
                while (state.TokenReservations.TryPeek(out TokenReservation old) && old.Utc <= tokenWindowStart)
                    state.TokenReservations.Dequeue();
                long reservedTokens = state.TokenReservations.Sum(item => (long)item.Tokens);
                DateTimeOffset tokenAvailable = now;
                if (reservedTokens + reservation > tpm && state.TokenReservations.TryPeek(out TokenReservation first))
                    tokenAvailable = first.Utc + TimeSpan.FromMinutes(1);

                DateTimeOffset available = state.NextAllowedUtc > _globalNextAllowedUtc
                    ? state.NextAllowedUtc : _globalNextAllowedUtc;
                if (tokenAvailable > available) available = tokenAvailable;
                delay = available > now ? available - now : TimeSpan.Zero;
                if (delay <= TimeSpan.FromMilliseconds(15))
                {
                    state.NextAllowedUtc = now + groupInterval;
                    _globalNextAllowedUtc = now + _minimumGlobalInterval;
                    state.TokenReservations.Enqueue(new TokenReservation(now, reservation));
                    state.DailyRequests++;
                    return;
                }
            }
            finally { _gate.Release(); }

            waiting?.Invoke(delay, groupName);
            await Task.Delay(delay, cancellationToken);
        }
    }

    public void ReportResponse(GeminiApiKeyEntry key, HttpStatusCode statusCode, TimeSpan? retryAfter)
        => ReportResponse(key, GeminiModelRouting.Flash37, statusCode, retryAfter);

    public void ReportResponse(GeminiApiKeyEntry key, string model, HttpStatusCode statusCode, TimeSpan? retryAfter)
    {
        if (statusCode != HttpStatusCode.TooManyRequests) return;
        TimeSpan cooldown = retryAfter is { } specified && specified > TimeSpan.Zero
            ? specified : TimeSpan.FromSeconds(60);
        if (cooldown > TimeSpan.FromMinutes(10)) cooldown = TimeSpan.FromMinutes(10);
        _gate.Wait();
        try
        {
            QuotaState state = GetState(GroupName(key, model));
            DateTimeOffset until = DateTimeOffset.UtcNow + cooldown;
            if (until > state.NextAllowedUtc) state.NextAllowedUtc = until;
        }
        finally { _gate.Release(); }
    }

    private QuotaState GetState(string groupName)
    {
        if (!_groups.TryGetValue(groupName, out QuotaState? state))
        {
            state = new QuotaState { PacificDay = CurrentPacificDay(DateTimeOffset.UtcNow) };
            _groups[groupName] = state;
        }
        return state;
    }

    private static string GroupName(GeminiApiKeyEntry key, string model)
    {
        string project = string.IsNullOrWhiteSpace(key.QuotaGroup) ? "Alapértelmezett projekt" : key.QuotaGroup.Trim();
        return project + " · " + model;
    }

    private static DateOnly CurrentPacificDay(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, PacificTime).DateTime);

    private static TimeZoneInfo FindPacificTime()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch { return TimeZoneInfo.Utc; }
    }
}
