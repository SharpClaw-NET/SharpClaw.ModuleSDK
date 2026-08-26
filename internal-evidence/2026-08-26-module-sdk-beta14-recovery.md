# ModuleSDK Beta14 Recovery Record

## Objective

This record recovers the latest valid ModuleSDK objective after the stale beta24 and beta17 reactivation. The authoritative source state is commit `e55c00262dab549444cf22074be20e3f8a6690a7` on `main`. This turn preserves that state and records the current package boundary and blocker.

The earlier temporary edit changed the OutOfProcess project to beta13, Contracts beta24, and Core beta17. I reverted that edit before this record was created. No source version, dependency, package, or runtime change remains from that edit.

## Repository State

The repository path is `D:\Source\SharpClaw-NET\SharpClaw.ModuleSDK`. Local `main`, `origin/main`, and the remote `main` reference all identify `e55c00262dab549444cf22074be20e3f8a6690a7`. The tracked worktree is clean before this record, and no unowned change was found.

The latest production source correction is `962b37b589ba9dfd2bf80e7b5a361de5f3add85a`. It imports the authenticated receiving peer relay through the existing action exchange. Later commits through `e55c00262dab549444cf22074be20e3f8a6690a7` add bounded test diagnostics and preserve the source state. Those later commits remain unchanged.

The current OutOfProcess project declares package version `0.5.0-beta.14`. It references SharpClaw.Contracts `0.5.0-beta.27` and SharpClaw.Core `0.5.0-beta.20`. The current source version and dependency lines are authoritative for this recovery. No version change is authorized before Overwatch reviews the recovered boundary.

## Public Package State

A read-only NuGet.org flat-container check lists SharpClaw.ModuleSDK through `0.5.0-beta.9` and SharpClaw.ModuleHost.InProcess through `0.5.0-beta.9`. It lists SharpClaw.ModuleHost.OutOfProcess through `0.5.0-beta.13`, so beta14 is not public on NuGet.org.

The same check lists SharpClaw.Contracts through `0.5.0-beta.27` and SharpClaw.Core through `0.5.0-beta.20`. No package was packed, pushed, republished, or mutated during this recovery turn. No credential, package archive, cache, or generated output was changed.

## Latest Blocker

The latest valid experiment used public Contracts beta27 and Core beta20. A target cross-sidecar dispatch can be cancelled before the terminal exchange imports the peer relay. The next source dispatch then issues peer sequence two while the receiving module session remains at sequence zero.

Contracts rejects `ImportCrossSidecarActionEntryPeerRelay` with `The cross-sidecar peer relay call is already used or outside the receiving budget.` The receiving session has no public authenticated transition that consumes this peer relay without a terminal request. ModuleSDK cannot create that state without a private wire field, local authority, second session, fallback, or second action route.

The latest disposable evidence is under `D:\temp\SharpClaw.ModuleSDK\hostentry-storage-continuation-beta14-20260826`. The latest report is `docs/internal/codex/2026-08-26-cross-sidecar-rotation-experiment.md`. The evidence shows the blocker is Contracts-owned and does not justify a ModuleSDK workaround.

## Validation State

The prior beta14 production validation passed 136 OutOfProcess tests, 13 ModuleSDK tests, and 3 InProcess tests after the storage cleanup correction. The exact three-sidecar Agent gate still failed `agents.job.import` before its import storage write. That gate result is not a complete production acceptance result.

No beta14 candidate is accepted or published. The prior beta14 candidate remains unpublished and must not be treated as a public production identity. This recovery turn ran no build, test, pack, publish, cleanup, or process gate.

## Next Turn

The next bounded turn requires an accepted public Contracts correction and compatible Core identity for receiving peer-relay continuation after pre-terminal cancellation. It must start from clean pushed `main` and preserve the current beta14 and beta27 or beta20 source lines until that authority arrives.

After the dependency authority is public, rerun the focused cross-sidecar cancellation and rotation tests. Then rerun all 136 OutOfProcess tests, all 13 ModuleSDK tests, and all 3 InProcess tests. Only a complete green result may proceed to the beta14 package identity gate, package-only consumer, eight-entry tools check, and separate-process proof.

## Disposition

The only intended tracked change in this turn is this sanitized recovery record. It uses the tracked `internal-evidence` path because `/docs/internal` is ignored and is a shared Overwatch link. This record contains no credentials, temporary configuration, archive, log, cache, or generated output.

The report will be committed with `docs: record ModuleSDK beta14 recovery` and pushed to `main`. The active goal remains blocked on the external Contracts boundary. No publication request is made.
