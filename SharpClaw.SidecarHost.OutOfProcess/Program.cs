using SharpClaw.SidecarHost.OutOfProcess;

await using var host = await OutOfProcessModuleServer.CreateAsync(args);
await host.RunAsync();
