using System.Reflection;
using System.Runtime.Loader;
using FluentAssertions;
using NUnit.Framework;

namespace SharpClaw.SidecarHost.InProcess.Tests;

public sealed class ModuleLoadContextBoundaryTests
{
    [TestCase("SharpClaw.Persistence", true)]
    [TestCase("Microsoft.EntityFrameworkCore", true)]
    [TestCase("Microsoft.EntityFrameworkCore.Abstractions", true)]
    [TestCase("Microsoft.EntityFrameworkCore.Relational", true)]
    [TestCase("Microsoft.EntityFrameworkCore.SqlServer", false)]
    [TestCase("Microsoft.EntityFrameworkCore.Sqlite", false)]
    [TestCase("Npgsql.EntityFrameworkCore.PostgreSQL", false)]
    [TestCase("System.ClientModel", false)]
    [TestCase("System.Configuration.ConfigurationManager", false)]
    public void PersistenceBoundary_SharesContractsButKeepsProvidersModuleLocal(
        string assemblyName,
        bool expectedShared)
    {
        var method = typeof(RegistrationLoadContext).GetMethod(
            "IsHostSharedAssembly",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(null, [assemblyName]).Should().Be(expectedShared);
    }

    [Test]
    public void ModuleLocalFallbackLoadsAnAdjacentManagedDependencyBySimpleName()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.ModuleSDK.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostAssembly = typeof(RegistrationLoadContext).Assembly.Location;
        var dependencyAssembly = Path.Combine(AppContext.BaseDirectory, "System.ClientModel.dll");
        File.Exists(dependencyAssembly).Should().BeTrue();
        var hostPath = Path.Combine(root, Path.GetFileName(hostAssembly));
        var dependencyPath = Path.Combine(root, Path.GetFileName(dependencyAssembly));
        File.Copy(hostAssembly, hostPath);
        File.Copy(dependencyAssembly, dependencyPath);
        var context = new RegistrationLoadContext(hostPath);

        try
        {
            var dependencyName = AssemblyName.GetAssemblyName(dependencyPath).Name;
            dependencyName.Should().NotBeNullOrWhiteSpace();
            var request = new AssemblyName(dependencyName!);
            var fallback = typeof(RegistrationLoadContext).GetMethod(
                "ResolveModuleLocalAssemblyPath",
                BindingFlags.Instance | BindingFlags.NonPublic);
            fallback.Should().NotBeNull();
            fallback!.Invoke(context, [dependencyName]).Should().Be(dependencyPath);

            var loaded = context.LoadFromAssemblyName(request);

            loaded.Location.Should().Be(dependencyPath);
            AssemblyLoadContext.GetLoadContext(loaded).Should().BeSameAs(context);
        }
        finally
        {
            context.Unload();
            Directory.Delete(root, recursive: true);
        }
    }
}
