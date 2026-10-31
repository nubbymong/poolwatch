# Contributing

Thanks for helping. PoolWatch is a small project with one maintainer, so short, focused changes are easiest to take.
[AGENTS.md](AGENTS.md) has the full rules for working on the code (build, security, privacy, branches); AI coding agents
read it first.

- **Security problems**: never in a public issue or pull request. See [SECURITY.md](SECURITY.md).
- **Bugs**: open an issue with the bug template. Redact what you paste (see "Privacy" in the README): use Export report
  with redaction on, and read it before you attach it.
- **Known issues**: if something grows the kernel pool on many PCs, propose it with the known issue template so
  PoolWatch can recognise it, or add it yourself as a file in [knowledge/](knowledge) (the format is in
  [docs/knowledge-packs.md](docs/knowledge-packs.md)).
- **The Windows services list** (`src/PoolWatch.Core/Services/services-catalogue.json`): each Windows service's row
  says where the advice to turn it off comes from, in a `source` object: `kind` is `wdot` (Microsoft's Windows Desktop
  Optimization Tool), `cis` (the CIS Microsoft Windows 11 Enterprise Benchmark), `both` or `none`, in lower case, with
  `wdotRef` and `cisRef` for the item in each and `checked` for the day the row was last checked against them. Add a new
  row only when at least one of the two backs it, and check every row again for each release. Cite the CIS Benchmark by
  item number only: never copy its text. A row the app cannot read fails the unit tests, which name the row and what was
  refused. Neither source says a service is safe to turn off on a person's own PC; the app says what each is written
  for.
- **Changes**: open an issue first for anything larger than a fix, so we can agree on the approach. Then a pull request
  against `beta`: new work goes there, and `main` only receives releases. Pull requests are squash-merged once CI is
  green.

## Making a change

1. Build and test (the .NET 10 SDK):
   ```
   dotnet build PoolWatch.slnx -c Release
   dotnet test tests/PoolWatch.Tests -c Release
   dotnet test tests/PoolWatch.AppTests -c Release
   ```
   The build must have no warnings.
2. Add a test with the change. Tests never change the real system: no service, scheduled task, registry value or ACL is
   touched and no real process is ended; app code takes its system access through fakes, file tests stay inside their
   own TEMP folder, and a test that needs a security descriptor builds it in memory. Tests never show a window or a
   dialog either, and a test that checks a window's layout runs in the `WholeWindowLayout` collection ("Build and test"
   in AGENTS.md says how).
3. Anything that runs elevated is security-sensitive ("Code that runs elevated" in AGENTS.md has the rules): write and
   delete only through the write policy, never follow a link, never load code or read unbounded input from a folder a
   standard user can write to, and keep every new system change behind an explicit action of the user (a click, or a
   setting they turned on). If you add a new kind of system change, add it to the table in SECURITY.md. The parts listed
   under "Security-sensitive code" in AGENTS.md need a separate security review before they merge, and a change to how
   PoolWatch detects growth follows "Changing detection" there.
4. If you change a package reference, update the lock files on Windows (`dotnet restore --force-evaluate`) and commit
   them: elsewhere the Windows app's lock files come out different, and CI's locked restore fails.
5. The CI runs the end-to-end scenarios on a Windows runner; please do not run `ci/*.ps1` on your own PC: they change
   services, scheduled tasks and the standby list.
6. If users will notice the change, add a line in plain words under "Unreleased" in [CHANGELOG.md](CHANGELOG.md).

By contributing you agree that your contribution is licensed under the [MIT licence](LICENSE).
