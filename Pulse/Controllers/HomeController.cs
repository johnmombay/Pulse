using Pulse.Models;
using Pulse.Services;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Security.Claims;

namespace Pulse.Controllers
{
	public class HomeController(SchedulerService schedulerService) : Controller
	{
		public async Task<IActionResult> Index()
		{
			if (User.Identity?.IsAuthenticated == true)
			{
				var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
				ViewBag.RecentAlerts = await schedulerService.GetRecentResultsAsync(userId, 15);
				ViewBag.UnreadCount  = await schedulerService.GetUnreadCountAsync(userId);
			}
			return View();
		}

		public IActionResult Privacy() => View();

		[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
		public IActionResult Error()
			=> View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
	}
}
