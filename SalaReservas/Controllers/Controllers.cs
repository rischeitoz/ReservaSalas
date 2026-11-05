using Microsoft.AspNetCore.Mvc;
using SalaReservas.Models;
using SalaReservas.Services;

namespace SalaReservas.Controllers;

public class HomeController : Controller
{
    private readonly IConfiguration _cfg;
    public HomeController(IConfiguration cfg) => _cfg = cfg;

    public IActionResult Index()
    {
        ViewBag.RoomName = _cfg["Room:DisplayName"] ?? "Sala";
        ViewBag.Min = _cfg.GetValue("Room:MinMinutes", 15);
        ViewBag.Max = _cfg.GetValue("Room:MaxMinutes", 240);
        ViewBag.WorkStart = _cfg["Room:WorkdayStart"] ?? "07:00";
        ViewBag.WorkEnd = _cfg["Room:WorkdayEnd"] ?? "21:00";
        return View();
    }
}

[Route("api")]
[ApiController]
[AutoValidateAntiforgeryToken]
public class ApiController : ControllerBase
{
    private readonly IRoomService _rooms;
    public ApiController(IRoomService rooms) => _rooms = rooms;

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await _rooms.GetStatusAsync(ct));
    }

    [HttpPost("book")]
    public async Task<IActionResult> Book([FromBody] BookingRequest req, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(new BookingResult(false, "Datos no válidos: indica un título (mín. 2 caracteres)."));
        var result = await _rooms.BookAsync(req, ct);
        return result.Success ? Ok(result) : Conflict(result);
    }
}
