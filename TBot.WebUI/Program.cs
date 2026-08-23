using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.FileProviders;
using Tbot.Common.Settings;
using TBot.Common.Logging.Hubs;

namespace TBot.WebUI {
	public static class WebApp {
		static WebApp() {
			_builder = WebApplication.CreateBuilder();
		}

		private static WebApplicationBuilder _builder;
		private static WebApplication _webApplication;

		public static IServiceCollection GetServiceCollection() {
			return _builder.Services;
		}

		public static async Task<IServiceProvider> Build() {
			var assembly = Assembly.GetExecutingAssembly();
			_builder.Services.AddControllersWithViews()
				.AddRazorRuntimeCompilation()
				.AddApplicationPart(assembly)
				.AddControllersAsServices();
			_builder.Services.AddSignalR();

			_builder.Services.AddResponseCompression(options =>
			{
				options.EnableForHttps = true;
			});

			// Folder path intentionally not printed here (was leaking the deploy path into shared
			// logs/screenshots) - see LoggerServiceSharedState/HideSensitiveDataInLogs if per-instance
			// diagnostics on the folder path are ever needed again.

			var settingsFile = await SettingsService.GetSettings(SettingsService.GlobalSettingsPath);
			string urls = (string) settingsFile.WebUI.Urls;


			_builder.WebHost.UseUrls(urls.Split(",").Select(c => c.Trim()).ToArray());
			_webApplication = _builder.Build();
			return _webApplication.Services;
		}

		public static async Task Main(CancellationToken ct) {
			// Configure the HTTP request pipeline.
			if (!_webApplication.Environment.IsDevelopment()) {
				_webApplication.UseExceptionHandler("/Home/Error");
			}

			// This WebUI is only ever served over plain HTTP locally (settings.json's WebUI.Urls
			// is an http:// address, no HTTPS binding/certificate is configured anywhere) - both
			// UseHttpsRedirection() and UseHsts() would tell the browser to force HTTPS on this
			// host (immediately via a 307 redirect to a scheme nothing is listening on, and/or
			// cached for future visits via the Strict-Transport-Security header), breaking every
			// subsequent http://localhost:PORT visit with a connection failure. If the browser
			// already cached that HSTS policy from before this fix, clearing it
			// (about:networking#hsts in Firefox) is needed too.

			var filesProvider = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
			_webApplication.UseStaticFiles(new StaticFileOptions() {
				FileProvider = filesProvider
			});

			_webApplication.UseRouting();
			_webApplication.UseEndpoints(endpoints => {
				endpoints.MapHub<WebHub>("/realTimeLog");
			});

			_webApplication.UseAuthorization();

			_webApplication.UseResponseCompression();

			_webApplication.MapControllerRoute(
				name: "default",
				pattern: "{controller=Home}/{action=Index}/{id?}");

			await _webApplication.RunAsync(ct);
		}
	}
}
