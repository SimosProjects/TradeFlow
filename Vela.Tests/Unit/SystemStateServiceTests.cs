using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vela.Worker.Configuration;
using Vela.Worker.Data;
using Vela.Worker.Engine;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

/// <summary>
/// Unit tests for SystemStateService and the TradeGuard cache properties it reads.
/// All tests run in memory with no external dependencies.
/// </summary>
public class SystemStateServiceTests
{
    // -- Builders --

    private static TradeGuard BuildTradeGuard()
    {
        var broker = new Mock<IBrokerService>();
        broker.Setup(b => b.GetAccountBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0m);
        broker.Setup(b => b.GetOpenPositionsValueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0m);
        return new TradeGuard(
            broker.Object,
            Options.Create(new RiskEngineOptions()),
            NullLogger<TradeGuard>.Instance);
    }

    private static IbkrConnectionService BuildIbkrService()
    {
        var discord = new DiscordNotificationService(NullLogger<DiscordNotificationService>.Instance);
        return new IbkrConnectionService(
            Options.Create(new IbkrOptions { Host = "127.0.0.1", Port = 4002, ClientId = 9 }),
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance,
            discord);
    }

    private static MarketRegimeService BuildRegimeService() =>
        new(NullLogger<MarketRegimeService>.Instance);

    private static (SystemStateService svc, string dbName) BuildService(TradeGuard? guard = null)
    {
        guard ??= BuildTradeGuard();
        var ibkr       = BuildIbkrService();
        var regime     = BuildRegimeService();
        var riskOptions = Options.Create(new RiskEngineOptions
        {
            RegimeBullishSizingPct = 1.0,
            RegimeChoppySizingPct  = 0.5,
            RegimeBearishSizingPct = 0.25,
            RegimeBearishBlockCalls = true,
        });

        var dbName = $"sysstate_{Guid.NewGuid():N}";
        var dbOptions = new DbContextOptionsBuilder<VelaDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => new VelaDbContext(dbOptions));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var svc = new SystemStateService(
            scopeFactory, ibkr, guard, regime, riskOptions, NullLogger<SystemStateService>.Instance);

        return (svc, dbName);
    }

    private static VelaDbContext OpenDb(string dbName) =>
        new(new DbContextOptionsBuilder<VelaDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    // -- TradeGuard cache property tests --

    [Fact]
    public void TradeGuard_CachedBalance_ReturnsValueFromSetCacheForTesting()
    {
        var guard = BuildTradeGuard();
        guard.SetCacheForTesting(52_140m, 0m);
        guard.CachedBalance.Should().Be(52_140m);
    }

    [Fact]
    public void TradeGuard_CachedOpenValue_ReturnsValueFromSetCacheForTesting()
    {
        var guard = BuildTradeGuard();
        guard.SetCacheForTesting(0m, 2_850m);
        guard.CachedOpenValue.Should().Be(2_850m);
    }

    [Fact]
    public void TradeGuard_CachedProperties_DefaultToZeroBeforeFirstRefresh()
    {
        var guard = BuildTradeGuard();
        guard.CachedBalance.Should().Be(0m);
        guard.CachedOpenValue.Should().Be(0m);
    }

    // -- WriteHeartbeatAsync DB write tests --

    [Fact]
    public async Task WriteHeartbeat_NoExistingRow_CreatesRowWithAllValues()
    {
        var guard = BuildTradeGuard();
        guard.SetCacheForTesting(52_140m, 2_850m);
        var (svc, dbName) = BuildService(guard);

        svc.UpdateRegime("Bullish", 1.0m, false, 578.42m, 572.10m, 561.40m, 521.80m, 13.21m, -1.4m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var db = OpenDb(dbName);
        var row = await db.SystemState.FindAsync(1);

        row.Should().NotBeNull();
        row!.RegimeTier.Should().Be("Bullish");
        row.SizingMultiplier.Should().Be(1.0m);
        row.BlockCalls.Should().BeFalse();
        row.SpyPrice.Should().Be(578.42m);
        row.Ma20.Should().Be(572.10m);
        row.Ma50.Should().Be(561.40m);
        row.Ma200.Should().Be(521.80m);
        row.Vix.Should().Be(13.21m);
        row.VixDelta.Should().Be(-1.4m);
        row.ChopScore.Should().Be(1);
        row.AccountBalance.Should().Be(52_140m);
        row.OpenValue.Should().Be(2_850m);
        row.WorkerHeartbeat.Should().NotBeNull();
        row.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WriteHeartbeat_ExistingRow_OverwritesRegimeAndAccount()
    {
        var (svc, dbName) = BuildService();

        using (var seed = OpenDb(dbName))
        {
            seed.SystemState.Add(new SystemState { Id = 1, RegimeTier = "Bearish", SizingMultiplier = 0.25m });
            await seed.SaveChangesAsync();
        }

        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var verify = OpenDb(dbName);
        var row = await verify.SystemState.FindAsync(1);
        row!.RegimeTier.Should().Be("Bullish");
        row.SizingMultiplier.Should().Be(1.0m);
    }

    [Fact]
    public async Task WriteHeartbeat_DoesNotOverwriteIsPaused()
    {
        // The Api owns is_paused. The Worker heartbeat must never reset it.
        var (svc, dbName) = BuildService();

        using (var seed = OpenDb(dbName))
        {
            seed.SystemState.Add(new SystemState { Id = 1, IsPaused = true });
            await seed.SaveChangesAsync();
        }

        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var verify = OpenDb(dbName);
        var row = await verify.SystemState.FindAsync(1);
        row!.IsPaused.Should().BeTrue("heartbeat must not overwrite is_paused set by the Api");
    }

    [Fact]
    public async Task WriteHeartbeat_WithForceRegime_AppliesOverrideAndClears()
    {
        // When the dashboard writes force_regime, the next heartbeat applies it to
        // MarketRegimeService and clears the column so the override only fires once.
        var (svc, dbName) = BuildService();

        using (var seed = OpenDb(dbName))
        {
            seed.SystemState.Add(new SystemState
            {
                Id          = 1,
                RegimeTier  = "Bearish",
                ForceRegime = "Bullish",
            });
            await seed.SaveChangesAsync();
        }

        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var verify = OpenDb(dbName);
        var row = await verify.SystemState.FindAsync(1);
        row!.RegimeTier.Should().Be("Bullish", "override should have been applied");
        row.BlockCalls.Should().BeFalse("Bullish regime should not block calls");
        row.ForceRegime.Should().BeNull("override should be cleared after a single application");
    }

    [Fact]
    public async Task WriteHeartbeat_DbUnavailable_DoesNotPropagate()
    {
        // Verify the trading path is never affected by a failed DB write.
        var guard      = BuildTradeGuard();
        var ibkr       = BuildIbkrService();
        var regime     = BuildRegimeService();
        var riskOptions = Options.Create(new RiskEngineOptions());

        var dbOptions = new DbContextOptionsBuilder<VelaDbContext>()
            .UseInMemoryDatabase($"sysstate_throw_{Guid.NewGuid():N}")
            .Options;

        var disposedDb = new VelaDbContext(dbOptions);
        disposedDb.Dispose();

        var services = new ServiceCollection();
        services.AddScoped<VelaDbContext>(_ => disposedDb);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var svc = new SystemStateService(
            scopeFactory, ibkr, guard, regime, riskOptions, NullLogger<SystemStateService>.Instance);

        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);

        var act = () => svc.WriteHeartbeatAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteHeartbeat_Cancelled_DoesNotPropagate()
    {
        // OperationCanceledException on shutdown must be swallowed cleanly.
        var (svc, _) = BuildService();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => svc.WriteHeartbeatAsync(cts.Token);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteHeartbeat_DefaultRegime_WritesUnknownTier()
    {
        // Before 9:20am ET the regime has not been set, row should still be created.
        var (svc, dbName) = BuildService();

        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using var db = OpenDb(dbName);
        var row = await db.SystemState.FindAsync(1);
        row.Should().NotBeNull();
        row!.RegimeTier.Should().Be("Unknown");
        row.SizingMultiplier.Should().Be(1.0m);
    }

    // -- Manual block-flag pin tests (2026-08-13 QQQ lotto incident) --
    //
    // A manually pinned block flag must never be silently reverted by regime auto-sync, in
    // either direction, until the user changes it again. Reproduces the exact incident: a
    // dashboard Block Lotto toggle was silently reverted by the next Bullish-regime checkpoint,
    // letting a lotto trade through despite the user believing it was blocked.

    [Fact]
    public async Task UpdateRegime_AfterManualLottoPin_DoesNotRevertToRegimeDerivedValue()
    {
        var (svc, dbName) = BuildService();

        // First checkpoint establishes Bullish regime — lotto is unblocked per regime.
        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        // User manually blocks lotto via the dashboard despite the Bullish regime.
        using (var seed = OpenDb(dbName))
        {
            var row = await seed.SystemState.FindAsync(1);
            row!.BlockLottoOverride = true;
            await seed.SaveChangesAsync();
        }
        await svc.WriteHeartbeatAsync(CancellationToken.None); // detects the DB change, pins it

        using (var verify = OpenDb(dbName))
        {
            var row = await verify.SystemState.FindAsync(1);
            row!.BlockLottoOverride.Should().BeTrue("manual toggle should have been pinned");
            row.BlockLottoManuallySet.Should().BeTrue();
        }

        // A fresh regime checkpoint fires — still Bullish, still says lotto shouldn't be
        // blocked. The pin must hold — this is the exact incident.
        svc.UpdateRegime("Bullish", 1.0m, false, 580m, 572m, 561m, 521m, 13m, -1m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using (var verify = OpenDb(dbName))
        {
            var row = await verify.SystemState.FindAsync(1);
            row!.BlockLottoOverride.Should().BeTrue(
                "a manually pinned block flag must survive a regime checkpoint, in either direction");
        }
    }

    [Fact]
    public async Task ApplyRegimeDerivedBlocks_AfterManualLottoPin_DoesNotRevertToRegimeDerivedValue()
    {
        var (svc, dbName) = BuildService();

        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        // User enables the blanket override AND manually blocks lotto.
        using (var seed = OpenDb(dbName))
        {
            var row = await seed.SystemState.FindAsync(1);
            row!.AllowOverrideBlocks = true;
            row.BlockLottoOverride = true;
            await seed.SaveChangesAsync();
        }
        await svc.WriteHeartbeatAsync(CancellationToken.None); // detects both changes, pins lotto

        using (var verify = OpenDb(dbName))
        {
            var row = await verify.SystemState.FindAsync(1);
            row!.BlockLottoManuallySet.Should().BeTrue();
        }

        // User disables the blanket override — this fires ApplyRegimeDerivedBlocks(), which
        // must still respect the per-flag pin even though blanket protection just ended.
        using (var seed = OpenDb(dbName))
        {
            var row = await seed.SystemState.FindAsync(1);
            row!.AllowOverrideBlocks = false;
            await seed.SaveChangesAsync();
        }
        await svc.WriteHeartbeatAsync(CancellationToken.None); // fires ApplyRegimeDerivedBlocks
        await svc.WriteHeartbeatAsync(CancellationToken.None); // flushes the pinned value to DB

        using (var verify = OpenDb(dbName))
        {
            var row = await verify.SystemState.FindAsync(1);
            row!.BlockLottoOverride.Should().BeTrue(
                "disabling the blanket override must not itself revert a manually pinned flag");
        }
    }

    [Fact]
    public async Task LoadRegimeFromDatabaseAsync_WithPersistedManualPin_RestoresPinnedValueNotRegimeDerived()
    {
        var (svc, dbName) = BuildService();

        using (var seed = OpenDb(dbName))
        {
            seed.SystemState.Add(new SystemState
            {
                Id = 1,
                RegimeTier = "Bullish",           // regime-derived lotto value would be false
                AllowOverrideBlocks = false,
                BlockLottoOverride = true,         // but the user pinned it blocked
                BlockLottoManuallySet = true,
            });
            await seed.SaveChangesAsync();
        }

        bool? observedLotto = null;
        svc.BlockLottoOverrideChanged += v => observedLotto = v;

        // svc is a fresh instance from BuildService with no manual-pin state in memory yet —
        // exactly like a real Worker restart reading persisted state for the first time.
        await svc.LoadRegimeFromDatabaseAsync(CancellationToken.None);

        observedLotto.Should().BeTrue(
            "a manual pin must survive a Worker restart, not reset to the regime-derived value");

        using var verify = OpenDb(dbName);
        var row = await verify.SystemState.FindAsync(1);
        row!.BlockLottoOverride.Should().BeTrue();
    }

    [Fact]
    public async Task WriteHeartbeat_WhenManuallySetFlagCleared_ImmediatelyRestoresRegimeDerivedValue()
    {
        // "Return to regime auto-management" only clears the pin flag from the dashboard — the
        // Worker must notice that on its next heartbeat and immediately re-derive the value from
        // the current regime, not leave it stuck at the last pinned value for up to ~2 hours
        // until the next scheduled MarketConditions checkpoint.
        var (svc, dbName) = BuildService();

        svc.UpdateRegime("Bullish", 1.0m, false, 578m, 572m, 561m, 521m, 13m, -1m, 1);
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using (var seed = OpenDb(dbName))
        {
            var row = await seed.SystemState.FindAsync(1);
            row!.BlockLottoOverride = true;
            await seed.SaveChangesAsync();
        }
        await svc.WriteHeartbeatAsync(CancellationToken.None); // pins it

        using (var verify = OpenDb(dbName))
        {
            var row = await verify.SystemState.FindAsync(1);
            row!.BlockLottoManuallySet.Should().BeTrue();
        }

        // User clicks "Return to auto" — the dashboard clears only the pin flag.
        using (var seed = OpenDb(dbName))
        {
            var row = await seed.SystemState.FindAsync(1);
            row!.BlockLottoManuallySet = false;
            await seed.SaveChangesAsync();
        }
        await svc.WriteHeartbeatAsync(CancellationToken.None);

        using (var verify = OpenDb(dbName))
        {
            var row = await verify.SystemState.FindAsync(1);
            row!.BlockLottoManuallySet.Should().BeFalse();
            row.BlockLottoOverride.Should().BeFalse(
                "clearing the pin must immediately re-derive from the current Bullish regime, " +
                "not wait for the next scheduled checkpoint");
        }
    }

    [Fact]
    public async Task LoadRegimeFromDatabaseAsync_WithAllowOverrideBlocksTrue_RestoresDbValuesRegardlessOfPinFlag()
    {
        // Confirms the existing blanket AllowOverrideBlocks=true path is unchanged by this fix
        // — it protects all three flags on its own, independent of per-flag pinning.
        var (svc, dbName) = BuildService();

        using (var seed = OpenDb(dbName))
        {
            seed.SystemState.Add(new SystemState
            {
                Id = 1,
                RegimeTier = "Bullish",           // regime-derived would be false for all three
                AllowOverrideBlocks = true,
                BlockCallsOverride = true,
                BlockHighOverride = true,
                BlockLottoOverride = true,
                // None of the *ManuallySet flags are set — the blanket override alone protects.
            });
            await seed.SaveChangesAsync();
        }

        await svc.LoadRegimeFromDatabaseAsync(CancellationToken.None);

        using var verify = OpenDb(dbName);
        var row = await verify.SystemState.FindAsync(1);
        row!.BlockCallsOverride.Should().BeTrue("AllowOverrideBlocks=true must restore DB values as-is");
        row.BlockHighOverride.Should().BeTrue();
        row.BlockLottoOverride.Should().BeTrue();
    }
}