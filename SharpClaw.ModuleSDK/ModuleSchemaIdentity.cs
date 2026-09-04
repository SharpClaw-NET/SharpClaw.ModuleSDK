using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Creates deterministic schema references for module discovery.</summary>
public static class ModuleSchemaIdentity
{
    /// <summary>Creates an action input schema reference.</summary>
    public static JsonSchemaReference ActionInput(
        SharpClawActionKey key,
        int version,
        Type actionType) =>
        TypeSchema("action.input", key.Value, version, actionType);

    /// <summary>Creates an action result schema reference.</summary>
    public static JsonSchemaReference ActionResult(
        SharpClawActionKey key,
        int version,
        Type resultType) =>
        TypeSchema("action.result", key.Value, version, resultType);

    /// <summary>Creates an event payload schema reference.</summary>
    public static JsonSchemaReference EventPayload(
        SharpClawEventKey key,
        int version,
        Type eventType) =>
        TypeSchema("event.payload", key.Value, version, eventType);

    /// <summary>Creates a schema reference for an untyped action selector.</summary>
    public static JsonSchemaReference UntypedAction(string role, string target) =>
        TextSchema($"sidecar.action.{role}", target, 1);

    /// <summary>Creates a schema reference for an untyped event selector.</summary>
    public static JsonSchemaReference UntypedEvent(string target) =>
        TextSchema("sidecar.event.payload", target, 1);

    /// <summary>Creates a tool input schema reference from its JSON schema.</summary>
    public static JsonSchemaReference ToolInput(ToolDescriptor descriptor) =>
        TextSchema("tool.input", descriptor.Name, descriptor.Version, CanonicalJson(descriptor.ParametersSchema));

    /// <summary>Creates the fixed tool result schema reference.</summary>
    public static JsonSchemaReference ToolResult(ToolDescriptor descriptor) =>
        TypeSchema("tool.result", descriptor.Name, descriptor.Version, typeof(ToolResult));

    private static JsonSchemaReference TypeSchema(
        string role,
        string key,
        int version,
        Type type)
    {
        var contractName = $"sharpclaw.kernel.{role}.{key}";
        var input = $"{contractName}|{version}|{type.AssemblyQualifiedName}";
        return new JsonSchemaReference(contractName, version, Hash(input));
    }

    private static JsonSchemaReference TextSchema(
        string role,
        string key,
        int version,
        string? value = null)
    {
        var contractName = $"sharpclaw.module-sdk.{role}.{key}";
        return new JsonSchemaReference(contractName, version, Hash($"{contractName}|{version}|{value}"));
    }

    private static string CanonicalJson(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? "undefined" : value.GetRawText();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
