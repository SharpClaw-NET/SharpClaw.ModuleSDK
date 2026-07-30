using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.ModuleHost.OutOfProcess.Tests;

public sealed class OutOfProcessLifecycleTests
{
    [Test, CancelAfter(30000)]
    public async Task HostStartsAuthenticatesRunsLifecycleAndStops()
    {
        var sourceDirectory = AppContext.BaseDirectory;
        var moduleDirectory = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-module-host-smoke-" + Guid.NewGuid().ToString("N"));
        var hostAssemblyPath = Environment.GetEnvironmentVariable("SHARPCLAW_SMOKE_HOST_ASSEMBLY")
            ?? typeof(OutOfProcessHost).Assembly.Location;
        var moduleAssemblyName = Path.GetFileName(typeof(LifecycleSmokeModule).Assembly.Location);
        var controlAddress = await FindFreeAddressAsync();
        var controlToken = "smoke-token-" + Guid.NewGuid().ToString("N");
        StartedHost? startedHost = null;

        try
        {
            CopyDirectory(sourceDirectory, moduleDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(moduleDirectory, "module.json"),
                $$"""
                {
                  "id": "lifecycle_smoke_module",
                  "displayName": "Lifecycle Smoke",
                  "version": "1.0.0",
                  "toolPrefix": "smoke",
                  "entryAssembly": "{{moduleAssemblyName}}",
                  "runtime": "dotnet",
                  "hostMode": "sidecar",
                  "moduleType": "{{typeof(LifecycleSmokeModule).FullName}}"
                }
                """,
                Encoding.UTF8);

            startedHost = StartHost(
                hostAssemblyPath,
                moduleDirectory,
                controlAddress,
                controlToken);

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(1),
            };

            await WaitForUnauthorizedAsync(client, controlAddress, startedHost);

            using var handshake = CreateRequest(
                HttpMethod.Post,
                controlAddress,
                ForeignModuleProtocol.HandshakePath,
                controlToken);
            using var handshakeResponse = await client.SendAsync(handshake);
            handshakeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var discovery = CreateRequest(
                HttpMethod.Get,
                controlAddress,
                ForeignModuleProtocol.DiscoveryPath,
                controlToken);
            using var discoveryResponse = await client.SendAsync(discovery);
            discoveryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var health = CreateRequest(
                HttpMethod.Get,
                controlAddress,
                ForeignModuleProtocol.HealthPath,
                controlToken);
            using var healthResponse = await client.SendAsync(health);
            healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var initialize = CreateRequest(
                HttpMethod.Post,
                controlAddress,
                ForeignModuleProtocol.InitializePath,
                controlToken);
            using var initializeResponse = await client.SendAsync(initialize);
            initializeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var shutdown = CreateRequest(
                HttpMethod.Post,
                controlAddress,
                ForeignModuleProtocol.ShutdownPath,
                controlToken);
            using var shutdownResponse = await client.SendAsync(shutdown);
            shutdownResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            await startedHost.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            startedHost.Process.ExitCode.Should().Be(0, await startedHost.ReadOutputAsync());
        }
        finally
        {
            if (startedHost is not null)
            {
                try
                {
                    await startedHost.StopAsync();
                }
                catch (InvalidOperationException)
                {
                }
                catch (TimeoutException)
                {
                }
            }

            startedHost?.Process.Dispose();

            if (Directory.Exists(moduleDirectory))
                Directory.Delete(moduleDirectory, recursive: true);
        }
    }

    private static StartedHost StartHost(
        string hostAssemblyPath,
        string moduleDirectory,
        Uri controlAddress,
        string controlToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = moduleDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(hostAssemblyPath);
        startInfo.Environment[ForeignModuleProtocol.ModuleDirectoryEnv] = moduleDirectory;
        startInfo.Environment[ForeignModuleProtocol.ControlAddressEnv] = controlAddress.ToString();
        startInfo.Environment[ForeignModuleProtocol.ControlTokenEnv] = controlToken;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the out-of-process host.");
        process.EnableRaisingEvents = true;
        return new StartedHost(
            process,
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri address,
        string path,
        string token)
    {
        var request = new HttpRequestMessage(method, new Uri(address, path));
        request.Headers.TryAddWithoutValidation(ForeignModuleProtocol.TokenHeaderName, token);
        return request;
    }

    private static async Task WaitForUnauthorizedAsync(
        HttpClient client,
        Uri address,
        StartedHost startedHost)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(address, ForeignModuleProtocol.HandshakePath));
                using var response = await client.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return;

                lastError = new InvalidOperationException(
                    $"Expected unauthorized handshake, received {(int)response.StatusCode}.");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (TaskCanceledException ex)
            {
                lastError = ex;
            }

            await Task.Delay(100);
        }

        var output = await startedHost.StopAsync();
        throw new AssertionException(
            "The host did not become ready within 15 seconds. " +
            (lastError?.ToString() ?? "No response was observed.") +
            Environment.NewLine + output);
    }

    private static async Task<Uri> FindFreeAddressAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Task.CompletedTask;
        return new Uri($"http://127.0.0.1:{port}");
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private sealed class StartedHost(
        Process process,
        Task<string> standardOutput,
        Task<string> standardError)
    {
        public Process Process { get; } = process;

        public async Task<string> ReadOutputAsync()
        {
            var stdout = await standardOutput;
            var stderr = await standardError;
            return $"stdout:\n{stdout}\nstderr:\n{stderr}";
        }

        public async Task<string> StopAsync()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            return await ReadOutputAsync();
        }
    }
}
