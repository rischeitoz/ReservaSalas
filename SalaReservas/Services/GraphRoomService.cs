using System.Globalization;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using SalaReservas.Models;

namespace SalaReservas.Services;

public interface IRoomService
{
    Task<RoomStatusDto> GetStatusAsync(CancellationToken ct);
    Task<BookingResult> BookAsync(BookingRequest req, CancellationToken ct);
}

public class GraphRoomService : IRoomService
{
    // Un solo proceso IIS (maxProcesses=1): este semáforo serializa las reservas
    // y evita duplicados simultáneos. La comprobación final se repite dentro del bloqueo.
    private static readonly SemaphoreSlim BookingLock = new(1, 1);

    private readonly GraphServiceClient _graph;
    private readonly RoomOptions _room;
    private readonly TimeZoneInfo _tz;
    private readonly ILogger<GraphRoomService> _log;

    public GraphRoomService(IOptions<GraphOptions> g, IOptions<RoomOptions> r, ILogger<GraphRoomService> log)
    {
        _room = r.Value;
        _log = log;
        _tz = FindTz(_room.TimeZone);
        var cred = new ClientSecretCredential(g.Value.TenantId, g.Value.ClientId, g.Value.ClientSecret);
        _graph = new GraphServiceClient(cred, new[] { "https://graph.microsoft.com/.default" });
    }

    private static TimeZoneInfo FindTz(string id)
    {
        foreach (var candidate in new[] { id, "Romance Standard Time", "Europe/Madrid" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); } catch { }
        }
        return TimeZoneInfo.Utc;
    }

    private async Task<List<MeetingDto>> GetEventsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var result = await _graph.Users[_room.Mailbox].CalendarView.GetAsync(rc =>
        {
            rc.QueryParameters.StartDateTime = fromUtc.ToString("yyyy-MM-ddTHH:mm:ss.0000000", CultureInfo.InvariantCulture);
            rc.QueryParameters.EndDateTime = toUtc.ToString("yyyy-MM-ddTHH:mm:ss.0000000", CultureInfo.InvariantCulture);
            rc.QueryParameters.Orderby = new[] { "start/dateTime" };
            rc.QueryParameters.Select = new[] { "id", "subject", "organizer", "start", "end", "isCancelled", "showAs", "isOnlineMeeting" };
            rc.QueryParameters.Top = 100;
            rc.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        }, ct);

        var list = new List<MeetingDto>();
        foreach (var e in result?.Value ?? new List<Event>())
        {
            if (e.IsCancelled == true || e.ShowAs == FreeBusyStatus.Free) continue;
            if (e.Start?.DateTime is null || e.End?.DateTime is null) continue;
            list.Add(new MeetingDto(
                e.Id ?? Guid.NewGuid().ToString(),
                string.IsNullOrWhiteSpace(e.Subject) ? "(Sin título)" : e.Subject!,
                e.Organizer?.EmailAddress?.Name ?? "",
                ParseUtc(e.Start.DateTime),
                ParseUtc(e.End.DateTime),
                e.IsOnlineMeeting == true));
        }
        return list.OrderBy(m => m.StartUtc).ToList();
    }

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public async Task<RoomStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var status = new RoomStatusDto { RoomName = _room.DisplayName, ServerUtc = now };
        try
        {
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, _tz);
            var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localNow.Date, _tz);
            var dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(localNow.Date.AddDays(1), _tz);

            var events = await GetEventsAsync(dayStartUtc, dayEndUtc, ct);
            status.Current = events.FirstOrDefault(m => m.StartUtc <= now && m.EndUtc > now);
            status.IsBusy = status.Current != null;
            status.Upcoming = events.Where(m => m.StartUtc > now).ToList();
            if (!status.IsBusy)
                status.FreeUntilUtc = status.Upcoming.FirstOrDefault()?.StartUtc;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error consultando Graph");
            status.Error = "No se puede conectar con el calendario. Reintentando…";
        }
        return status;
    }

    public async Task<BookingResult> BookAsync(BookingRequest req, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var start = DateTime.SpecifyKind(req.StartUtc, DateTimeKind.Utc);
        // Tolerancia: permitir "ahora" aunque el reloj de la tablet vaya unos minutos atrasado.
        if (start < now.AddMinutes(-5)) return new(false, "La hora de inicio ya ha pasado.");
        if (start < now) start = now;
        start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, DateTimeKind.Utc);

        if (req.Minutes < _room.MinMinutes || req.Minutes > _room.MaxMinutes)
            return new(false, $"La duración debe estar entre {_room.MinMinutes} y {_room.MaxMinutes} minutos.");

        var end = start.AddMinutes(req.Minutes);

        // Horario laborable (hora local)
        var ls = TimeZoneInfo.ConvertTimeFromUtc(start, _tz);
        var le = TimeZoneInfo.ConvertTimeFromUtc(end, _tz);
        if (TimeSpan.TryParse(_room.WorkdayStart, CultureInfo.InvariantCulture, out var ws) &&
            TimeSpan.TryParse(_room.WorkdayEnd, CultureInfo.InvariantCulture, out var we))
        {
            if (ls.TimeOfDay < ws || le.Date != ls.Date || le.TimeOfDay > we)
                return new(false, $"Solo se puede reservar entre las {_room.WorkdayStart} y las {_room.WorkdayEnd}.");
        }

        var title = req.Title.Trim();
        var organizer = string.IsNullOrWhiteSpace(req.Organizer) ? "tablet de sala" : req.Organizer.Trim();

        await BookingLock.WaitAsync(ct);
        try
        {
            // Comprobación de disponibilidad DENTRO del bloqueo, contra el calendario real.
            var conflicts = await GetEventsAsync(start, end, ct);
            if (conflicts.Any(m => m.StartUtc < end && m.EndUtc > start))
                return new(false, "La sala ya está reservada en ese horario.");

            var ev = new Event
            {
                Subject = title,
                Body = new ItemBody
                {
                    ContentType = BodyType.Text,
                    Content = $"Reservado desde la tablet de la sala por: {organizer}"
                },
                Start = new DateTimeTimeZone { DateTime = start.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), TimeZone = "UTC" },
                End = new DateTimeTimeZone { DateTime = end.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), TimeZone = "UTC" },
                ShowAs = FreeBusyStatus.Busy,
                // Guarda quién reservó para trazabilidad
                Location = new Location { DisplayName = _room.DisplayName }
            };
            if (_room.CreateTeamsMeeting)
            {
                ev.IsOnlineMeeting = true;
                ev.OnlineMeetingProvider = OnlineMeetingProviderType.TeamsForBusiness;
            }

            var created = await _graph.Users[_room.Mailbox].Events.PostAsync(ev, cancellationToken: ct);
            var dto = new MeetingDto(created?.Id ?? "", title, organizer, start, end, _room.CreateTeamsMeeting);
            _log.LogInformation("Reserva creada: {Title} {Start:u}-{End:u} por {Org}", title, start, end, organizer);
            return new(true, "Reserva confirmada.", dto);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error creando evento");
            return new(false, "No se pudo crear la reserva. Inténtalo de nuevo.");
        }
        finally
        {
            BookingLock.Release();
        }
    }
}
