# ModuleHost beta14 beta30 completion evidence

## Objective

This record documents the ModuleHost beta14 completion correction. The correction uses public SharpClaw.Contracts 0.5.0-beta.30 and SharpClaw.Core 0.5.0-beta.23. The OutOfProcess package remains 0.5.0-beta.14 and remains unpublished.

## Source and dependencies

The first source correction is commit 110d5378ce191fe5ad9fec4a01c6bf1335f00115. The focused regression fixture correction is commit 7a593673c30bf0ac4fc8ec04fa5898a4ea06ce72. Both commits are pushed to `main`. Local `main`, `origin/main`, and remote `main` identify 7a593673c30bf0ac4fc8ec04fa5898a4ea06ce72 before this evidence commit. The tracked tree is clean apart from three preserved untracked generated directories.

The OutOfProcess production and test projects request SharpClaw.Contracts 0.5.0-beta.30 and SharpClaw.Core 0.5.0-beta.23. ModuleSDK and InProcess remain at their public beta9 identities. No ModuleSDK or InProcess source file changed in this turn.

## Source validation

The NuGet.org restore used isolated caches, CLI home, artifacts, and test results below `D:\temp\SharpClaw.ModuleSDK\beta14-completecall-beta30-20260826`. The ModuleSDK Release suite passed 13 of 13 tests. The InProcess Release suite passed 3 of 3 tests. The OutOfProcess Release suite passed 141 of 141 tests. Each suite reported zero failures and zero skips.

The focused `NonRetryableCompletionReleasesLiveSessionBeforeCarrierCleanup` test passed 1 of 1 tests. It preserved `sidecar_invalid_binding` and `The terminal call count must be zero or one.` It proved zero outgoing calls, no active carrier, six later accepted typed calls, seven dispatcher calls, seven terminal calls, and a binding generation increase. The source build succeeded with zero errors and fifteen known warnings.

The focused TRX is `D:\temp\SharpClaw.ModuleSDK\beta14-completecall-beta30-20260826\focused-results-final5\completecall-final5.trx` with SHA-256 D1F704DB7A42015EE18BA7F448DE913252B8652952F6EA1397CA05482A837D92. The full SDK, InProcess, and OutOfProcess TRX files have SHA-256 F4265B438F5821D311CE5AA95D15607B63484E327755D0B79E23D8E85E9FA170, EC322A327D3B9F327A6CE6658AD8F696321894241CAD290E6F36AFA7F781EBA8, and 87658610774E58CD049D8E1350CA2175479F12316088D9F0854D7E5044C94ED8.

## Candidate package

The immutable unpublished candidate is `D:\temp\SharpClaw.ModuleSDK\beta14-completecall-beta30-20260826\candidate-corrected\SharpClaw.ModuleHost.OutOfProcess.0.5.0-beta.14.nupkg`. Its length is 1114316 bytes. Its SHA-256 is BEC0B665B4E9CE1A6795D64C81D6C9C2EAABE300B49F8CD4F1DF4232AD6CECCC.

The nuspec identifies SharpClaw.ModuleHost.OutOfProcess 0.5.0-beta.14. It declares AGPL-3.0-or-later. It uses the canonical repository and project URL `https://github.com/SharpClaw-NET/SharpClaw.ModuleSDK`. Its repository commit is 7a593673c30bf0ac4fc8ec04fa5898a4ea06ce72. Its readme is `README.md`. Its dependencies are SharpClaw.ModuleHost.InProcess 0.5.0-beta.9, SharpClaw.Contracts 0.5.0-beta.30, and SharpClaw.Core 0.5.0-beta.23.

The tools payload has exactly eight entries. They are the OutOfProcess executable, deps file, runtimeconfig file, OutOfProcess DLL, ModuleSDK DLL, InProcess DLL, Contracts DLL, and Core DLL. It has no source, project, object, PDB, test, readme, or package entry. The OutOfProcess library and tools DLL both have SHA-256 5FAC01B534955366739BEA30ECC36E205F5AD5EC5BB7C91EB532C74D7BADCC1B. The embedded ModuleSDK, InProcess, Contracts, and Core DLL hashes are B5B7C66FE3E4940C2F5B1E388C971C0EA3D5BDCE8779539457C24A52CF63110E, A95A5DD6B7B01EB757D0D3847EE4B335DEB8771CF49C01A322A7584061C32E64, DF2ADE2596BE3A54482E7DFDB29560543CD3EA0D74C64FC100F767CCB1853A85, and 9638942E3AD077DF041878F736C2E7416E46397240E8C4BE9C6E178A0C85D2D1.

The tracked `eng/Verify-PackageAssemblyIdentity.ps1` gate passed against the public beta9 ModuleSDK and InProcess packages and the candidate. The explicit tools allowlist check passed with eight actual entries, zero unexpected entries, and zero missing entries.

## Consumer validation

The package-only consumer has no project references. Its isolated NuGet configuration uses the candidate feed and public NuGet.org. Its assets file resolves OutOfProcess beta14, InProcess beta9, ModuleSDK beta9, Contracts beta30, and Core beta23. The Release build succeeded with zero errors. The executable ran and reported assembly version 0.5.0.0.

The extracted-tools smoke used the candidate tools payload without rebuilding package contents. It returned readiness HTTP 200 and discovery HTTP 200. Readiness reported `tool_lifecycle_smoke_module` and `authorized=false`. Discovery returned a 2017-character document with the expected module identifier. The wrapper stopped the host after the checks and recorded exit code -1 because it terminated the process. Standard error was empty.

## Agent gate

The required runner `D:\Temp\SharpClaw.AgentOrchestration\beta8-entry-correction\gate\IntegratedGate` is absent. The available Agent files are beta5 and beta4 packages with hashes different from the accepted inputs. They cannot replace the accepted archives. The two-sidecar and three-sidecar Agent gates remain an external artifact blocker. No Agent source was inspected or changed, and no substitute host path was used.

## Warnings and disposition

Restore and build output contains five NU1903 advisories for System.Security.Cryptography.Xml 10.0.7 and existing nullable or unused-variable warnings. These warnings did not produce test failures. No package was published. The candidate remains unpublished for Overwatch review.

The next bounded turn is the consolidated review handoff. It must include this source provenance, candidate identity, package proof, consumer proof, process proof, and the external Agent artifact blocker.
