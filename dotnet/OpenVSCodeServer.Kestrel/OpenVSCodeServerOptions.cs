// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE.txt for license information.

using Microsoft.AspNetCore.Http;

namespace OpenVSCodeServer.Kestrel;

/// <summary>
/// Options that control how the embedded OpenVSCode Server is extracted and launched.
/// </summary>
public sealed class OpenVSCodeServerOptions
{
	/// <summary>
	/// Workspace folder opened by VS Code when the user first connects. Translates to the
	/// upstream <c>--default-folder</c> flag. When null the upstream server applies its own
	/// default (an empty workbench).
	/// </summary>
	public string? WorkspaceFolder { get; set; }

	/// <summary>
	/// Directory used to extract the embedded distribution. Defaults to a sub-folder of
	/// <see cref="Path.GetTempPath"/> derived from a hash of the bundled archive so that
	/// multiple library versions can coexist on the same machine.
	/// </summary>
	public string? ExtractionDirectory { get; set; }

	/// <summary>
	/// If set, points at an existing openvscode-server installation on disk. When non-null
	/// the embedded resource is ignored entirely. The directory must contain
	/// <c>out/server-main.js</c> and a <c>node</c> binary at the root.
	/// </summary>
	public string? ExternalServerPath { get; set; }

	/// <summary>
	/// Optional connection token used to gate access to the IDE. Equivalent to the upstream
	/// <c>--connection-token</c> flag. Ignored when <see cref="WithoutConnectionToken"/> is true.
	/// </summary>
	public string? ConnectionToken { get; set; }

	/// <summary>
	/// Forwarded to the upstream <c>--without-connection-token</c> flag. Defaults to true so
	/// that the IDE is immediately reachable from the parent ASP.NET Core application (which
	/// is expected to handle authentication at its own layer).
	/// </summary>
	public bool WithoutConnectionToken { get; set; } = true;

	/// <summary>
	/// Loopback address the child server binds to. Almost always <c>127.0.0.1</c>; Kestrel is the
	/// outer-facing listener.
	/// </summary>
	public string Host { get; set; } = "127.0.0.1";

	/// <summary>
	/// Port the child server binds to. When null an ephemeral port is allocated automatically.
	/// </summary>
	public int? Port { get; set; }

	/// <summary>
	/// How long to wait for the child server to print its "Web UI available" banner before
	/// declaring startup a failure.
	/// </summary>
	public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>
	/// Additional command-line arguments forwarded verbatim to <c>server-main.js</c>.
	/// </summary>
	public IList<string> AdditionalArguments { get; } = new List<string>();

	/// <summary>
	/// Path prefix that Kestrel exposes the IDE under (e.g. <c>/ide</c>). Set automatically by
	/// <see cref="OpenVSCodeServerEndpointRouteBuilderExtensions.MapOpenVSCodeServer"/> on the
	/// first call; later mounts share the same backing process but have their own routes — see
	/// <see cref="AdditionalMountPrefixes"/>.
	/// </summary>
	public string PathPrefix { get; internal set; } = "/";

	/// <summary>
	/// Tracks whether <see cref="OpenVSCodeServerEndpointRouteBuilderExtensions.MapOpenVSCodeServer"/>
	/// has already established the canonical prefix. Internal — external callers shouldn't override.
	/// </summary>
	internal bool PathPrefixSet { get; set; }

	/// <summary>
	/// Additional Kestrel mount points layered onto the same backing Node process. The first
	/// <see cref="OpenVSCodeServerEndpointRouteBuilderExtensions.MapOpenVSCodeServer"/> call sets
	/// <see cref="PathPrefix"/> (which becomes <c>--server-base-path</c>); subsequent calls with a
	/// different prefix add to this collection. They are reachable through the reverse proxy but
	/// the workbench HTML will reference the canonical prefix in its absolute URLs.
	/// </summary>
	public IList<string> AdditionalMountPrefixes { get; } = new List<string>();

	/// <summary>
	/// Environment variables to set on the child process (in addition to the inherited environment).
	/// </summary>
	public IDictionary<string, string?> EnvironmentOverrides { get; } =
		new Dictionary<string, string?>(StringComparer.Ordinal);

	/// <summary>
	/// Controls the runtime downloader that fetches a pre-built openvscode-server distribution
	/// from GitHub when neither <see cref="ExternalServerPath"/> nor an embedded archive is
	/// available. Disabled by default to avoid surprise network access in production.
	/// </summary>
	public OpenVSCodeServerDownloadOptions Download { get; } = new();

	/// <summary>
	/// Tunables for the per-request session feature exposed by
	/// <see cref="OpenVSCodeServerEndpointRouteBuilderExtensions.MapOpenVSCodeServerSessions"/>.
	/// </summary>
	public VSCodeSessionOptions Sessions { get; } = new();

	/// <summary>
	/// When true (default), the hosted service watches the child node process and restarts it
	/// with exponential backoff if it exits unexpectedly after a successful initial startup.
	/// </summary>
	public bool RestartOnCrash { get; set; } = true;

	/// <summary>
	/// Maximum number of automatic restart attempts after an unexpected child exit. Zero means
	/// unlimited. The counter resets when a restart succeeds and the child stays alive for
	/// longer than <see cref="RestartAttemptResetWindow"/>.
	/// </summary>
	public int MaxRestartAttempts { get; set; } = 5;

	/// <summary>
	/// Initial delay before the first restart attempt. Subsequent attempts double the delay up
	/// to <see cref="RestartMaxDelay"/>.
	/// </summary>
	public TimeSpan RestartInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

	/// <summary>
	/// Upper bound on the restart back-off.
	/// </summary>
	public TimeSpan RestartMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// If a restarted child stays alive at least this long, the retry counter resets to zero so
	/// transient crashes don't accumulate forever.
	/// </summary>
	public TimeSpan RestartAttemptResetWindow { get; set; } = TimeSpan.FromMinutes(2);

	/// <summary>
	/// Asserts that the options are internally consistent. Called automatically by the hosted
	/// service before launching the child process so misconfigurations fail at startup with a
	/// clear message instead of producing opaque downstream errors.
	/// </summary>
	internal void Validate()
	{
		if (Port is { } port && (port < 0 || port > 65535))
		{
			throw new ArgumentOutOfRangeException(nameof(Port), port,
				"Port must be in the range 0–65535 (use null to allocate an ephemeral port).");
		}

		if (StartupTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(StartupTimeout), StartupTimeout,
				"StartupTimeout must be positive.");
		}

		if (MaxRestartAttempts < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(MaxRestartAttempts), MaxRestartAttempts,
				"MaxRestartAttempts must be non-negative (use 0 for unlimited).");
		}

		if (RestartInitialDelay < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(RestartInitialDelay), RestartInitialDelay,
				"RestartInitialDelay cannot be negative.");
		}

		if (RestartMaxDelay < RestartInitialDelay)
		{
			throw new ArgumentException(
				$"RestartMaxDelay ({RestartMaxDelay}) must be >= RestartInitialDelay ({RestartInitialDelay}).",
				nameof(RestartMaxDelay));
		}

		if (!WithoutConnectionToken && string.IsNullOrEmpty(ConnectionToken))
		{
			// We auto-generate one in this case — fine, but warn loud if the caller passed an
			// empty string, which is almost certainly a bug.
			if (ConnectionToken is { Length: 0 })
			{
				throw new ArgumentException(
					"ConnectionToken is set to an empty string. Either set WithoutConnectionToken=true, "
					+ "supply a non-empty token, or leave the token unset to let the library auto-generate one.",
					nameof(ConnectionToken));
			}
		}
	}
}

/// <summary>
/// Options for the runtime distribution downloader.
/// </summary>
public sealed class OpenVSCodeServerDownloadOptions
{
	/// <summary>
	/// When true, <see cref="EmbeddedDistribution.Materialize"/> will fetch a pre-built tarball
	/// from <see cref="BaseUrl"/> if no embedded asset or <see cref="OpenVSCodeServerOptions.ExternalServerPath"/>
	/// is available. Disabled by default.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// openvscode-server release tag to fetch (e.g. <c>v1.109.5</c>). The leading <c>v</c> is
	/// optional. Defaults to <see cref="OpenVSCodeServerDownloader.DefaultVersion"/>.
	/// </summary>
	public string Version { get; set; } = OpenVSCodeServerDownloader.DefaultVersion;

	/// <summary>
	/// Optional full override URL. When set, <see cref="Version"/>, <see cref="BaseUrl"/> and the
	/// detected platform/arch are ignored.
	/// </summary>
	public string? Url { get; set; }

	/// <summary>
	/// Root URL containing the release tag folders. Defaults to the gitpod-io GitHub release URL.
	/// </summary>
	public string BaseUrl { get; set; } = OpenVSCodeServerDownloader.DefaultBaseUrl;

	/// <summary>
	/// Expected SHA-256 (hex) of the downloaded archive. When set, the downloader refuses to use
	/// the file unless the hash matches. Strongly recommended for production deployments.
	/// </summary>
	public string? Sha256 { get; set; }

	/// <summary>
	/// Directory used to cache the downloaded tarball. Defaults to a sub-folder of
	/// <see cref="Path.GetTempPath"/>.
	/// </summary>
	public string? CacheDirectory { get; set; }

	/// <summary>
	/// Maximum time allowed for the download to complete.
	/// </summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Options governing the per-request session feature surfaced via
/// <see cref="OpenVSCodeServerEndpointRouteBuilderExtensions.MapOpenVSCodeServerSessions"/>.
/// </summary>
public sealed class VSCodeSessionOptions
{
	/// <summary>
	/// Filesystem path under which each session's temporary workspace folder is created. When
	/// null, defaults to <c>${TEMP}/openvscode-sessions</c>. The directory is created if missing.
	/// </summary>
	public string? RootDirectory { get; set; }

	/// <summary>
	/// Quiet period after the last filesystem event before <see cref="IVSCodeFiles.SaveAsync"/> is
	/// invoked. Tune larger for noisier workspaces (formatters, language-server scratch files),
	/// smaller for tighter save latency. Defaults to 500ms.
	/// </summary>
	public TimeSpan SaveDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// How long a session may sit idle (no proxy traffic, no <c>GET /sessions/{id}</c>, no
	/// heartbeat call) before the manager calls <see cref="IVSCodeFiles.SaveAsync"/> and tears it
	/// down. Defaults to 30 minutes. Set to <see cref="TimeSpan.Zero"/> to disable idle GC and
	/// keep sessions alive until the process shuts down or the caller invokes
	/// <c>DELETE /sessions/{id}</c>.
	/// </summary>
	public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

	/// <summary>
	/// How often the idle sweeper wakes up to evict expired sessions. Defaults to one minute,
	/// clamped to no more than <see cref="IdleTimeout"/>. Ignored when <see cref="IdleTimeout"/>
	/// is <see cref="TimeSpan.Zero"/>.
	/// </summary>
	public TimeSpan IdleSweepInterval { get; set; } = TimeSpan.FromMinutes(1);

	/// <summary>
	/// When true (default), <see cref="OpenVSCodeServerProxy"/> inspects every inbound request
	/// for a <c>?folder=&lt;workspace&gt;</c> query parameter and refreshes the matching session's
	/// last-seen timestamp. Disable if you prefer to refresh strictly via the heartbeat endpoint.
	/// </summary>
	public bool RefreshOnProxyTraffic { get; set; } = true;

	/// <summary>
	/// When true (default), the manager scans <see cref="RootDirectory"/> on startup and removes
	/// any sub-directories left behind by a previous run that crashed before disposing its
	/// sessions. The temp folders are leaks otherwise — nothing else cleans them up.
	/// </summary>
	public bool CleanOrphansOnStartup { get; set; } = true;

	/// <summary>
	/// When true, <see cref="OpenVSCodeServerEndpointRouteBuilderExtensions.MapOpenVSCodeServer"/>
	/// mounts the proxy at <c>{prefix}/{sessionId}/{**catchall}</c> instead of the bare
	/// <c>{prefix}/{**catchall}</c>. Inbound requests whose first path segment after the prefix
	/// doesn't match a live session are rejected with 404 before any traffic reaches the upstream
	/// node process. The proxy also sets <c>X-Forwarded-Prefix: {prefix}/{sessionId}</c> on the
	/// upstream request so vscode emits absolute URLs that include the session id (it honours
	/// that header in <c>webClientServer.ts</c>).
	/// <para>The <c>ideUrl</c> returned from <c>POST /sessions</c> includes the session id in the
	/// path when this flag is set: <c>{prefix}/{sessionId}/?folder=…</c>. Default is false to
	/// preserve back-compat with hosts that gate access at the ASP.NET Core authorization layer
	/// instead.</para>
	/// </summary>
	public bool RequireSessionInPath { get; set; }

	/// <summary>
	/// Optional hook invoked at the start of <c>POST /sessions</c>, before any temp folder is
	/// created or <see cref="IVSCodeFiles.InitializeAsync"/> runs. Implementers use this to
	/// authenticate the caller (cookie, JWT, header, tenant claims …) and either:
	/// <list type="bullet">
	///   <item>Return <c>null</c> to allow the session to be created.</item>
	///   <item>Return a non-null <see cref="IResult"/> (e.g. <see cref="Results.Unauthorized"/>,
	///   <see cref="Results.Forbid"/>, <see cref="Results.Problem(string?, string?, int?, string?, string?)"/>)
	///   to short-circuit the request — the returned result is written to the response verbatim
	///   and the session is NOT created.</item>
	/// </list>
	/// <para>The hook may also mutate
	/// <see cref="VSCodeSessionValidationContext.State"/> to thread server-trusted values
	/// (resolved user id, tenant id, document id) down to
	/// <see cref="IVSCodeFiles.InitializeAsync"/>; the mutated dictionary is what ends up on
	/// <see cref="VSCodeSessionContext.State"/>.</para>
	/// <para>The library deliberately does not enforce authentication on the other session
	/// endpoints (<c>GET</c>, <c>DELETE</c>, heartbeat) or on the IDE proxy itself — gate those
	/// with the host's normal ASP.NET Core authorization policies (e.g.
	/// <c>app.MapOpenVSCodeServer("/ide").WithSessions("/sessions").RequireAuthorization()</c>).</para>
	/// </summary>
	public Func<VSCodeSessionValidationContext, ValueTask<IResult?>>? ValidateUser { get; set; }
}

/// <summary>
/// Context handed to <see cref="VSCodeSessionOptions.ValidateUser"/>. Carries the in-flight
/// <see cref="HttpContext"/> so the hook can read claims, cookies, headers etc., plus the
/// mutable <c>state</c> dictionary parsed from the request body.
/// </summary>
public sealed class VSCodeSessionValidationContext
{
	/// <summary>
	/// The HTTP context for the <c>POST /sessions</c> request. <see cref="HttpContext.User"/> is
	/// the authenticated principal (when an auth scheme ran upstream); <see cref="HttpContext.RequestServices"/>
	/// resolves any DI services the hook depends on.
	/// </summary>
	public required HttpContext HttpContext { get; init; }

	/// <summary>
	/// Mutable view of the <c>state</c> dictionary supplied by the client. The hook may add or
	/// overwrite entries (e.g. to attach a server-trusted user id) and the resulting dictionary
	/// is what gets passed to <see cref="IVSCodeFiles.InitializeAsync"/>.
	/// </summary>
	public required IDictionary<string, string> State { get; init; }
}
