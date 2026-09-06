# Reporting problems

Use [GitHub Issues](https://github.com/Realm667/DoomLauncher667/issues). Search for
an existing report first and include:

- Exact launcher version from the title bar or `VERSION.txt`, including `rc.1`.
- Windows version/architecture; display scaling for layout problems.
- Fresh installation or update, and the previous launcher version if updating.
- Steps to reproduce, expected result and actual result/error text.
- Source-port and Obsidian versions where relevant; a public mod download link
  instead of uploading commercial IWADs or an entire personal library.
- A relevant screenshot, with personal paths/names removed before posting.

You can use `DoomLauncher667-debug.cmd` to reproduce a diagnostic problem.
Failed generation logs are retained under `Data/Temp/Obsidian`. Review logs before
sharing: they can contain local paths and command lines. Never post credentials,
commercial game files, a full database or unrelated personal data.

## Known RC limitation

Some Obsidian-Content v21 builds replace the requested batch seed. The launcher
records the reported effective seed and the discrepancy; an absent effective
seed is marked unverified. Exact requested-seed reproducibility is not promised
for that external build. Obsidian is installed separately and is not bundled.
