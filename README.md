![banner](RDLC%20baner.png)
# RDLC Without BC: When the Feedback Loop Is Longer Than the Workday
#csharp #dotnet #businesscentral #rdlc #wpf

*Nexus Claude: Every time a developer says "let me check how the report looks" — somewhere in an office, Business Central starts deploying.*

---

## Context

Developing RDLC reports for Business Central looks straightforward: edit the `.rdlc` file in Visual Studio, deploy the extension to BC, run the report, see the result.

The problem is in the word "deploy".

Every change — even nudging a field by two pixels — requires a full cycle: build, deploy, launch BC, navigate to the report, enter parameters. Five minutes at best. Multiply by twenty iterations a day and the workday becomes waiting.

The goal is simple:

*Open RDLC and XML data → see the result. No BC. No deploy. Now.*

---

## Act One: Dead End in Visual Studio

The first thought — Visual Studio Report Designer has a Preview mode. So you can connect XML as a DataSource and view the report right there.

Create a Report Server Project. Open Designer. Try to add a DataSource — Designer won't allow it. Try connecting XML directly through Connection Properties — and get:

```
The XmlDP query is invalid.
Unexpected end of file has occurred.
The following elements are not closed: Column, Columns, DataItem, DataItems, ReportDataSet.
```

Open the `.rdlc` file manually. Inside — a hard lock:

```xml
<DataSources>
  <DataSource Name="DataSource">
    <ConnectionProperties>
      <DataProvider>SQL</DataProvider>
      <ConnectString />
    </ConnectionProperties>
  </DataSource>
</DataSources>
```

`DataProvider=SQL`. Can't change it through the UI. Designer won't allow it.

*Nexus Claude: Microsoft isn't hiding the solution. Microsoft considers this scenario non-existent.*

Official dead end. Moving on.

---

## Act Two: Build It Yourself

If the tool doesn't exist — write it.

The architecture is simple: a WPF application takes two files — `.rdlc` and `.xml` with BC data — renders a PDF via `ReportViewerCore.NETCore` and displays the result through WebView2.

```xml
<PackageReference Include="ReportViewerCore.NETCore" Version="*" />
<PackageReference Include="Microsoft.Web.WebView2" Version="*" />
```

The rendering pipeline is transparent: XML is parsed into a `DataTable`, `DataTable` is passed to `LocalReport`, `LocalReport` renders PDF into a `MemoryStream`, WebView2 displays the bytes.

The only non-trivial part — column type inference. BC delivers everything as strings, but `LocalReport` needs proper types — otherwise numeric fields don't calculate, dates don't sort.

The `InferColumnType` logic walks through all non-empty values in a column:

`int → decimal → DateTime → string`

Dates deserve special attention. BC uses the `dd/MM/yyyy` format. Standard `DateTime.TryParse` with `InvariantCulture` won't recognize it. Explicit formats are required:

```csharp
private static readonly string[] DateFormats =
{
    "dd/MM/yyyy", "dd/MM/yyyy HH:mm:ss",
    "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss"
};

DateTime.TryParseExact(value, DateFormats,
    CultureInfo.InvariantCulture,
    DateTimeStyles.None, out _)
```

Without this, `19/02/2026` silently falls through to `string` — the report works, but date sorting breaks. Silently.

*Nexus Claude: A system that stays quiet when something goes wrong isn't more reliable than one that crashes. It's just harder to diagnose.*

---

## Act Three: The Installer Is Its Own Story

A tool for yourself is one thing. A tool for the team requires an installer.

The choice is obvious — Inno Setup. Free, proven, no dependencies.

### Problem one: the preprocessor

The first version of the script used `#define` for variables:

```pascal
#define AppName "RDLC Report Tester"
#define AppVersion "1.01"
```

Inno Setup Compiler compiles without errors, but the `[Setup]` section is empty — values weren't substituted. Cause: file encoding. `#define` directives aren't read when saved as UTF-8 with BOM in certain configurations.

Fix — remove the preprocessor entirely. Hardcode values directly.

*Result:* ❌ ~20 minutes

### Problem two: DownloadTemporaryFile

The installer needs to check for WebView2 Runtime and download it if missing. First version:

```pascal
if not DownloadTemporaryFile(Url, FileName, '', @DownloadProgress) then
  // error
```

Compiler: type mismatch. `DownloadTemporaryFile` returns `Int64` (byte count), not `Boolean`. On error — it throws an exception. Need `try/except`, not a return value check.

*Result:* ❌ ~15 minutes

### Problem three: PublishSingleFile

The app was built as `PublishSingleFile` — one `.exe`, no dependencies alongside. Logical for distribution.

After installing via the installer, launch from `Program Files` — silence. Event Viewer says:

```
System.DllNotFoundException: Dll was not found.
at MS.Internal.WindowsBase.NativeMethodsSetLastError.SetWindowLongPtrWndProc
at MS.Win32.HwndSubclass.SubclassWndProc
```

`PublishSingleFile` extracts native DLLs into a temp folder next to the exe at runtime. In `Program Files` — no write permissions. WPF crashes before the window initializes. Silently.

Fix — disable `PublishSingleFile` in `.csproj`. The installer packages the entire `publish\` folder into one Setup.exe. The end result is the same — one file to download — but native DLLs sit alongside the exe where they belong.

*Result:* ❌ ~30 minutes

### Problem four: WebView2 and permissions

After the DLL fix the app launches, but crashes again — now with a different error code. WebView2 creates a user data folder next to the exe during initialization. In `Program Files` — no write permissions again.

```csharp
// Before: WebView2 creates folder next to exe
await webView.EnsureCoreWebView2Async();

// After: explicit path in AppData
var env = await CoreWebView2Environment.CreateAsync(
    null,
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RdlcReportTester", "WebView2"));
await webView.EnsureCoreWebView2Async(env);
```

*Result:* ✅

*Nexus Claude: Two crashes in a row from the same root cause — no write permissions. Both silent. A good reminder: "installing in Program Files" isn't just a path — it's a contract with Windows about access rights.*

---

## Result

```
BC XML DataSource → RdlcRendererWpf → PDF Preview
```

Open the app, drag and drop `.rdlc` and `.xml`, see the report. No deployments, no BC sessions.

![RDLC Report Tester v1.01 — PDF preview with test data](RDLC%20Print%20Screen.png)

A careful reader will spot `StackCollider Latvia SIA` in the vendor list. An easter egg. Test data deserves character too.

```xml
<PackageReference Include="ReportViewerCore.NETCore" Version="*" />
<PackageReference Include="Microsoft.Web.WebView2" Version="*" />
```

Full source → [github.com/stackcollider/rdlc-report-tester](https://github.com/stackcollider/rdlc-report-tester)

Article on Dev.to → [RDLC Without BC](https://dev.to/stackcollider/rdlc-without-bc-when-the-feedback-loop-is-longer-than-the-workday-19im)

*Nexus Claude: A tool that speeds up development is code too. Sometimes it's more important to write it than the next report.*