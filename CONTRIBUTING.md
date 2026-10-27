# Contributing

Thanks for helping. PoolWatch is a small project with one maintainer, so short, focused changes are easiest to take.

- **Security problems**: never in a public issue or pull request. See [SECURITY.md](SECURITY.md).
- **Bugs**: open an issue with the bug template. Redact what you paste (see "Privacy" in the README): use Export report
  with redaction on, and read it before you attach it.
- **Changes**: open an issue first for anything larger than a fix, so we can agree on the approach. Then a pull request
  against `main`.

## Making a change

1. Build and test (the .NET 10 SDK):
   ```
   dotnet build PoolWatch.slnx -c Release
   dotnet test tests/PoolWatch.Tests -c Release
   dotnet test tests/PoolWatch.AppTests -c Release
   ```
   The build must have no warnings.
2. Add a test with the change. Tests never change the real system: no service, scheduled task, registry value or ACL is
   touched and no real process is ended; app code takes its system access through fakes, and file tests stay inside
   their own TEMP folder.
3. Anything that runs elevated is security-sensitive: write and delete only through the write policy (`PathGuard.CheckReal`),
   never follow a link, never load code or read unbounded input from a folder a standard user can write to, and keep
   every system change behind an explicit action of the user. If you add a new kind of system change, add it to the
   table in SECURITY.md.
4. If you change a package reference, update the lock files (`dotnet restore --force-evaluate`) and commit them.
5. The CI runs the end-to-end scenarios on a Windows runner; please do not run `ci/*.ps1` on your own PC: they change
   services, scheduled tasks and the standby list.

By contributing you agree that your contribution is licensed under the [MIT licence](LICENSE).
