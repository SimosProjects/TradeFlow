using Microsoft.EntityFrameworkCore;

namespace TradeFlow.Worker.Data;

/// <summary>
/// Entity Framework Core DbContext for the TradeFlow application, 
/// representing the database session and providing access to the Alerts table.
/// </summary>
public class TradeFlowDbContext : DbContext
{
    public TradeFlowDbContext(DbContextOptions<TradeFlowDbContext> options)
        : base(options) { }

    public DbSet<AlertEntity> Alerts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertEntity>(entity =>
        {
            // Map to "alerts" table
            entity.ToTable("alerts");

            // Primary key on Id
            entity.HasKey(a => a.Id);

            // Index on user_id — queries filtered by user will be common
            entity.HasIndex(a => a.UserName)
                  .HasDatabaseName("idx_alerts_username");

            // Index on symbol — allows fast retrieval of all alerts for a given stock/option
            entity.HasIndex(a => a.Symbol)
                  .HasDatabaseName("idx_alerts_symbol");

            // Index on time_of_entry_alert — allows efficient querying of recent alerts
            entity.HasIndex(a => a.TimeOfEntryAlert)
                  .HasDatabaseName("idx_alerts_time_of_entry");

            // Composite index for the most common pipeline query:
            // "give me BTO alerts from today ordered by time"
            entity.HasIndex(a => new { a.Side, a.TimeOfEntryAlert })
                  .HasDatabaseName("idx_alerts_side_time");

            // Column name conventions — lowercase snake_case for PostgreSQL
            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.UserId).HasColumnName("user_id");
            entity.Property(a => a.UserName).HasColumnName("user_name");
            entity.Property(a => a.XScore).HasColumnName("xscore");
            entity.Property(a => a.Symbol).HasColumnName("symbol");
            entity.Property(a => a.Type).HasColumnName("type");
            entity.Property(a => a.Direction).HasColumnName("direction");
            entity.Property(a => a.Strike).HasColumnName("strike");
            entity.Property(a => a.Expiration).HasColumnName("expiration");
            entity.Property(a => a.OptionsContractSymbol).HasColumnName("options_contract_symbol");
            entity.Property(a => a.ContractDescription).HasColumnName("contract_description");
            entity.Property(a => a.Side).HasColumnName("side");
            entity.Property(a => a.Status).HasColumnName("status");
            entity.Property(a => a.Result).HasColumnName("result");
            entity.Property(a => a.ActualPriceAtTimeOfAlert).HasColumnName("actual_price_at_time_of_alert");
            entity.Property(a => a.PricePaid).HasColumnName("price_paid");
            entity.Property(a => a.PriceAtExit).HasColumnName("price_at_exit");
            entity.Property(a => a.LastCheckedPrice).HasColumnName("last_checked_price");
            entity.Property(a => a.LastKnownPercentProfit).HasColumnName("last_known_percent_profit");
            entity.Property(a => a.Risk).HasColumnName("risk");
            entity.Property(a => a.IsProfitableTrade).HasColumnName("is_profitable_trade");
            entity.Property(a => a.CanAverage).HasColumnName("can_average");
            entity.Property(a => a.TimeOfEntryAlert).HasColumnName("time_of_entry_alert");
            entity.Property(a => a.TimeOfFullExitAlert).HasColumnName("time_of_full_exit_alert");
            entity.Property(a => a.FormattedLength).HasColumnName("formatted_length");
            entity.Property(a => a.IsSwing).HasColumnName("is_swing");
            entity.Property(a => a.IsBullish).HasColumnName("is_bullish");
            entity.Property(a => a.IsShort).HasColumnName("is_short");
            entity.Property(a => a.Strategy).HasColumnName("strategy");
            entity.Property(a => a.OriginalMessage).HasColumnName("original_message");
            entity.Property(a => a.OriginalExitMessage).HasColumnName("original_exit_message");
            entity.Property(a => a.IngestedAt).HasColumnName("ingested_at");
            entity.Property(a => a.RiskApproved).HasColumnName("risk_approved");
            entity.Property(a => a.RiskReason).HasColumnName("risk_reason");
        });
    }
}