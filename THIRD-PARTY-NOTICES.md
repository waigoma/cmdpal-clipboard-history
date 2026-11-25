# Third-Party Notices

This project (clipboard history for PowerToys Command Palette) is distributed under the MIT License (see `LICENSE`).
The package and this repository include or are built with the third-party components below. Each component keeps its own license, listed here.

## Components compiled into or shipped with the package

| Component | Version | License | Copyright notice |
|---|---|---|---|
| .NET runtime and libraries | bundled with SDK 10.0.300 | MIT | Copyright (c) .NET Foundation and Contributors |
| Microsoft.CommandPalette.Extensions | 0.12.260812002 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Windows.CsWinRT (C#/WinRT) | 2.2.0 | MIT | Copyright (c) Microsoft Corporation. |
| Shmuelie.WinRTServer | 2.1.1 | MIT | Copyright (c) 2022 Shmueli Englard |
| Microsoft.Data.Sqlite.Core | 10.0.15 | MIT | © Microsoft Corporation. All rights reserved. |
| SQLitePCLRaw (bundle_winsqlite3, core, provider.winsqlite3) | 2.1.11 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| ZXing.Net | 0.16.11 | Apache-2.0 | (not included in the NuGet package; see the upstream project https://github.com/micjahn/ZXing.Net/) |

Notes:

- The Apache-2.0 components are licensed under the Apache License, Version 2.0. The full text is in `licenses/Apache-2.0.txt`, which is also included in the package.
- SQLite itself is not redistributed. The database uses the `winsqlite3` copy that ships with Windows, so no SQLite binary is part of the package.
- The extension's executable is built with Native AOT. It contains .NET runtime native components (the runtime and the garbage collector, for example) that are covered by the .NET notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

## Project template

The project layout and some files (`app.manifest`, the publish profile, and the COM server start-up code) are based on the Command Palette extension template in the PowerToys project.

- Copyright (c) Microsoft Corporation
- License: MIT

## MIT License

The following license text applies to each component above marked MIT, with that component's copyright notice as listed in the table.

```
MIT License

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
```

## Apache License 2.0

The Apache-2.0 components (SQLitePCLRaw and ZXing.Net) are available under the Apache License, Version 2.0. See `licenses/Apache-2.0.txt`.
