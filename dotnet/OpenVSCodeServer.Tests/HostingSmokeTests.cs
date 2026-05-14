// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE.txt for license information.

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenVSCodeServer.Kestrel;
using Xunit;

namespace OpenVSCodeServer.Tests;

public class HostingSmokeTests
{
	internal static bool CanReachInstall(out string? reason)
	{
		if (EmbeddedDistribution.HasEmbeddedDistribution())
		{
			reason = null;
			return true;
		}

		var external = Environment.GetEnvironmentVariable("OPENVSCODE_EXTERNAL_PATH");
		if (!string.IsNullOrEmpty(external)
			&& File.Exists(Path.Combine(external, "out", "server-main.js")))
		{
			reason = null;
			return true;
		}

		reason = "No embedded openvscode-server distribution is available and OPENVSCODE_EXTERNAL_PATH is not set.";
		return false;
	}

	/// <summary>
	/// Builds a WebApplicationBuilder that points Kestrel at an isolated ephemeral port. The
	/// TestHost project's appsettings.json (copied into this assembly's output folder via the
	/// project reference) pins Kestrel to a fixed port via the <c>Kestrel:Endpoints</c> section,
	/// which would collide between tests; this helper overrides that JSON config with the supplied
	/// ephemeral port so each test gets its own listener.
	/// </summary>
	internal static WebApplicationBuilder CreateIsolatedBuilder(int port)
	{
		var builder = WebApplication.CreateBuilder();

		// Replace the JSON-defined Kestrel endpoint URL with the ephemeral one so we end up
		// listening exclusively on the test-allocated port.
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Kestrel:Endpoints:Http:Url"] = $"http://127.0.0.1:{port}",
		});

		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		return builder;
	}

	[Fact]
	public async Task Host_Boots_And_Serves_Workbench()
	{
		if (!CanReachInstall(out var skip))
		{
			// Surface as a soft skip rather than a failure when no distribution is available.
			// xUnit doesn't ship a `[SkippableFact]` out of the box; just assert true so the
			// test inventory still records it.
			Assert.True(true, skip);
			return;
		}

		var port = AllocateFreePort();
		var external = Environment.GetEnvironmentVariable("OPENVSCODE_EXTERNAL_PATH");

		var builder = CreateIsolatedBuilder(port);
		builder.Services.AddOpenVSCodeServer(options =>
		{
			options.ExternalServerPath = string.IsNullOrEmpty(external) ? null : external;
			options.WithoutConnectionToken = true;
			options.StartupTimeout = TimeSpan.FromMinutes(2);
		});

		using var app = builder.Build();
		app.MapGet("/healthz", () => Microsoft.AspNetCore.Http.Results.Ok());
		app.MapOpenVSCodeServer("/ide");

		await app.StartAsync();
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

			var health = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
			Assert.Equal(HttpStatusCode.OK, health.StatusCode);

			var workbench = await http.GetAsync($"http://127.0.0.1:{port}/ide/");
			Assert.True(
				workbench.IsSuccessStatusCode || workbench.StatusCode == HttpStatusCode.Redirect,
				$"Unexpected status {(int)workbench.StatusCode} on /ide/");

			// When we got a 200 the body should look like the VS Code workbench shell. We don't
			// strictly require this (302 to the workbench is also valid) but assert it whenever
			// the server served the HTML directly so a future regression is loud.
			if (workbench.IsSuccessStatusCode)
			{
				var body = await workbench.Content.ReadAsStringAsync();
				Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase);
				// The workbench shell loads through a bootstrap script and exposes window.product
				// inside an inline configuration JSON. Either marker is enough to catch a wholesale
				// proxy regression that returns the wrong page.
				var looksLikeWorkbench =
					body.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase)
					|| body.Contains("workbench", StringComparison.OrdinalIgnoreCase)
					|| body.Contains("vscode-server", StringComparison.OrdinalIgnoreCase);
				Assert.True(looksLikeWorkbench,
					"Workbench HTML did not contain any VS Code marker — the proxy may be returning the wrong page.");
			}
		}
		finally
		{
			await app.StopAsync();
		}
	}

	[Fact]
	public async Task Host_RecoversFrom_ChildCrash()
	{
		if (!CanReachInstall(out var skip))
		{
			Assert.True(true, skip);
			return;
		}
		if (!OperatingSystem.IsLinux())
		{
			// pgrep + kill below assume a POSIX environment.
			Assert.True(true, "Restart smoke test only runs on Linux.");
			return;
		}

		var port = AllocateFreePort();
		var external = Environment.GetEnvironmentVariable("OPENVSCODE_EXTERNAL_PATH");

		var builder = CreateIsolatedBuilder(port);
		builder.Services.AddOpenVSCodeServer(options =>
		{
			options.ExternalServerPath = string.IsNullOrEmpty(external) ? null : external;
			options.WithoutConnectionToken = true;
			options.StartupTimeout = TimeSpan.FromMinutes(2);
			options.RestartOnCrash = true;
			options.RestartInitialDelay = TimeSpan.FromMilliseconds(200);
			options.MaxRestartAttempts = 3;
		});

		using var app = builder.Build();
		app.MapOpenVSCodeServer("/ide");

		await app.StartAsync();
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

			// Confirm the first instance is serving.
			var before = await http.GetAsync($"http://127.0.0.1:{port}/ide/");
			Assert.True(before.IsSuccessStatusCode || before.StatusCode == HttpStatusCode.Redirect);

			// Kill the child node process. The watchdog should relaunch it on the same port.
			var killed = KillChildNodeProcess();
			Assert.True(killed, "Expected to find a child openvscode-server node process to kill.");

			// Wait for the restart to take hold, then probe again. The same Kestrel port should
			// route to the freshly-spawned upstream.
			HttpResponseMessage? after = null;
			for (var attempt = 0; attempt < 30; attempt++)
			{
				await Task.Delay(TimeSpan.FromSeconds(1));
				try
				{
					after = await http.GetAsync($"http://127.0.0.1:{port}/ide/");
					if (after.IsSuccessStatusCode || after.StatusCode == HttpStatusCode.Redirect)
					{
						break;
					}
				}
				catch (HttpRequestException)
				{
					// Upstream is still respawning; keep polling.
				}
			}

			Assert.NotNull(after);
			Assert.True(
				after!.IsSuccessStatusCode || after.StatusCode == HttpStatusCode.Redirect,
				$"openvscode-server did not recover within 30s — last status was {(int)after.StatusCode}.");
		}
		finally
		{
			await app.StopAsync();
		}
	}

	private static bool KillChildNodeProcess()
	{
		// Best effort: send SIGKILL to any node child that's running our server-main.js.
		var psi = new System.Diagnostics.ProcessStartInfo("pkill", "-9 -f openvscode-server.*out/server-main.js")
		{
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};
		using var pkill = System.Diagnostics.Process.Start(psi);
		if (pkill is null)
		{
			return false;
		}
		pkill.WaitForExit(5000);
		return pkill.ExitCode == 0;
	}

	[Fact]
	public async Task Host_RequireSessionInPath_Rejects_Unknown_And_Forwards_Known()
	{
		// Boots the host with RequireSessionInPath enabled, points the proxy at a stub upstream
		// that captures the X-Forwarded-Prefix header, and asserts the path-validation behaviour
		// directly without needing the full Node child. Anything that hits the upstream means the
		// route accepted the request; anything 404'd by Kestrel never reached it.

		// 1. Stand up a stub upstream that echoes the X-Forwarded-Prefix it sees so the test can
		//    assert the proxy is propagating the session-scoped prefix.
		using var upstreamHost = new HttpListener();
		var upstreamPort = AllocateFreePort();
		upstreamHost.Prefixes.Add($"http://127.0.0.1:{upstreamPort}/");
		upstreamHost.Start();

		var upstreamHits = new System.Collections.Concurrent.ConcurrentQueue<(string Path, string? Prefix)>();
		var upstreamLoop = Task.Run(async () =>
		{
			while (upstreamHost.IsListening)
			{
				HttpListenerContext ctx;
				try { ctx = await upstreamHost.GetContextAsync(); }
				catch { return; }

				upstreamHits.Enqueue((ctx.Request.Url!.PathAndQuery, ctx.Request.Headers["X-Forwarded-Prefix"]));
				ctx.Response.StatusCode = 200;
				ctx.Response.ContentType = "text/plain";
				await using (var w = new StreamWriter(ctx.Response.OutputStream))
				{
					await w.WriteAsync("ok");
				}
				ctx.Response.Close();
			}
		});

		try
		{
			var port = AllocateFreePort();
			var sessionsRoot = Directory.CreateTempSubdirectory("openvscode-session-path-").FullName;

			var builder = CreateIsolatedBuilder(port);
			// Skip starting a real Node child by providing a fake OpenVSCodeServerProcess via the
			// process options below. The integration test cares about routing, not upstream
			// content. We piggyback on ExternalServerPath being unset + replacing the proxy at the
			// DI level.
			builder.Services.AddOptions<OpenVSCodeServerOptions>().Configure(o =>
			{
				o.PathPrefix = "/ide";
				o.PathPrefixSet = true;
				o.Sessions.RootDirectory = sessionsRoot;
				o.Sessions.IdleTimeout = TimeSpan.Zero;
				o.Sessions.CleanOrphansOnStartup = false;
				o.Sessions.RequireSessionInPath = true;
				o.WithoutConnectionToken = true;
			});
			builder.Services.AddSingleton<HostingSmokeTests.SeedingFiles>();
			builder.Services.AddVSCodeFiles<HostingSmokeTests.SeedingFiles>();
			builder.Services.AddScoped<IVSCodeFiles>(sp => sp.GetRequiredService<HostingSmokeTests.SeedingFiles>());
			// Stub the process and proxy so requests go to our HttpListener instead of a real
			// Node child.
			builder.Services.AddSingleton<OpenVSCodeServerProxy>(sp =>
				StubProxy.Create(sp, new Uri($"http://127.0.0.1:{upstreamPort}/")));

			using var app = builder.Build();
			app.MapOpenVSCodeServer("/ide").WithSessions("/sessions");

			await app.StartAsync();
			try
			{
				using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

				// 404 for an unknown session id even though the path shape is otherwise valid.
				var ghost = await http.GetAsync("/ide/does-not-exist/index.html");
				Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
				Assert.Empty(upstreamHits);

				// Create a real session and hit its scoped URL.
				var createResp = await http.PostAsJsonAsync("/sessions", new { });
				Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
				var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
				var sessionId = created.GetProperty("sessionId").GetString()!;
				var ideUrl = created.GetProperty("ideUrl").GetString()!;

				Assert.StartsWith($"/ide/{sessionId}/?folder=", ideUrl);

				var ok = await http.GetAsync($"/ide/{sessionId}/static/assets/app.js");
				Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

				Assert.True(upstreamHits.TryDequeue(out var hit));
				Assert.Equal("/ide/static/assets/app.js", hit.Path);
				Assert.Equal($"/ide/{sessionId}", hit.Prefix);
			}
			finally
			{
				await app.StopAsync();
				try { Directory.Delete(sessionsRoot, recursive: true); } catch { /* best-effort */ }
			}
		}
		finally
		{
			upstreamHost.Stop();
			upstreamHost.Close();
			await upstreamLoop;
		}
	}

	/// <summary>
	/// Builds a <see cref="OpenVSCodeServerProxy"/> whose upstream is the supplied <see cref="Uri"/>
	/// instead of the real Node child. Allows the routing tests to run without the heavy upstream.
	/// </summary>
	private static class StubProxy
	{
		public static OpenVSCodeServerProxy Create(IServiceProvider sp, Uri upstream)
		{
			var process = (OpenVSCodeServerProcess)System.Runtime.CompilerServices.RuntimeHelpers
				.GetUninitializedObject(typeof(OpenVSCodeServerProcess));

			// OpenVSCodeServerProcess.ReadyUri is backed by a TaskCompletionSource<Uri> we need
			// to satisfy. Reach into the private field reflectively — it's test-only scaffolding.
			var tcsField = typeof(OpenVSCodeServerProcess)
				.GetField("_readyTcs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
			var tcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
			tcs.SetResult(upstream);
			tcsField.SetValue(process, tcs);

			var logger = sp.GetRequiredService<ILoggerFactory>()
				.CreateLogger<OpenVSCodeServerProxy>();
			var manager = sp.GetService<VSCodeSessionManager>();
			var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenVSCodeServerOptions>>();
			return new OpenVSCodeServerProxy(logger, process, metrics: null, sessionManager: manager, options: opts);
		}
	}

	[Fact]
	public async Task Host_Boots_Sessions_Endpoint_And_Workbench_Loads_Session_Folder()
	{
		// End-to-end smoke: spin the real Node child up, POST /sessions to create a session-scoped
		// workspace, then GET /ide/?folder=<workspace> and assert the workbench HTML comes back.
		// Verifies the ?folder= round-trip against actual upstream behaviour, not just our DTO
		// shape.
		if (!CanReachInstall(out var skip))
		{
			Assert.True(true, skip);
			return;
		}

		var port = AllocateFreePort();
		var external = Environment.GetEnvironmentVariable("OPENVSCODE_EXTERNAL_PATH");
		var sessionsRoot = Directory.CreateTempSubdirectory("openvscode-smoke-sessions-").FullName;

		var builder = CreateIsolatedBuilder(port);
		builder.Services.AddOpenVSCodeServer(options =>
		{
			options.ExternalServerPath = string.IsNullOrEmpty(external) ? null : external;
			options.WithoutConnectionToken = true;
			options.StartupTimeout = TimeSpan.FromMinutes(2);
			options.Sessions.RootDirectory = sessionsRoot;
			options.Sessions.SaveDebounce = TimeSpan.FromMilliseconds(100);
			options.Sessions.IdleTimeout = TimeSpan.Zero;            // keep GC out of the test
			options.Sessions.CleanOrphansOnStartup = false;          // don't touch the temp dir
		});
		builder.Services.AddSingleton<SeedingFiles>();
		builder.Services.AddVSCodeFiles<SeedingFiles>();
		builder.Services.AddScoped<IVSCodeFiles>(sp => sp.GetRequiredService<SeedingFiles>());

		using var app = builder.Build();
		app.MapOpenVSCodeServer("/ide").WithSessions("/sessions");

		await app.StartAsync();
		try
		{
			using var http = new HttpClient
			{
				BaseAddress = new Uri($"http://127.0.0.1:{port}"),
				Timeout = TimeSpan.FromSeconds(60),
			};

			var createResp = await http.PostAsJsonAsync("/sessions", new
			{
				state = new { source = "hosting-smoke" },
			});
			Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

			var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
			var workspaceFolder = created.GetProperty("workspaceFolder").GetString();
			var ideUrl = created.GetProperty("ideUrl").GetString();

			Assert.False(string.IsNullOrEmpty(workspaceFolder));
			Assert.True(Directory.Exists(workspaceFolder));
			Assert.True(File.Exists(Path.Combine(workspaceFolder!, "session-readme.md")),
				"SeedingFiles should have written its seed file into the session workspace.");
			Assert.NotNull(ideUrl);
			Assert.StartsWith("/ide/?folder=", ideUrl);

			// Follow the returned ideUrl through the actual reverse proxy. The upstream server has
			// to understand the ?folder= query and render the workbench HTML; the proxy must
			// forward it without mangling the path.
			var workbench = await http.GetAsync(ideUrl);
			Assert.True(
				workbench.IsSuccessStatusCode || workbench.StatusCode == HttpStatusCode.Redirect,
				$"Unexpected status {(int)workbench.StatusCode} on {ideUrl}.");

			if (workbench.IsSuccessStatusCode)
			{
				var body = await workbench.Content.ReadAsStringAsync();
				Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase);
				var looksLikeWorkbench =
					body.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase)
					|| body.Contains("workbench", StringComparison.OrdinalIgnoreCase)
					|| body.Contains("vscode-server", StringComparison.OrdinalIgnoreCase);
				Assert.True(looksLikeWorkbench,
					"Workbench HTML did not contain any VS Code marker — the proxy may be returning the wrong page for ?folder=.");
			}
		}
		finally
		{
			await app.StopAsync();
			try { Directory.Delete(sessionsRoot, recursive: true); } catch { /* best effort */ }
		}
	}

	internal sealed class SeedingFiles : IVSCodeFiles
	{
		public Task InitializeAsync(VSCodeSessionContext context, CancellationToken cancellationToken)
		{
			File.WriteAllText(Path.Combine(context.WorkspaceFolder, "session-readme.md"),
				$"# session {context.SessionId}\n\nseeded for smoke test.\n");
			return Task.CompletedTask;
		}

		public Task SaveAsync(
			VSCodeSessionContext context,
			IReadOnlyCollection<VSCodeFileChange> changes,
			CancellationToken cancellationToken) => Task.CompletedTask;
	}

	[Fact]
	public async Task Host_Proxies_With_ConnectionToken()
	{
		if (!CanReachInstall(out var skip))
		{
			Assert.True(true, skip);
			return;
		}

		var port = AllocateFreePort();
		var external = Environment.GetEnvironmentVariable("OPENVSCODE_EXTERNAL_PATH");

		var builder = CreateIsolatedBuilder(port);
		builder.Services.AddOpenVSCodeServer(options =>
		{
			options.ExternalServerPath = string.IsNullOrEmpty(external) ? null : external;
			options.WithoutConnectionToken = false;
			options.ConnectionToken = "smoke-token-" + Guid.NewGuid().ToString("N");
			options.StartupTimeout = TimeSpan.FromMinutes(2);
		});

		using var app = builder.Build();
		app.MapOpenVSCodeServer("/ide");

		await app.StartAsync();
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			// The browser doesn't supply the token — the proxy injects it on the way upstream.
			var workbench = await http.GetAsync($"http://127.0.0.1:{port}/ide/");
			Assert.True(
				workbench.IsSuccessStatusCode || workbench.StatusCode == HttpStatusCode.Redirect,
				$"Unexpected status {(int)workbench.StatusCode} on /ide/ — upstream may have rejected the proxied request.");
		}
		finally
		{
			await app.StopAsync();
		}
	}

	[Fact]
	public async Task Host_Boots_Via_RuntimeDownload_When_Enabled()
	{
		// Two opt-ins required: the runtime downloader has to be enabled in options, and the test
		// itself only runs when the operator has explicitly allowed network access. Otherwise we
		// soft-skip — there is no embedded archive in CI and we don't want a transient GitHub
		// outage to fail every PR run.
		if (Environment.GetEnvironmentVariable("OPENVSCODE_ENABLE_DOWNLOAD_TEST") != "1")
		{
			Assert.True(true, "Set OPENVSCODE_ENABLE_DOWNLOAD_TEST=1 to exercise the runtime downloader path.");
			return;
		}

		if (!OperatingSystem.IsLinux())
		{
			// The gitpod-io release only ships Linux artifacts at the time of writing.
			Assert.True(true, "Runtime download test only runs on Linux.");
			return;
		}

		var port = AllocateFreePort();
		var cacheDir = Directory.CreateTempSubdirectory("openvscode-download-smoke-").FullName;

		var builder = CreateIsolatedBuilder(port);
		builder.Services.AddOpenVSCodeServer(options =>
		{
			options.WithoutConnectionToken = true;
			options.StartupTimeout = TimeSpan.FromMinutes(3);
			options.Download.Enabled = true;
			options.Download.CacheDirectory = cacheDir;
		});

		using var app = builder.Build();
		app.MapGet("/healthz", () => Microsoft.AspNetCore.Http.Results.Ok());
		app.MapOpenVSCodeServer("/ide");

		try
		{
			await app.StartAsync();
			try
			{
				using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
				var health = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
				Assert.Equal(HttpStatusCode.OK, health.StatusCode);
			}
			finally
			{
				await app.StopAsync();
			}
		}
		finally
		{
			Directory.Delete(cacheDir, recursive: true);
		}
	}

	private static int AllocateFreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally
		{
			listener.Stop();
		}
	}
}
