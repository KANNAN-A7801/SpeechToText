namespace SpeechToText.Models;

public class Transcription
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public string Language { get; set; } = "Hindi + English";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
