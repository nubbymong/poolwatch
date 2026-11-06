## What and why

<!-- What this changes and the problem it solves. Link the issue ("Fixes #123"). -->

## Tests

<!-- The tests you added or changed, and what you ran. -->

## Checklist

- [ ] `dotnet build PoolWatch.slnx -c Release` has no warnings, and both test projects pass.
- [ ] The change has a test, and no test changes the real PC (fakes for every system change, files only in the test's
      own TEMP folder).
- [ ] No secrets or personal data anywhere in the change, its screenshots or this description: no tokens or passwords,
      user or machine names, home paths, IP addresses, or unredacted incident files.
- [ ] What a user reads is in plain words: no internal setting names, no pool tag without a plain name.
- [ ] A change users will notice has a line under "Unreleased" in CHANGELOG.md.
- [ ] A new kind of change to the PC is listed in SECURITY.md and, if it lasts, recorded so Active changes can put it back.

## Security review

Does this change an elevated action (anything that changes the PC), `src/PoolWatch/Native/`, the update check, the
installer, the release path, or a workflow's triggers or permissions?

- [ ] No
- [ ] Yes: <!-- which, and how --> It needs an independent security review before merge (see AGENTS.md).
