# Third-Party Notices
## Doom Launcher 667 portable distribution

The launcher source is distributed under the repository's GNU GPL v3 license
(see `LICENSE`). Original Doom Launcher attribution is retained in source and
below. Game names, trademarks and game-derived preview artwork remain the
property of their respective owners; the launcher license does not relicense
third-party game assets. Commercial game data is not included.

The WinUI release uses Microsoft .NET/Windows App SDK components,
Microsoft.Data.Sqlite, SQLitePCLRaw/SQLite, SharpCompress and System.Drawing.Common.
Each portable package contains `Licenses/DEPENDENCIES.md`, the restored packages'
metadata and their included license/notice files. This inventory also lists
build-time tooling, which need not be present as executable code in the package.
License terms supplied by Microsoft packages are retained verbatim, rather than
assuming that every Windows App SDK component has the same license.

- SharpCompress: MIT; original license in `Licenses/SharpCompress-LICENSE.txt`.
  Source: https://github.com/adamhathcock/sharpcompress/tree/1d9fd83a73db0144df3d2844133cf1b1f3699c7c
- SQLitePCLRaw: Apache-2.0; Copyright 2014-2024 SourceGear, LLC (package metadata).
  License in `Licenses/SQLitePCLRaw-LICENSE.txt`;
  source: https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.11
- Bundled Titillium Web fonts: SIL Open Font License 1.1; original copyright
  and terms are included in `WinUI/Assets/Fonts/OFL.txt`.
- Self-contained .NET runtime notices are copied from the restored runtime
  package into its versioned directory under `Licenses`.

The older Gameloop.Vdf notice below is retained for the classic source tree;
the current WinUI project does not directly reference that package.

## Optional Obsidian-Content interoperability

Doom Launcher 667 can invoke a user-supplied installation of
[Obsidian-Content](https://github.com/GTD-Carthage/Obsidian-Content) through
its documented batch command-line interface. Obsidian-Content is not bundled,
downloaded, modified or redistributed by Doom Launcher 667. Users who install
it separately remain subject to its own GNU GPL v2-or-later license and notices.

Doom Launcher makes use of third-party assets - these may include libraries, images, icons, fonts, and more. The licenses for these assets are reproduced below, alongside any other information deemed to be relevant to the asset. If we have failed to include a license for a third-party asset, please make us aware so that we can add it to this document.

## Gameloop.Vdf

[GitHub repo](https://github.com/shravan2x/Gameloop.Vdf)

The MIT License (MIT)

Copyright (c) 2016 Shravan Rajinikanth

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
