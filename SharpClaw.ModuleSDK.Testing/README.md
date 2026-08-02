# SharpClaw.ModuleSDK.Testing

`SharpClaw.ModuleSDK.Testing` compiles a module with the ModuleSDK and executes
its actions and events through the production `SharpClaw.Core` dispatcher.

Use `SharpClawModuleTestBuilder` to add modules and their manifests. The built
test host exposes fluent action and event builders. These builders keep Core's
outcome authority, continuation rules, effect checks, and wildcard dispatch.

Use `AddHostAction` and `AddHostEvent` for contracts that the host owns. Call
`ApproveSensitiveContributions` only when the test intentionally grants the
module access to each sensitive contract that its compiled hooks select. The
test host creates exact Core approvals and does not disable sensitive checks.

This package is for module tests. A production module host does not need it.
