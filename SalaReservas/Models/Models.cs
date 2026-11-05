using System.ComponentModel.DataAnnotations;

namespace SalaReservas.Models;

public class GraphOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public class RoomOptions
{
    public string Mailbox { get; set; } = "";
    public string DisplayName { get; set; } = "Sala";
    public string TimeZone { get; set; } = "Romance Standard Time";
    public int MinMinutes { get; set; } = 15;
    public int MaxMinutes { get; set; } = 240;
    public string WorkdayStart { get; set; } = "07:00";
    public string WorkdayEnd { get; set; } = "21:00";
    public bool CreateTeamsMeeting { get; set; }
}

public record MeetingDto(string Id, string Subject, string Organizer, DateTime StartUtc, DateTime EndUtc, bool IsTeams);

public class RoomStatusDto
{
    public string RoomName { get; set; } = "";
    public DateTime ServerUtc { get; set; }
    public bool IsBusy { get; set; }
    public MeetingDto? Current { get; set; }
    public List<MeetingDto> Upcoming { get; set; } = new();
    /// <summary>Si está libre: hasta cuándo (próxima reunión). Null = libre el resto del día.</summary>
    public DateTime? FreeUntilUtc { get; set; }
    public string? Error { get; set; }
}

public class BookingRequest
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string Title { get; set; } = "";

    [StringLength(80)]
    public string? Organizer { get; set; }

    /// <summary>Inicio en UTC (ISO 8601).</summary>
    [Required]
    public DateTime StartUtc { get; set; }

    [Range(5, 1440)]
    public int Minutes { get; set; }
}

public record BookingResult(bool Success, string Message, MeetingDto? Meeting = null);
