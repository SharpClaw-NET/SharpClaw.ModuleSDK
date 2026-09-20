using System.Reflection;
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
}
