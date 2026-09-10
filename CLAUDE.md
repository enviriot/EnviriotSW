# CLAUDE.md

Notes for working in this repository: how it builds, how it is checked, how it is released.

## Environment

Windows. Two shells with different syntax are available: **PowerShell** (Windows PowerShell 5.1,
`powershell.exe`) and **Git Bash**. Installed: `dotnet` SDK 10, `git`, MSBuild from Visual Studio.

**There is no Python and no Node.js.** Do not check this with `command -v`: `python` and `python3`
are on PATH but lead to `AppInstallerPythonRedirector.exe`, the Microsoft Store stub. It prints
`Python`, exits with code 49 and does not run the script, so an attempt to compute something with
python fails silently, without an error message. `node`, `npm` and `npx` are missing entirely.

Hence: write one-off scripts and data processing in PowerShell (`ConvertFrom-Json`, `[xml]`) or
batch, never in python/node.

**Backslashes in arguments do not survive Git Bash.** `prog.exe --dst C:\X13\dir` reaches the
program as `C:X13dir` - bash strips them as escapes, and doubling them does not help. Windows reads
that as a path relative to the current directory of the drive, so the program happily runs and
writes somewhere else, without a single error message. Write paths in arguments with forward slashes
(`C:/X13/dir`) or in single quotes.

**And the mirror trap: an argument that looks like a unix path gets expanded instead.** A lone
`--paths /export/Hall/H_Sens/T` reaches the program as `C:/Program Files/Git/export/Hall/H_Sens/T`,
and that is not a filesystem path but a **topic path** - the program simply does not find it and
silently returns an empty result. A comma-separated list (`/a,/b`) is not treated as a path by MSYS
and is left alone, so a group of topics works while the same topic alone does not; that cost an hour
of hunting a defect that did not exist. Put `MSYS_NO_PATHCONV=1` in front of the call whenever an
argument is a topic path rather than a file path.

**Edit text containing backslashes with PowerShell, not `sed`/`perl`.** In a replacement string
`perl` reads `\b` as a backspace (0x08) and swallows `\<`, and the result looks like a vanished
character: `Output\bin_r` becomes `Output` + 0x08 + `in_r`, which is indistinguishable from
`Outputin_r` in a terminal and cannot be found by an ordinary search. What works is
`[IO.File]::ReadAllText` plus `String.Replace` with single quotes: there a literal stays a literal.

That also decides how the web interface is built: there is not a single `package.json` in the
repository, Lit is vendored in `Output\www\lib\lit-all.min.js`, and the frontend has no build step
at all. Files in `Output\www` are edited directly; `WebUI.www.csproj` on `Microsoft.Build.NoTargets`
only pulls them into the solution tree. There are two stacks: the IDE is built on Lit, while the
twelve dashboard components in `Output\www\components` use the vendored `lib\symbiote.js`. They do
not conflict - different files, different element names - but a trick learned in one does not work
in the other.

**The syntax of those files can still be checked - with `NiL.JS`.** "No node" does not mean "no way
to parse JS": `Output\bin_d\NiL.JS.dll` is the very engine that runs topic scripts, and its
`Module(name, code)` parses the source right in the constructor, throwing `SyntaxError` with an
exact line and column. Imports are not resolved that way - resolvers are only needed for `Run()`. 
Since the frontend has no build step, this is the only check of `Output\www\*.js` available without starting the server.

```powershell
Add-Type -Path 'D:\Projects\H05\Output\bin_d\NiL.JS.dll'
New-Object NiL.JS.Module($name, [IO.File]::ReadAllText($path))
```

Version 2.6.1722 does not understand two things, and the IDE code contains both: private class
members (`#m() {}`, `this.#m()`) and `catch` without a variable. So parse a copy in the scratchpad,
not the file itself: `sed 's/#\([A-Za-z_$]\)/_PRIV_\1/g'` plus `sed 's/catch {/catch(_e) {/g'`.
Substituting inside strings and CSS is harmless - the structure of the file does not change, and
only parsing is being checked. Modules, `static` class fields, `?.`, `??` and tagged templates parse
as they are.

A negative control is mandatory: break the copy (`X * ;` or an extra `{`) and make sure parsing
fails. Without it "no errors" may mean the check is blind - and a blind check is worse than none.

## Build, test, release

All three are one script - `.github/script/Build.ps1`. Both workflows call it too, so a local
build and a CI build cannot drift apart.

```powershell
.github\script\Build.ps1                          # Debug + tests (the default mode)
.github\script\Build.ps1 -Mode Pack               # Release + archive in Output\release
.github\script\Build.ps1 -Mode Pack -OutDir D:\t  # same, archive somewhere else
```

| Mode   | What it does                                                    | Who calls it              |
|--------|-----------------------------------------------------------------|---------------------------|
| `Test` | rebuilds Debug, runs `X13.Tests` under vstest.console            | a developer, `ci.yml`     |
| `Pack` | rebuilds Release, lays out `enviriot\{bin,www}`, writes the zip  | `release.yml`             |

Both modes are a **full** rebuild (`msbuild -t:Rebuild`) and wipe their output directory first -
`Output\bin_d` and `Output\tests` for `Test`, `Output\bin_r` for `Pack`. `Clean` removes only what
the current projects produce, and both consumers here discover by scanning a folder: the server
finds its plugins through MEF, and vstest.console finds its test adapters the same way. So a `.dll`
from a plugin that no longer exists, or from a test framework no longer referenced, would go on
being loaded at run time - and would be shipped in the release archive.

Visual Studio or Build Tools is required. The script locates MSBuild and `vstest.console.exe`
itself: `PATH` first, then `vswhere`. Tests need the "Testing tools" component. That is also why
neither workflow uses `microsoft/setup-msbuild` - on a hosted runner it only puts the same MSBuild
on `PATH`, and dropping it removes a dependency that has to be re-pinned every time GitHub retires
a Node version.

### Version numbering

`Server/Properties/VersionInfo.cs` is written on every build by the `UpdateVersionInfo` task
(`VersionUpdate.targets`) and is **not** in git: it is a build artefact. Nothing commits a
version number anywhere.

The format is still `major.minor.yyMM.(day * 1000 + counter)`, but its parts now come from
different places:

- `major.minor` is `VersionBaseline` in `VersionUpdate.targets`. That one property is the only
  thing a human edits, and only to go from 0.5 to 0.6.
- A Debug build - the IDE, or `-Mode Test` - gets a local counter, +1 per build, restarting when
  the day changes. It means "the n-th build made on this machine today" and nothing beyond that.
- A release - `-Mode Pack` - gets its number from `Get-ReleaseVersion` in `Build.ps1`: the
  highest existing release tag is the floor, and the counter is rounded up to the next whole
  hundred, so a number ending in two zeroes still marks a release. The result goes into MSBuild
  as `-p:BuildVersion` and the task writes it verbatim.

Release tags are the shared state, and that is the whole point. They travel between machines with
the release itself, which is what a committed file never really did: it produced two copies of one
counter that drifted apart, a bot commit in the middle of every release, and a conflict on that
same line at every `dev` → `master` merge.

Every build also records the commit it was made from, as
`[assembly: AssemblyMetadata("Commit", "aefa27c-dirty")]`: the short hash of `HEAD` plus a marker
for uncommitted changes. The number says which build, the commit says which source, and only the
commit means the same thing on someone else's machine - so the server logs the two side by side at
startup, `Enviriot v.0.5.2609.10001, commit aefa27c-dirty`. A build with no git around, from a
source archive, records no commit and logs `-` in its place.

`AssemblyMetadata` and not a `+<hash>` suffix on `AssemblyInformationalVersion`, which is where
such a suffix conventionally belongs: csc fills the numeric `PRODUCTVERSION` of the Win32 version
resource by parsing that attribute as four numbers, and writes `0,0,0,0` for anything it cannot
parse - so a hash there blanks out the version tab of the file's properties.

Worth knowing:

- a `dry_run` release creates no tag, so dry runs cost nothing and can be repeated;
- the configuration no longer affects the number, so an accidental Release build from the IDE
  cannot spend a release number;
- `Server.csproj` lists `Properties\VersionInfo.cs` by hand instead of letting the SDK's
  `**\*.cs` glob find it. The glob is evaluated before the target that writes the file has run,
  so on a fresh clone the server would otherwise compile with no version attributes at all and
  report `0.0.0.0` while the plugins reported the real number.

### Workflows

- **`.github/workflows/ci.yml`** - every push to any branch, plus a manual run. Calls
  `Build.ps1 -Mode Test` and attaches the `.trx` to the run, including when tests failed: the
  script writes its step outputs before checking vstest's exit code. Pushing a tag does not
  trigger it, and a second push to the same branch cancels the previous run.
- **`.github/workflows/release.yml`** - manual only (`workflow_dispatch`), with the `dry_run`
  and `prerelease` switches. Calls `Build.ps1 -Mode Pack`, attaches the archive, checks that the
  tag `v.<version>` is still free and creates the release through `gh`. It pushes nothing back
  into the branch - the number comes from the tags, so there is no version file to commit - and
  `fetch-depth: 0` is there to fetch those tags. This workflow runs no tests; those belong to
  `ci.yml`.

The script writes to `GITHUB_OUTPUT`: `version` in both modes, `trx` in `Test`, `zip` in `Pack`.

## Tests

`Tests/X13.Tests.csproj`, NUnit 4, net48, run through `vstest.console.exe`.

- The adapter package is **`NUnit3TestAdapter`** (currently 6.x) even though the project is on
  NUnit 4 - the name is historical and no `NUnit4TestAdapter` exists. `Microsoft.NET.Test.Sdk` is
  required alongside it: it supplies the test host and the build integration vstest.console needs.
- NUnit 4 moved the classic assertions - `Assert.AreEqual`, `Assert.IsNotNull` and the rest - into
  `NUnit.Framework.Legacy.ClassicAssert`. Write `Assert.That(x, Is.EqualTo(y))` instead; most NUnit
  examples online are still the classic form and will not compile here. `NUnit.Analyzers` is
  referenced to catch the remaining misuse - a swapped expected/actual, say - at compile time.
- **The assembly name `X13.Tests` cannot be changed.** Six projects opened their `internal`
  members to it with `[assembly: InternalsVisibleTo("X13.Tests")]`; under any other name that
  access closes again, and you find out only when the next test fails to compile.
- The project references every project in the solution, so a test can be written against any of
  them without touching the `.csproj` first.
- It builds into `Output\tests`, not `bin_d` - otherwise the server's directory scan would offer
  the test assembly and its adapters up as a plugin.
- It is **not built at all** in the Release configuration: in the `.sln` the project has an
  `ActiveCfg` for Release but no `Build.0`. That is what keeps the test platform packages out of
  the release archive.
- Do not delete `Tests/ScaffoldTests.cs`: vstest treats a run with no tests in it as a failure,
  and that one test also checks that the `internal` access above still works.

## The `Output` layout

Everything under `Output` is git-ignored except `Output\www`.

| Directory        | What is in it                                                                     |
|------------------|-----------------------------------------------------------------------------------|
| `Output\bin_d`   | the Debug build: server and plugins in one flat folder, which is how MEF finds them |
| `Output\bin_r`   | the same for Release                                                               |
| `Output\www`     | the WebUI static files, kept in the repository; built as `WebUI.www.csproj` (Microsoft.Build.NoTargets) in Release only |
| `Output\tests`   | `X13.Tests.dll` and `TestResults\X13.Tests.trx`                                    |
| `Output\release` | the `enviriot\{bin,www}` layout and `enviriot_v<version>.zip`                       |

## Language and documents

Code and identifiers are English. Сomments in Russian.
The transition is long and files stay mixed - that is accepted deliberately. `README.md` and
`changelog.md` are Russian; **this file is English**.

