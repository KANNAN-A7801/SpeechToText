using Microsoft.EntityFrameworkCore;
using SpeechToText.Models;

namespace SpeechToText.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Transcription> Transcriptions => Set<Transcription>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Transcription>().ToTable("Transcriptions");
    }
}
