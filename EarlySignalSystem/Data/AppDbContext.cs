using EarlySignalSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace EarlySignalSystem.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<SectorScore> SectorScores => Set<SectorScore>();
    public DbSet<CompanyPick> CompanyPicks => Set<CompanyPick>();
    public DbSet<RunLog> RunLogs => Set<RunLog>();
    public DbSet<CumulativeScore> CumulativeScores => Set<CumulativeScore>();
    public DbSet<CompanyPickSignal> CompanyPickSignals => Set<CompanyPickSignal>();
    public DbSet<ShortlistSnapshot> ShortlistSnapshots => Set<ShortlistSnapshot>();
    public DbSet<TechnicalAssessment> TechnicalAssessments => Set<TechnicalAssessment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SectorScore>()
            .Property(s => s.Score)
            .HasPrecision(5, 2);

        modelBuilder.Entity<CompanyPick>()
            .Property(c => c.ConfidenceScore)
            .HasPrecision(5, 2);

        modelBuilder.Entity<CumulativeScore>()
            .Property(c => c.Score)
            .HasPrecision(5, 2);

        modelBuilder.Entity<CumulativeScore>()
            .Property(c => c.PriceOnFirstSignalDate)
            .HasPrecision(18, 4);

        modelBuilder.Entity<CumulativeScore>()
            .Property(c => c.LatestPrice)
            .HasPrecision(18, 4);

        modelBuilder.Entity<CumulativeScore>()
            .Property(c => c.PriceChangePercent)
            .HasPrecision(9, 2);

        modelBuilder.Entity<TechnicalAssessment>()
            .Property(t => t.RSI)
            .HasPrecision(9, 4);

        modelBuilder.Entity<TechnicalAssessment>()
            .Property(t => t.MACDSignal)
            .HasPrecision(9, 4);

        modelBuilder.Entity<ShortlistSnapshot>()
            .Property(s => s.CumulativeScore)
            .HasPrecision(5, 2);

        modelBuilder.Entity<Signal>()
            .HasOne(s => s.RunLog)
            .WithMany(r => r.Signals)
            .HasForeignKey(s => s.RunLogId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<SectorScore>()
            .HasOne(s => s.RunLog)
            .WithMany(r => r.SectorScores)
            .HasForeignKey(s => s.RunLogId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CompanyPick>()
            .HasOne(c => c.RunLog)
            .WithMany(r => r.CompanyPicks)
            .HasForeignKey(c => c.RunLogId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CompanyPickSignal>()
            .HasOne(cps => cps.CompanyPick)
            .WithMany(c => c.CompanyPickSignals)
            .HasForeignKey(cps => cps.CompanyPickId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CompanyPickSignal>()
            .HasOne(cps => cps.Signal)
            .WithMany()
            .HasForeignKey(cps => cps.SignalId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
