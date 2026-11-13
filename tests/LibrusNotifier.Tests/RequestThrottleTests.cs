using System.Net;
using LibrusNotifier.Librus;
using LibrusNotifier.Reporting;
using LibrusNotifier.Scheduling;

namespace LibrusNotifier.Tests;

public sealed class RequestThrottleTests
{
    private sealed class Setup : IDisposable
    {
        public Setup(double random, DateTime? start = null)
        {
            Clock = new FakeClock(start ?? new DateTime(2026, 11, 12, 10, 0, 0));
            Guard = new DayGuard(Clock, Clock.Today, CancellationToken.None);
            Throttle = new RequestThrottle(Clock, new FixedRandom(random), delay: (delay, _) =>
            {
                Delays.Add(delay);
                Clock.Advance(delay);
                return Task.CompletedTask;
            });
            Inner = new MockHandler(r =>
            {
                RequestTimes.Add(Clock.UtcNow);
                return r.Uri.AbsolutePath == "/przekierowanie" ? MockHandler.Redirect("/cel") : MockHandler.Html("ok");
            });
            var stats = new DailyStats(Clock.Today, Clock.Now);
            ParentHandler = Handler(AccountKind.Parent, stats);
            Parent = new HttpClient(ParentHandler, disposeHandler: false);
            Student = new HttpClient(Handler(AccountKind.Student, stats), disposeHandler: false);
        }

        public LibrusHttpHandler ParentHandler { get; }

        public FakeClock Clock { get; }
        public DayGuard Guard { get; }
        public RequestThrottle Throttle { get; }
        public MockHandler Inner { get; }
        public ListLog Log { get; } = new();
        public List<TimeSpan> Delays { get; } = [];
        public List<DateTimeOffset> RequestTimes { get; } = [];
        public HttpClient Parent { get; }
        public HttpClient Student { get; }

        private LibrusHttpHandler Handler(AccountKind account, DailyStats stats) =>
            new(Inner, new CookieContainer(), account, Log, stats, Guard, new SessionActivity(Clock, TimeSpan.FromMinutes(9)), Throttle, TestBrowser.Profile);

        public async Task GetAsync(HttpClient client, string path = "/x") => (await client.GetAsync("https://school.test" + path, Guard.Token)).Dispose();

        public void Dispose()
        {
            Parent.Dispose();
            Student.Dispose();
            Guard.Dispose();
        }
    }

    [Fact]
    public void DefaultGapIsFiveToElevenSeconds()
    {
        var throttle = new RequestThrottle(new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0)));
        Assert.Equal(TimeSpan.FromSeconds(5), throttle.MinGap);
        Assert.Equal(TimeSpan.FromSeconds(11), throttle.MaxGap);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RequestThrottle(new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0)), minGap: TimeSpan.FromSeconds(5), maxGap: TimeSpan.FromSeconds(4)));
    }

    [Theory]
    [InlineData(0.0, 5.0)]
    [InlineData(0.5, 8.0)]
    [InlineData(0.999999, 11.0)]
    public async Task GapIsRandomAndSharedByBothAccounts(double random, double gapSeconds)
    {
        using var s = new Setup(random);

        await s.GetAsync(s.Parent);
        s.Clock.Advance(TimeSpan.FromSeconds(1));
        await s.GetAsync(s.Student);

        Assert.Equal(gapSeconds - 1, Assert.Single(s.Delays).TotalSeconds, precision: 3);
        Assert.Equal(gapSeconds, (s.RequestTimes[1] - s.RequestTimes[0]).TotalSeconds, precision: 3);
    }

    [Fact]
    public async Task NoWaitWhenNaturalBreakIsLongerThanGap()
    {
        using var s = new Setup(0.999999);

        await s.GetAsync(s.Parent);
        s.Clock.Advance(TimeSpan.FromSeconds(12));
        await s.GetAsync(s.Parent);

        Assert.Empty(s.Delays);
        Assert.DoesNotContain(s.Log.Messages(), m => m.Contains("czekano"));
    }

    [Fact]
    public async Task RedirectHopsAreSpacedAndWaitIsLogged()
    {
        using var s = new Setup(0.5);

        await s.GetAsync(s.Parent, "/przekierowanie");

        Assert.Equal(2, s.RequestTimes.Count);
        Assert.Equal(TimeSpan.FromSeconds(8), s.RequestTimes[1] - s.RequestTimes[0]);
        Assert.Contains(s.Log.Messages(), m => m.StartsWith("HTTP rodzic GET school.test/cel -> 200 ") && m.EndsWith("(odstęp: czekano 8.0 s)"));
    }

    [Fact]
    public void DefaultLoginGapIsZeroToOneSecond()
    {
        var throttle = new RequestThrottle(new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0)));
        Assert.Equal(TimeSpan.Zero, throttle.LoginMinGap);
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.LoginMaxGap);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RequestThrottle(new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0)), loginMinGap: TimeSpan.FromSeconds(2), loginMaxGap: TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.999999, 1.0)]
    public async Task LoginStepWaitsShortRandomGapAndNextRegularRequestWaitsRegularGap(double random, double loginGapSeconds)
    {
        using var s = new Setup(random);
        var regular = 5 + 6 * random;

        await s.GetAsync(s.Parent);
        s.ParentHandler.BeginLogin();
        await s.GetAsync(s.Parent);
        await s.GetAsync(s.Parent);
        s.ParentHandler.EndLogin();
        await s.GetAsync(s.Parent);

        // Pierwsze żądanie logowania czeka zwykły odstęp, kolejny krok krótki (zerowy odstęp nie wywołuje oczekiwania),
        // a po zakończeniu logowania znów zwykły.
        double[] expected = loginGapSeconds > 0 ? [regular, loginGapSeconds, regular] : [regular, regular];
        Assert.Equal(expected, s.Delays.Select(d => d.TotalSeconds).ToArray(), new SecondsComparer());
        Assert.Equal(4, s.RequestTimes.Count);
    }

    [Fact]
    public async Task RedirectHopsOfLoginUseShortGap()
    {
        using var s = new Setup(0.5);

        await s.GetAsync(s.Parent);
        s.ParentHandler.BeginLogin();
        await s.GetAsync(s.Parent, "/przekierowanie");

        Assert.Equal(3, s.RequestTimes.Count);
        Assert.Equal(TimeSpan.FromSeconds(8), s.RequestTimes[1] - s.RequestTimes[0]);
        Assert.Equal(TimeSpan.FromSeconds(0.5), s.RequestTimes[2] - s.RequestTimes[1]);
        Assert.Contains(s.Log.Messages(), m => m.StartsWith("HTTP rodzic GET school.test/cel -> 200 ") && m.EndsWith("(odstęp: czekano 0.5 s)"));
    }

    [Fact]
    public async Task LoginGapIsNeverLongerThanRegularGap()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        var delays = new List<TimeSpan>();
        var throttle = new RequestThrottle(clock, new FixedRandom(0.0), minGap: TimeSpan.FromSeconds(2), maxGap: TimeSpan.FromSeconds(2),
            loginMinGap: TimeSpan.FromSeconds(30), loginMaxGap: TimeSpan.FromSeconds(30), delay: (d, _) =>
            {
                delays.Add(d);
                clock.Advance(d);
                return Task.CompletedTask;
            });

        (await throttle.AcquireAsync(CancellationToken.None)).Dispose();
        (await throttle.AcquireAsync(CancellationToken.None, loginStep: true)).Dispose();

        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(delays));
    }

    private sealed class SecondsComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 0.001;
        public int GetHashCode(double obj) => 0;
    }

    [Fact]
    public async Task DayChangeDuringWaitPreventsRequest()
    {
        using var s = new Setup(0.5, start: new DateTime(2026, 11, 12, 23, 59, 55));

        await s.GetAsync(s.Parent);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.GetAsync(s.Student));

        Assert.True(s.Guard.DayChanged);
        Assert.Single(s.RequestTimes);
    }

    [Fact]
    public async Task CancelledWaitReleasesTurn()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 12, 10, 0, 0));
        var throttle = new RequestThrottle(clock, new FixedRandom(0.5), delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        (await throttle.AcquireAsync(CancellationToken.None)).Dispose();

        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttle.AcquireAsync(cancel.Token));

        clock.Advance(TimeSpan.FromSeconds(9));
        using var slot = await throttle.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, slot.Waited);
    }

    [Fact]
    public async Task ConcurrentRequestsAreSerializedWithGap()
    {
        using var s = new Setup(0.0);

        await Task.WhenAll(s.GetAsync(s.Parent), s.GetAsync(s.Student), s.GetAsync(s.Parent));

        Assert.Equal(3, s.RequestTimes.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), s.RequestTimes[1] - s.RequestTimes[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), s.RequestTimes[2] - s.RequestTimes[1]);
    }
}
